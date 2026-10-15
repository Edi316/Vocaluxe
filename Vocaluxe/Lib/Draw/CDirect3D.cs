#region license
// This file is part of Vocaluxe.
// 
// Vocaluxe is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Vocaluxe is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Vocaluxe. If not, see <http://www.gnu.org/licenses/>.
#endregion

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SlimDX;
using SlimDX.Direct3D9;
using SlimDX.Windows;
using Vocaluxe.Base;
using VocaluxeLib;
using VocaluxeLib.Draw;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.Draw
{
    class CRenderFormHook : RenderForm, IFormHook
    {
        public MessageEventHandler OnMessage { private get; set; }

        protected override void WndProc(ref Message m)
        {
            if (OnMessage == null || OnMessage(ref m))
            {
                base.WndProc(ref m);
            }
        }
    }

    class CD3DTexture : CTextureBase
    {
        private readonly Device _Device;
        private readonly bool _DefaultPool;

        public Texture D3DTexture { get; private set; }

        /// <summary>
        ///     Y-, U- und V-Ebene (L8) bei Yuv420P-Videotexturen, sonst null
        /// </summary>
        public Texture[] Planes { get; private set; }

        /// <summary>
        ///     True, wenn die Textur im Default-Pool liegt und bei einem Device-Reset neu erzeugt werden muss
        /// </summary>
        public bool IsDefaultPool => _DefaultPool;

        public CD3DTexture(Device device, Size dataSize, int texWidth = 0, int texHeight = 0) : base(dataSize, new Size(texWidth, texHeight))
        {
            var noMips = texWidth >= 1280 || texHeight >= 720;
            var usage = noMips ? Usage.None : Usage.AutoGenerateMipMap;
            var levels = noMips ? 1 : 0;

            D3DTexture = device == null ? null : new Texture(device, texWidth, texHeight, levels, usage, Format.A8R8G8B8, Pool.Managed);
        }

        /// <summary>
        ///     Erzeugt eine Videotextur.
        ///     Bgra: dynamische Textur (Lock mit Discard, keine Kopie im Systemspeicher).
        ///     Yuv420P: Render-Target, das per Pixelshader aus drei dynamischen L8-Ebenen befuellt wird.
        /// </summary>
        public CD3DTexture(Device device, Size dataSize, Size textureSize, EVideoFrameFormat format, bool defaultPool) : base(dataSize, textureSize)
        {
            _Device = device;
            _DefaultPool = defaultPool || format == EVideoFrameFormat.Yuv420P;
            VideoFormat = format;
            CreateVideoResources();
        }

        public void CreateVideoResources()
        {
            if (VideoFormat == null || D3DTexture != null)
            {
                return;
            }

            if (VideoFormat == EVideoFrameFormat.Bgra)
            {
                D3DTexture = _DefaultPool
                    ? new Texture(_Device, Size.Width, Size.Height, 1, Usage.Dynamic, Format.A8R8G8B8, Pool.Default)
                    : new Texture(_Device, Size.Width, Size.Height, 1, Usage.None, Format.A8R8G8B8, Pool.Managed);
                return;
            }

            D3DTexture = new Texture(_Device, Size.Width, Size.Height, 1, Usage.RenderTarget, Format.A8R8G8B8, Pool.Default);
            try
            {
                using (var surface = D3DTexture.GetSurfaceLevel(0))
                {
                    _Device.ColorFill(surface, new Color4(Color.Black));
                }
            }
            catch (Exception)
            {
                // Only cosmetic (black instead of undefined content until the first frame)
            }

            var chromaWidth = (DataSize.Width + 1) / 2;
            var chromaHeight = (DataSize.Height + 1) / 2;
            Planes = new[]
            {
                new Texture(_Device, DataSize.Width, DataSize.Height, 1, Usage.Dynamic, Format.L8, Pool.Default),
                new Texture(_Device, chromaWidth, chromaHeight, 1, Usage.Dynamic, Format.L8, Pool.Default),
                new Texture(_Device, chromaWidth, chromaHeight, 1, Usage.Dynamic, Format.L8, Pool.Default)
            };
        }

        /// <summary>
        ///     Gibt die Ressourcen im Default-Pool frei (vor einem Device-Reset)
        /// </summary>
        public void ReleaseVideoResources()
        {
            if (!_DefaultPool)
            {
                return;
            }

            if (Planes != null)
            {
                foreach (var plane in Planes)
                {
                    plane.Dispose();
                }

                Planes = null;
            }

            if (D3DTexture != null)
            {
                D3DTexture.Dispose();
                D3DTexture = null;
            }
        }

        public override bool IsLoaded
        {
            get { return D3DTexture != null; }
        }

        public override void Dispose()
        {
            base.Dispose();
            if (Planes != null)
            {
                foreach (var plane in Planes)
                {
                    plane.Dispose();
                }

                Planes = null;
            }

            if (D3DTexture != null)
            {
                D3DTexture.Dispose();
                D3DTexture = null;
            }
        }
    }

    class CDirect3D : CDrawBaseWindows<CD3DTexture>, IDraw
    {
        private readonly Direct3D _D3D;
        private readonly Device _Device;
        private readonly PresentParameters _PresentParameters;

        private VertexBuffer _VertexBuffer;
        private IndexBuffer _IndexBuffer;

        private CTextureRef _BlankTexture;

        private readonly Queue<STexturedColoredVertex> _Vertices = new Queue<STexturedColoredVertex>();
        private readonly Queue<Texture> _VerticesTextures = new Queue<Texture>();
        private readonly Queue<Matrix> _VerticesRotationMatrices = new Queue<Matrix>();

        // Videotexturen im Default-Pool, die bei einem Device-Reset freigegeben und neu erzeugt werden muessen
        private readonly List<CD3DTexture> _VideoTextures = new List<CD3DTexture>();
        private PixelShader _YuvShader;
        private bool _DynamicTextures;
        // YUV-Umrechnungen, die vor dem naechsten Frame ausgefuehrt werden (Textur -> Farbmatrix)
        private readonly Dictionary<CD3DTexture, float[]> _PendingYuvConversions = new Dictionary<CD3DTexture, float[]>();
        private bool _VideoErrorLogged;

        private const string _YuvShaderSource = @"
sampler2D texY : register(s0);
sampler2D texU : register(s1);
sampler2D texV : register(s2);
float4 rowR : register(c0);
float4 rowG : register(c1);
float4 rowB : register(c2);

float4 main(float2 uv : TEXCOORD0) : COLOR0
{
    float4 yuv = float4(tex2D(texY, uv).r, tex2D(texU, uv).r, tex2D(texV, uv).r, 1.0);
    return float4(saturate(dot(rowR, yuv)), saturate(dot(rowG, yuv)), saturate(dot(rowB, yuv)), 1.0);
}";

        /// <summary>
        ///     Creates a new Instance of the CDirect3D Class
        /// </summary>
        public CDirect3D()
        {
            _Form = new CRenderFormHook { ClientSize = new Size(CConfig.Config.Graphics.ScreenW * CConfig.Config.Graphics.NumScreens, CConfig.Config.Graphics.ScreenH) };

            try
            {
                _D3D = new Direct3D();
            }
            catch (Direct3DX9NotFoundException e)
            {
                CLog.Error(e, "No DirectX runtimes were found, please download and install them from http://www.microsoft.com/download/en/details.aspx?id=8109");
            }

            _Form.KeyDown += _OnKeyDown;
            _Form.PreviewKeyDown += _OnPreviewKeyDown;
            _Form.KeyPress += _OnKeyPress;
            _Form.KeyUp += _OnKeyUp;

            _Form.MouseMove += _OnMouseMove;
            _Form.MouseWheel += _OnMouseWheel;
            _Form.MouseDown += _OnMouseDown;
            _Form.MouseUp += _OnMouseUp;
            _Form.MouseLeave += _OnMouseLeave;
            _Form.MouseEnter += _OnMouseEnter;

            _PresentParameters = new PresentParameters
            {
                Windowed = true,
                SwapEffect = SwapEffect.Discard,
                BackBufferHeight = CConfig.Config.Graphics.ScreenH,
                BackBufferWidth = CConfig.Config.Graphics.ScreenW * CConfig.Config.Graphics.NumScreens,
                BackBufferFormat = _D3D.Adapters.DefaultAdapter.CurrentDisplayMode.Format,
                Multisample = MultisampleType.None,
                MultisampleQuality = 0
            };

            //Apply antialiasing and check if antialiasing mode is supported

            #region Antialiasing
            int quality;
            MultisampleType msType;
            switch (CConfig.Config.Graphics.AAMode)
            {
                case EAntiAliasingModes.X2:
                    msType = MultisampleType.TwoSamples;
                    break;
                case EAntiAliasingModes.X4:
                    msType = MultisampleType.FourSamples;
                    break;
                case EAntiAliasingModes.X8:
                    msType = MultisampleType.EightSamples;
                    break;
                case EAntiAliasingModes.X16:
                case EAntiAliasingModes.X32: //x32 is not supported, fallback to x16
                    msType = MultisampleType.SixteenSamples;
                    break;
                default:
                    msType = MultisampleType.None;
                    break;
            }

            if (
                !_D3D.CheckDeviceMultisampleType(_D3D.Adapters.DefaultAdapter.Adapter, DeviceType.Hardware, _D3D.Adapters.DefaultAdapter.CurrentDisplayMode.Format, false, msType,
                    out quality))
            {
                CLog.Error("[Direct3D] This AAMode is not supported by this device or driver, fallback to no AA");
                msType = MultisampleType.None;
                quality = 1;
            }

            _PresentParameters.Multisample = msType;
            _PresentParameters.MultisampleQuality = quality - 1;
            #endregion Antialiasing

            //Apply the VSync configuration
            _PresentParameters.PresentationInterval = CConfig.Config.Graphics.VSync == EOffOn.TR_CONFIG_ON ? PresentInterval.Default : PresentInterval.Immediate;

            //GMA 950 graphics devices can only process vertices in software mode
            var caps = _D3D.GetDeviceCaps(_D3D.Adapters.DefaultAdapter.Adapter, DeviceType.Hardware);
            var flags = (caps.DeviceCaps & DeviceCaps.HWTransformAndLight) != 0 ? CreateFlags.HardwareVertexProcessing : CreateFlags.SoftwareVertexProcessing;

            //Check if Pow2 textures are needed
            _NonPowerOf2TextureSupported = true;
            _NonPowerOf2TextureSupported &= (caps.TextureCaps & TextureCaps.Pow2) == 0;
            _NonPowerOf2TextureSupported &= (caps.TextureCaps & TextureCaps.NonPow2Conditional) == 0;
            _NonPowerOf2TextureSupported &= (caps.TextureCaps & TextureCaps.SquareOnly) == 0;

            try
            {
                _Device = new Device(_D3D, _D3D.Adapters.DefaultAdapter.Adapter, DeviceType.Hardware, _Form.Handle, flags, _PresentParameters);
            }
            catch (Exception e)
            {
                CLog.Error(e, "Error during D3D device creation.");
            }
            finally
            {
                if (_Device == null || _Device.Disposed)
                {
                    CLog.Fatal(
                        "Something went wrong during device creating, please check if your DirectX redistributables and grafic card drivers are up to date. You can download the DirectX runtimes at http://www.microsoft.com/download/en/details.aspx?id=8109");
                }
            }
        }

        #region resize
        /// <summary>
        ///     Resizes the viewport
        /// </summary>
        protected override void _DoResize()
        {
            // The window was minimized, so restore it to the last known size
            if (_Form.ClientSize.Width == 0 || _Form.ClientSize.Height == 0)
            {
                _Form.ClientSize = _SizeBeforeMinimize;
            }

            if (_H == _Form.ClientSize.Height && _W == _Form.ClientSize.Width && CConfig.Config.Graphics.ScreenAlignment == _CurrentAlignment)
            {
                return;
            }

            _CurrentAlignment = CConfig.Config.Graphics.ScreenAlignment;
            _H = _Form.ClientSize.Height;
            _W = _Form.ClientSize.Width;

            if (CConfig.Config.Graphics.Stretch != EOffOn.TR_CONFIG_ON)
            {
                _AdjustAspect(false);
            }

            //Apply the new sizes to the PresentParameters
            _PresentParameters.BackBufferWidth = _Form.ClientSize.Width;
            _PresentParameters.BackBufferHeight = _Form.ClientSize.Height;
            if (_Run)
            {
                _ClearScreen();
                //To set new PresentParameters the device has to be resetted
                _Reset();
                //All configurations got flushed due to Reset(), so apply them again
                _InitDevice();
                _Device.Viewport = new Viewport(_X, _Y, _W, _H);
            }

            //Store size so it can get restored after the window gets minimized
            _SizeBeforeMinimize = _Form.ClientSize;
        }

        // ReSharper disable RedundantOverridenMember
        /// <summary>
        ///     Triggers the Fullscreen mode
        /// </summary>
        protected override void _EnterFullScreen()
        {
            //This currently not using real fullscreen mode but a borderless window
            //Real fullscreen could be gained setting _PresentParameters.Windowed = true
            //And calling Reset() and Init() after
            base._EnterFullScreen();
        }

        // ReSharper restore RedundantOverridenMember
        #endregion resize

        #region main stuff
        /// <summary>
        ///     Inits the Device
        /// </summary>
        /// <returns>True if it succeeded else false</returns>
        public override bool Init()
        {
            if (!base.Init())
            {
                return false;
            }

            if (_Device.Disposed)
            {
                return false;
            }

            _InitDevice();
            _InitVideo();

            //This creates a new white texture and adds it to the texture pool
            //This texture is used for the DrawRect method
            using (var blankMap = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(blankMap))
            {
                g.Clear(Color.White);
                _BlankTexture = AddTexture(blankMap);
            }

            return true;
        }

        private void _InitDevice()
        {
            _AdjustNewBorders();

            _VertexBuffer = new VertexBuffer(_Device, CSettings.VertexBufferElements * 4 * Marshal.SizeOf(typeof(STexturedColoredVertex)), Usage.WriteOnly | Usage.Dynamic,
                VertexFormat.Position | VertexFormat.Texture1 | VertexFormat.Diffuse, Pool.Default);

            if (_Device.SetStreamSource(0, _VertexBuffer, 0, Marshal.SizeOf(typeof(STexturedColoredVertex))).IsFailure)
            {
                CLog.Error("Failed to set stream source");
            }

            _Device.VertexDeclaration = STexturedColoredVertex.GetDeclaration(_Device);

            if (_Device.SetRenderState(RenderState.CullMode, Cull.None).IsFailure)
            {
                CLog.Error("Failed to set cull mode");
            }

            if (_Device.SetRenderState(RenderState.AlphaBlendEnable, true).IsFailure)
            {
                CLog.Error("Failed to enable alpha blending");
            }

            if (_Device.SetRenderState(RenderState.Lighting, false).IsFailure)
            {
                CLog.Error("Failed to disable lighting");
            }

            if (_Device.SetRenderState(RenderState.DestinationBlend, Blend.InverseSourceAlpha).IsFailure)
            {
                CLog.Error("Failed to set destination blend");
            }

            if (_Device.SetRenderState(RenderState.SourceBlend, Blend.SourceAlpha).IsFailure)
            {
                CLog.Error("Failed to set source blend");
            }

            if (_PresentParameters.Multisample != MultisampleType.None)
            {
                if (_Device.SetRenderState(RenderState.MultisampleAntialias, true).IsFailure)
                {
                    CLog.Error("Failed to set antialiasing");
                }
            }

            if (_Device.SetSamplerState(0, SamplerState.MinFilter, TextureFilter.Linear).IsFailure)
            {
                CLog.Error("Failed to set min filter");
            }

            if (_Device.SetSamplerState(0, SamplerState.MagFilter, TextureFilter.Linear).IsFailure)
            {
                CLog.Error("Failed to set mag filter");
            }

            if (_Device.SetSamplerState(0, SamplerState.MipFilter, TextureFilter.Linear).IsFailure)
            {
                CLog.Error("Failed to set mip filter");
            }

            if (_Device.SetSamplerState(0, SamplerState.AddressU, TextureAddress.Clamp).IsFailure)
            {
                CLog.Error("Failed to set clamping on u");
            }

            if (_Device.SetSamplerState(0, SamplerState.AddressV, TextureAddress.Clamp).IsFailure)
            {
                CLog.Error("Failed to set claming on v");
            }

            //Sampler 1 und 2 werden nur fuer die U- und V-Ebene von Videos genutzt
            for (var sampler = 1; sampler <= 2; sampler++)
            {
                if (_Device.SetSamplerState(sampler, SamplerState.MinFilter, TextureFilter.Linear).IsFailure ||
                    _Device.SetSamplerState(sampler, SamplerState.MagFilter, TextureFilter.Linear).IsFailure ||
                    _Device.SetSamplerState(sampler, SamplerState.MipFilter, TextureFilter.None).IsFailure ||
                    _Device.SetSamplerState(sampler, SamplerState.AddressU, TextureAddress.Clamp).IsFailure ||
                    _Device.SetSamplerState(sampler, SamplerState.AddressV, TextureAddress.Clamp).IsFailure)
                {
                    CLog.Error("Failed to set sampler states for video planes");
                }
            }

            if (_Device.SetTextureStageState(0, TextureStage.AlphaArg1, TextureArgument.Texture).IsFailure)
            {
                CLog.Error("Failed to set alpha argument 1");
            }

            if (_Device.SetTextureStageState(0, TextureStage.AlphaArg2, TextureArgument.Diffuse).IsFailure)
            {
                CLog.Error("Failed to set alpha argument 2");
            }

            if (_Device.SetTextureStageState(0, TextureStage.AlphaOperation, TextureOperation.Modulate).IsFailure)
            {
                CLog.Error("Failed to set alpha operation");
            }

            var indices = new Int16[6];
            indices[0] = 0;
            indices[1] = 1;
            indices[2] = 2;
            indices[3] = 0;
            indices[4] = 2;
            indices[5] = 3;

            _IndexBuffer = new IndexBuffer(_Device, 6 * sizeof(Int16), Usage.WriteOnly, Pool.Managed, true);

            var stream = _IndexBuffer.Lock(0, 0, LockFlags.Discard);
            stream.WriteRange(indices);
            _IndexBuffer.Unlock();
            _Device.Indices = _IndexBuffer;
        }

        protected override void _OnBeforeDraw()
        {
            _ConvertPendingYuvTextures();

            if (_Device.BeginScene().IsFailure)
            {
                CLog.Error("Failed to begin scene");
            }
        }

        protected override void _OnAfterDraw()
        {
            _RenderVertexBuffer();
            if (_Device.EndScene().IsFailure)
            {
                CLog.Error("Failed to end scene");
            }

            try
            {
                //Now push the frame to the Viewport
                _Device.Present();
            }
            catch (Direct3D9Exception)
            {
                //In Direct3D devices can get lost. 
                //This happens for example when opening the Task Manager in Vista/Windows 7 or if a UAC message is opening
                //We need to reset the device to get it to workable state again
                //After a reset Init() needs to be called because all data in the Direct3D default pool are lost and need to be recreated
                if (_Device.TestCooperativeLevel() == ResultCode.DeviceNotReset)
                {
                    _Reset();
                    _InitDevice();
                }
            }

            Application.DoEvents();
        }

        /// <summary>
        ///     Resets the device, all objects in the Direct3D default pool get flushed and need to be recreated
        /// </summary>
        private void _Reset()
        {
            //Dispose all objects in the default pool, those need to be recreated
            STexturedColoredVertex.GetDeclaration(_Device).Dispose();
            _VertexBuffer.Dispose();
            _IndexBuffer.Dispose();
            _VideoTextures.RemoveAll(t => t.IsDisposed);
            foreach (var texture in _VideoTextures)
            {
                texture.ReleaseVideoResources();
            }

            _PendingYuvConversions.Clear();

            if (_Device.Reset(_PresentParameters).IsFailure)
            {
                CLog.Error("Failed to reset the device");
            }

            //Video textures get their content with the next frame
            foreach (var texture in _VideoTextures)
            {
                try
                {
                    texture.CreateVideoResources();
                }
                catch (Exception e)
                {
                    CLog.Error(e, "Failed to recreate video texture after device reset");
                }
            }
        }

        protected override void _AdjustNewBorders()
        {
            const float dx = (float)CSettings.RenderW / 2;
            const float dy = (float)CSettings.RenderH / 2;
            var translate = Matrix.Translation(new Vector3(-dx, dy, 0));
            var projection = Matrix.OrthoOffCenterLH(
                -dx - _BorderLeft, CSettings.RenderW * CConfig.Config.Graphics.NumScreens - dx + _BorderRight,
                -dy - _BorderBottom, dy + _BorderTop,
                CSettings.ZNear, CSettings.ZFar);

            if (_Device.SetTransform(TransformState.Projection, projection).IsFailure)
            {
                CLog.Error("Failed to set orthogonal matrix");
            }

            if (_Device.SetTransform(TransformState.World, translate).IsFailure)
            {
                CLog.Error("Failed to set translation matrix");
            }
        }

        /// <summary>
        ///     Unloads all Textures and other objects used by Direct3D for rendering
        /// </summary>
        /// <returns></returns>
        public override void Close()
        {
            base.Close();
            _VideoTextures.Clear();
            _PendingYuvConversions.Clear();
            _YuvShader?.Dispose();
            _YuvShader = null;
            STexturedColoredVertex.DisposeDeclaration();
            _VertexBuffer.Dispose();
            _IndexBuffer.Dispose();
            _Device.Dispose();
            _D3D.Dispose();
        }

        /// <summary>
        ///     Gets the current viewport width
        /// </summary>
        /// <returns>The current viewport width</returns>
        public int GetScreenWidth()
        {
            return _Device.Viewport.Width;
        }

        /// <summary>
        ///     Gets the current viewport height
        /// </summary>
        /// <returns>The current viewport height</returns>
        public int GetScreenHeight()
        {
            return _Device.Viewport.Height;
        }

        /// <summary>
        ///     Adds a texture to the vertext buffer
        /// </summary>
        /// <param name="texture">Texture to draw</param>
        /// <param name="dc">Coordinates to draw on</param>
        /// <param name="color">Color to use</param>
        /// <param name="isReflection">If true, then color is faded out in y direction</param>
        protected override void _DrawTexture(CD3DTexture texture, SDrawCoords dc, SColorF color, bool isReflection = false)
        {
            //Align the pixels because Direct3D expects the pixels to be the left top corner
            dc.Wx1 -= 0.5f;
            dc.Wy1 -= 0.5f;
            dc.Wx2 -= 0.5f;
            dc.Wy2 -= 0.5f;

            color.A *= CGraphics.GlobalAlpha;
            var c = color.AsColor().ToArgb();
            int c2;
            if (isReflection)
            {
                color.A = 0;
                c2 = color.AsColor().ToArgb();
            }
            else
            {
                c2 = c;
            }

            var vert = new STexturedColoredVertex[4];
            vert[0] = new STexturedColoredVertex(new Vector3(dc.Wx1, -dc.Wy1, dc.Wz), new Vector2(dc.Tx1, dc.Ty1), c);
            vert[1] = new STexturedColoredVertex(new Vector3(dc.Wx1, -dc.Wy2, dc.Wz), new Vector2(dc.Tx1, dc.Ty2), c2);
            vert[2] = new STexturedColoredVertex(new Vector3(dc.Wx2, -dc.Wy2, dc.Wz), new Vector2(dc.Tx2, dc.Ty2), c2);
            vert[3] = new STexturedColoredVertex(new Vector3(dc.Wx2, -dc.Wy1, dc.Wz), new Vector2(dc.Tx2, dc.Ty1), c);
            _AddToVertexBuffer(vert, texture.D3DTexture, _CalculateRotationMatrix(dc.Rotation, dc.Wx1, dc.Wx2, dc.Wy1, dc.Wy2));
        }

        /// <summary>
        ///     Adds a quad a list which will be added and rendered to the vertexbuffer when calling RenderToVertexBuffer to reduce vertexbuffer calls each frame to a minimum
        /// </summary>
        /// <param name="vertices">A TexturedColoredVertex array containg 4 vertices</param>
        /// <param name="tex">The texture the vertex should be textured with</param>
        /// <param name="rotation">The vertices' rotation</param>
        private void _AddToVertexBuffer(STexturedColoredVertex[] vertices, Texture tex, Matrix rotation)
        {
            //The vertexbuffer is full, so we need to flush it before we can continue
            if (_Vertices.Count >= CSettings.VertexBufferElements)
            {
                _RenderVertexBuffer();
            }

            _Vertices.Enqueue(vertices[0]);
            _Vertices.Enqueue(vertices[1]);
            _Vertices.Enqueue(vertices[2]);
            _Vertices.Enqueue(vertices[3]);
            _VerticesTextures.Enqueue(tex);
            _VerticesRotationMatrices.Enqueue(rotation);
        }

        /// <summary>
        ///     Renders the vertex buffer
        /// </summary>
        private void _RenderVertexBuffer()
        {
            if (_Vertices.Count <= 0)
            {
                return;
            }

            //The vertex buffer locks are slow actions, its better to lock once per frame and write all vertices to the buffer at once
            var stream = _VertexBuffer.Lock(0, _Vertices.Count * Marshal.SizeOf(typeof(STexturedColoredVertex)), LockFlags.Discard);
            stream.WriteRange(_Vertices.ToArray());
            _VertexBuffer.Unlock();
            stream.Dispose();

            for (var i = 0; i < _Vertices.Count; i += 4)
            {
                //Apply rotation
                if (_Device.SetTransform(TransformState.World, _VerticesRotationMatrices.Dequeue()).IsFailure)
                {
                    CLog.Error("Failed to set world transformation");
                }

                //Apply texture
                if (_Device.SetTexture(0, _VerticesTextures.Dequeue()).IsFailure)
                {
                    CLog.Error("Failed to set texture");
                }

                //Draw 2 triangles from vertexbuffer
                if (_Device.DrawIndexedPrimitives(PrimitiveType.TriangleList, i, 0, 4, 0, 2).IsFailure)
                {
                    CLog.Error("Failed to draw quad");
                }
            }

            //Clear the queues for the next frame
            _Vertices.Clear();
            _VerticesTextures.Clear();
            _VerticesRotationMatrices.Clear();
        }
        #endregion main stuff

        /// <summary>
        ///     Removes all textures from the screen
        /// </summary>
        protected override void _ClearScreen()
        {
            if (_Device.Clear(ClearFlags.Target | ClearFlags.ZBuffer, Color.Black, 1.0f, 0).IsFailure)
            {
                CLog.Error("Failed to clear the backbuffer");
            }
        }

        /// <summary>
        ///     Copies the current frame into a texture
        ///     <returns>A texture holding the current frame</returns>
        /// </summary>
        public CTextureRef CopyScreen()
        {
            var tex = _CreateTexture(new Size(_W, _H));
            var backbufferSurface = _Device.GetBackBuffer(0, 0);
            var textureSurface = tex.D3DTexture.GetSurfaceLevel(0);
            Surface.FromSurface(textureSurface, backbufferSurface, Filter.Default, 0, new Rectangle(0, 0, _W, _H), new Rectangle(0, 0, _W, _H));
            backbufferSurface.Dispose();

            return _GetTextureReference(_W, _H, tex);
        }

        /// <summary>
        ///     Copies the current frame into a texture
        /// </summary>
        /// <param name="textureRef">The texture in which the frame is copied to</param>
        public void CopyScreen(ref CTextureRef textureRef)
        {
            CD3DTexture texture;
            if (!_GetTexture(textureRef, out texture) || texture.DataSize.Width != GetScreenWidth() || texture.DataSize.Height != GetScreenHeight())
            {
                RemoveTexture(ref textureRef);
                textureRef = CopyScreen();
            }
            else
            {
                var backbufferSurface = _Device.GetBackBuffer(0, 0);
                var textureSurface = texture.D3DTexture.GetSurfaceLevel(0);
                Surface.FromSurface(textureSurface, backbufferSurface, Filter.Default, 0);
                backbufferSurface.Dispose();
            }
        }

        /// <summary>
        ///     Creates a Screenshot of the current frame
        /// </summary>
        public void MakeScreenShot()
        {
            var file = CHelper.GetUniqueFileName(Path.Combine(CSettings.DataFolder, CSettings.FolderNameScreenshots), "Screenshot.png");

            //create a surface of the frame
            using (var surface = _Device.GetBackBuffer(0, 0))
            {
                var screen = new Bitmap(Surface.ToStream(surface, ImageFileFormat.Png));
                screen.Save(file, ImageFormat.Png);
                screen.Dispose();
            }
        }

        /// <summary>
        ///     Draws a colored rectangle
        /// </summary>
        /// <param name="color">The color in which the rectangle will be drawn in</param>
        /// <param name="rect">The coordinates in a SRectF struct</param>
        public void DrawRect(SColorF color, SRectF rect, bool allMonitors = true)
        {
            DrawTexture(_BlankTexture, rect, color, false, allMonitors);
        }

        /// <summary>
        ///     Draws reflection of a colored rectangle
        /// </summary>
        /// <param name="color">The color in which the rectangle will be drawn in</param>
        /// <param name="rect">The coordinates in a SRectF struct</param>
        /// <param name="space">The space between the texture and the reflection</param>
        /// <param name="height">The height of the reflection</param>
        public void DrawRectReflection(SColorF color, SRectF rect, float space, float height)
        {
            DrawTextureReflection(_BlankTexture, rect, color, rect, space, height);
        }

        protected override CD3DTexture _CreateTexture(Size dataSize)
        {
            if (dataSize.Width < 0)
            {
                return new CD3DTexture(null, dataSize);
            }

            return new CD3DTexture(_Device, dataSize, _CheckForNextPowerOf2(dataSize.Width), _CheckForNextPowerOf2(dataSize.Height));
        }

        protected override void _WriteDataToTexture(CD3DTexture texture, byte[] data)
        {
            var rect = texture.D3DTexture.LockRectangle(0, LockFlags.Discard);
            try
            {
                var rowWidth = 4 * texture.DataSize.Width;
                var dst = rect.Data.DataPointer;
                if (rowWidth == rect.Pitch)
                    Marshal.Copy(data, 0, dst, data.Length);
                else
                {
                    var rows = data.Length / rowWidth;
                    for (var y = 0; y < rows; y++)
                        Marshal.Copy(data, y * rowWidth, IntPtr.Add(dst, y * rect.Pitch), rowWidth);
                }
            }
            finally { texture.D3DTexture.UnlockRectangle(0); }
        }

        #region video
        /// <summary>
        ///     Prueft, welche Videopfade die Grafikkarte unterstuetzt und kompiliert den YUV-Pixelshader
        /// </summary>
        private void _InitVideo()
        {
            var caps = _Device.Capabilities;
            var adapter = _D3D.Adapters.DefaultAdapter.Adapter;
            var adapterFormat = _D3D.Adapters.DefaultAdapter.CurrentDisplayMode.Format;

            _DynamicTextures = (caps.Caps2 & Caps2.DynamicTextures) != 0 &&
                               _D3D.CheckDeviceFormat(adapter, DeviceType.Hardware, adapterFormat, Usage.Dynamic, ResourceType.Texture, Format.A8R8G8B8);

            if (CConfig.Config.FFmpeg.GpuColorConversion != EOffOn.TR_CONFIG_ON)
            {
                CLog.Information("[Direct3D] GPU color conversion for videos disabled by config");
                return;
            }

            var yuvSupported = _DynamicTextures && _NonPowerOf2TextureSupported && caps.PixelShaderVersion >= new Version(2, 0) &&
                               _D3D.CheckDeviceFormat(adapter, DeviceType.Hardware, adapterFormat, Usage.Dynamic, ResourceType.Texture, Format.L8) &&
                               _D3D.CheckDeviceFormat(adapter, DeviceType.Hardware, adapterFormat, Usage.RenderTarget, ResourceType.Texture, Format.A8R8G8B8);
            if (!yuvSupported)
            {
                CLog.Information("[Direct3D] Device does not support GPU color conversion for videos, using CPU conversion");
                return;
            }

            try
            {
                using (var bytecode = ShaderBytecode.Compile(_YuvShaderSource, "main", "ps_2_0", ShaderFlags.None))
                {
                    _YuvShader = new PixelShader(_Device, bytecode);
                }
            }
            catch (Exception e)
            {
                CLog.Error(e, "[Direct3D] Unable to compile the YUV pixel shader, using CPU conversion for videos");
                _YuvShader = null;
            }
        }

        public override bool SupportsYuvVideo => _YuvShader != null;

        protected override CD3DTexture _CreateVideoTexture(Size size, EVideoFrameFormat format)
        {
            var textureSize = format == EVideoFrameFormat.Yuv420P
                ? size
                : new Size(_CheckForNextPowerOf2(size.Width), _CheckForNextPowerOf2(size.Height));
            var texture = new CD3DTexture(_Device, size, textureSize, format, _DynamicTextures);
            if (texture.IsDefaultPool)
            {
                _VideoTextures.RemoveAll(t => t.IsDisposed);
                _VideoTextures.Add(texture);
            }

            return texture;
        }

        protected override void _WriteVideoFrameToTexture(CD3DTexture texture, in SVideoFrame frame)
        {
            if (!texture.IsLoaded)
            {
                return; // Device was lost and resources could not be recreated yet
            }

            try
            {
                if (frame.Format == EVideoFrameFormat.Bgra)
                {
                    _WritePlane(texture.D3DTexture, frame, 0, !texture.Size.Equals(texture.DataSize));
                    return;
                }

                for (var i = 0; i < 3; i++)
                {
                    _WritePlane(texture.Planes[i], frame, i, false);
                }

                // Die Umrechnung (Wechsel des Render-Targets) erfolgt erst vor dem naechsten Frame. Mitten in einem Frame
                // verlieren manche D3D9-Treiber (z.B. in VMs) dabei den Inhalt des Backbuffers im Bereich der Textur.
                _PendingYuvConversions[texture] = CVideoFrameHelper.GetYuvToRgbRows(frame.ColorMatrix, frame.FullRange);
            }
            catch (Exception e)
            {
                // Can happen while the device is lost, the texture is updated again with the next frame
                if (!_VideoErrorLogged)
                {
                    CLog.Error(e, "[Direct3D] Failed to update video texture");
                    _VideoErrorLogged = true;
                }
            }
        }

        private static void _WritePlane(Texture target, in SVideoFrame frame, int plane, bool clear)
        {
            var rect = target.LockRectangle(0, LockFlags.Discard);
            try
            {
                var dst = rect.Data.DataPointer;
                if (clear)
                {
                    // Textur ist groesser als das Video (Pow2): Rest einmal leeren, damit beim Filtern kein Muell an den Raendern auftaucht
                    var description = target.GetLevelDescription(0);
                    var size = (long)rect.Pitch * description.Height;
                    unsafe
                    {
                        var p = (byte*)dst;
                        for (long i = 0; i < size; i++)
                        {
                            p[i] = 0;
                        }
                    }
                }

                CVideoFrameHelper.CopyPlane(frame.GetPlane(plane), frame.GetStride(plane), dst, rect.Pitch, frame.GetPlaneRowBytes(plane), frame.GetPlaneHeight(plane));
            }
            finally
            {
                target.UnlockRectangle(0);
            }
        }

        /// <summary>
        ///     Rechnet alle seit dem letzten Frame hochgeladenen YUV-Ebenen per Pixelshader in ihre RGB-Texturen um
        ///     (Render-to-Texture). Laeuft vor dem Frame in einer eigenen Szene, damit der Wechsel des Render-Targets
        ///     nichts beeinflusst, was bereits in den Backbuffer gezeichnet wurde.
        /// </summary>
        private void _ConvertPendingYuvTextures()
        {
            if (_PendingYuvConversions.Count == 0)
            {
                return;
            }

            try
            {
                _Device.BeginScene();
                var viewport = _Device.Viewport;
                try
                {
                    using (var oldTarget = _Device.GetRenderTarget(0))
                    {
                        try
                        {
                            _Device.SetRenderState(RenderState.AlphaBlendEnable, false);
                            _Device.PixelShader = _YuvShader;
                            _Device.VertexFormat = VertexFormat.PositionRhw | VertexFormat.Texture1;
                            foreach (var conversion in _PendingYuvConversions)
                            {
                                var texture = conversion.Key;
                                if (!texture.IsDisposed && texture.IsLoaded && texture.Planes != null)
                                {
                                    _ConvertYuvToTexture(texture, conversion.Value);
                                }
                            }
                        }
                        finally
                        {
                            _Device.SetRenderTarget(0, oldTarget);
                        }
                    }
                }
                finally
                {
                    // Restore the state used by the normal drawing code
                    _Device.PixelShader = null;
                    _Device.SetTexture(0, null);
                    _Device.SetTexture(1, null);
                    _Device.SetTexture(2, null);
                    _Device.SetRenderState(RenderState.AlphaBlendEnable, true);
                    _Device.VertexDeclaration = STexturedColoredVertex.GetDeclaration(_Device);
                    _Device.SetStreamSource(0, _VertexBuffer, 0, Marshal.SizeOf(typeof(STexturedColoredVertex)));
                    _Device.Viewport = viewport;
                    _Device.EndScene();
                }
            }
            catch (Exception e)
            {
                // Can happen while the device is lost, the textures are updated again with the next video frame
                if (!_VideoErrorLogged)
                {
                    CLog.Error(e, "[Direct3D] Failed to convert video frames");
                    _VideoErrorLogged = true;
                }
            }
            finally
            {
                _PendingYuvConversions.Clear();
            }
        }

        private void _ConvertYuvToTexture(CD3DTexture texture, float[] colorRows)
        {
            var w = texture.DataSize.Width - 0.5f;
            var h = texture.DataSize.Height - 0.5f;
            var quad = new[]
            {
                new SRhwTexturedVertex(new Vector4(-0.5f, -0.5f, 0f, 1f), new Vector2(0f, 0f)),
                new SRhwTexturedVertex(new Vector4(w, -0.5f, 0f, 1f), new Vector2(1f, 0f)),
                new SRhwTexturedVertex(new Vector4(-0.5f, h, 0f, 1f), new Vector2(0f, 1f)),
                new SRhwTexturedVertex(new Vector4(w, h, 0f, 1f), new Vector2(1f, 1f))
            };

            using (var surface = texture.D3DTexture.GetSurfaceLevel(0))
            {
                _Device.SetRenderTarget(0, surface);
                _Device.SetPixelShaderConstant(0, colorRows);
                _Device.SetTexture(0, texture.Planes[0]);
                _Device.SetTexture(1, texture.Planes[1]);
                _Device.SetTexture(2, texture.Planes[2]);
                _Device.DrawUserPrimitives(PrimitiveType.TriangleStrip, 2, quad);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SRhwTexturedVertex
        {
            // ReSharper disable NotAccessedField.Local
            private Vector4 _Position;
            private Vector2 _Texture;
            // ReSharper restore NotAccessedField.Local

            public SRhwTexturedVertex(Vector4 position, Vector2 texture)
            {
                _Position = position;
                _Texture = texture;
            }
        }
        #endregion video

        private static Matrix _CalculateRotationMatrix(float rot, float rx1, float rx2, float ry1, float ry2)
        {
            var originTranslation = Matrix.Translation(new Vector3(-(float)CSettings.RenderW / 2, (float)CSettings.RenderH / 2, 0));
            if (Math.Abs(rot) > float.Epsilon)
            {
                var rotation = rot * (float)Math.PI / 180;
                var centerX = (rx1 + rx2) / 2f;
                var centerY = -(ry1 + ry2) / 2f;

                var translationA = Matrix.Translation(-centerX, -centerY, 0);
                var rotationMat = Matrix.RotationZ(-rotation);
                var translationB = Matrix.Translation(centerX, centerY, 0);

                //Multiplicate the matrices to get the real world matrix,
                //First shift the texture into the center
                //Rotate it and shift it back to the origin position
                //Apply the originTranslation after
                var result = translationA * rotationMat * translationB * originTranslation;
                return result;
            }

            return originTranslation;
        }

        private struct STexturedColoredVertex
        {
            private static VertexDeclaration _Declaration;
            private static readonly VertexElement[] _Elements =
            {
                new VertexElement(0, 0, DeclarationType.Float3, DeclarationMethod.Default, DeclarationUsage.Position, 0),
                new VertexElement(0, sizeof(float) * 3, DeclarationType.Float2, DeclarationMethod.Default, DeclarationUsage.TextureCoordinate, 0),
                new VertexElement(0, sizeof(float) * 3 + sizeof(float) * 2, DeclarationType.Color, DeclarationMethod.Default, DeclarationUsage.Color, 0),
                VertexElement.VertexDeclarationEnd
            };

            // ReSharper disable NotAccessedField.Local
            private Vector3 _Position;
            private Vector2 _Texture;
            private int _Color;
            // ReSharper restore NotAccessedField.Local

            public STexturedColoredVertex(Vector3 position, Vector2 texture, int color)
            {
                _Position = position;
                _Texture = texture;
                _Color = color;
            }

            public static VertexDeclaration GetDeclaration(Device device)
            {
                if (_Declaration == null || _Declaration.Disposed)
                {
                    _Declaration = new VertexDeclaration(device, _Elements);
                }

                return _Declaration;
            }

            public static void DisposeDeclaration()
            {
                if (_Declaration != null && !_Declaration.Disposed)
                {
                    _Declaration.Dispose();
                    _Declaration = null;
                }
            }
        }
    }
}
