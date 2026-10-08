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
using SharpDX.Direct3D9;
using Vocaluxe.Base;
using VocaluxeLib;
using VocaluxeLib.Draw;
using VocaluxeLib.Log;
using Matrix = SharpDX.Matrix;
using Vector2 = SharpDX.Vector2;
using Vector3 = SharpDX.Vector3;
using ColorBGRA = SharpDX.Mathematics.Interop.RawColorBGRA;

namespace Vocaluxe.Lib.Draw
{
    class CRenderFormHook : Form, IFormHook
    {
        public CRenderFormHook()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.UserPaint, true);
        }

        public MessageEventHandler OnMessage { private get; set; }

        protected override void WndProc(ref Message m)
        {
            if (OnMessage == null || OnMessage(ref m))
                base.WndProc(ref m);
        }
    }

    class CD3DTexture : CTextureBase
    {
        public readonly Texture D3DTexture;

        public CD3DTexture(Device device, Size dataSize, int texWidth = 0, int texHeight = 0)
            : base(dataSize, new Size(texWidth, texHeight))
        {
            //Managed pool: keeps a copy in RAM, survives a lost device
            D3DTexture = device == null ? null : new Texture(device, texWidth, texHeight, 0, Usage.AutoGenerateMipMap, Format.A8R8G8B8, Pool.Managed);
        }

        public override bool IsLoaded
        {
            get { return D3DTexture != null; }
        }

        public override void Dispose()
        {
            base.Dispose();
            if (D3DTexture != null)
                D3DTexture.Dispose();
        }
    }

    class CDirect3D : CDrawBaseWindows<CD3DTexture>, IDraw
    {
        private readonly Direct3D _D3D;
        private Device _Device;
        //PresentParameters ist in SharpDX ein struct -> nicht readonly
        private PresentParameters _PresentParameters;

        private VertexBuffer _VertexBuffer;
        private IndexBuffer _IndexBuffer;

        private CTextureRef _BlankTexture;

        private readonly Queue<STexturedColoredVertex> _Vertices = new Queue<STexturedColoredVertex>();
        private readonly Queue<Texture> _VerticesTextures = new Queue<Texture>();
        private readonly Queue<Matrix> _VerticesRotationMatrices = new Queue<Matrix>();

        private static bool _IsDeviceLost(SharpDX.SharpDXException e)
        {
            var code = e.ResultCode.Code;
            return code == ResultCode.DeviceLost.Result.Code || code == ResultCode.DeviceNotReset.Result.Code;
        }

        public CDirect3D()
        {
            _Form = new CRenderFormHook { ClientSize = new Size(CConfig.Config.Graphics.ScreenW * CConfig.Config.Graphics.NumScreens, CConfig.Config.Graphics.ScreenH) };

            _D3D = new Direct3D(); // nutzt nur d3d9.dll aus Windows, kein D3DX

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

            var adapter = _D3D.Adapters[0];
            _PresentParameters = new PresentParameters
            {
                Windowed = true,
                SwapEffect = SwapEffect.Discard,
                DeviceWindowHandle = _Form.Handle,
                BackBufferHeight = CConfig.Config.Graphics.ScreenH,
                BackBufferWidth = CConfig.Config.Graphics.ScreenW * CConfig.Config.Graphics.NumScreens,
                BackBufferFormat = adapter.CurrentDisplayMode.Format,
                MultiSampleType = MultisampleType.None,
                MultiSampleQuality = 0
            };

            #region Antialiasing
            MultisampleType msType;
            switch (CConfig.Config.Graphics.AAMode)
            {
                case EAntiAliasingModes.X2: msType = MultisampleType.TwoSamples; break;
                case EAntiAliasingModes.X4: msType = MultisampleType.FourSamples; break;
                case EAntiAliasingModes.X8: msType = MultisampleType.EightSamples; break;
                case EAntiAliasingModes.X16:
                case EAntiAliasingModes.X32: //x32 is not supported, fallback to x16
                    msType = MultisampleType.SixteenSamples; break;
                default: msType = MultisampleType.None; break;
            }

            int quality;
            if (!_D3D.CheckDeviceMultisampleType(0, DeviceType.Hardware, adapter.CurrentDisplayMode.Format, false, msType, out quality))
            {
                CLog.Error("[Direct3D] This AAMode is not supported by this device or driver, fallback to no AA");
                msType = MultisampleType.None;
                quality = 1;
            }
            _PresentParameters.MultiSampleType = msType;
            _PresentParameters.MultiSampleQuality = quality - 1;
            #endregion Antialiasing

            _PresentParameters.PresentationInterval = CConfig.Config.Graphics.VSync == EOffOn.TR_CONFIG_ON ? PresentInterval.Default : PresentInterval.Immediate;

            //GMA 950 graphics devices can only process vertices in software mode
            var caps = _D3D.GetDeviceCaps(0, DeviceType.Hardware);
            var flags = (caps.DeviceCaps & DeviceCaps.HWTransformAndLight) != 0 ? CreateFlags.HardwareVertexProcessing : CreateFlags.SoftwareVertexProcessing;

            //Check if Pow2 textures are needed
            _NonPowerOf2TextureSupported = true;
            _NonPowerOf2TextureSupported &= (caps.TextureCaps & TextureCaps.Pow2) == 0;
            _NonPowerOf2TextureSupported &= (caps.TextureCaps & TextureCaps.NonPow2Conditional) == 0;
            _NonPowerOf2TextureSupported &= (caps.TextureCaps & TextureCaps.SquareOnly) == 0;

            try
            {
                _Device = new Device(_D3D, 0, DeviceType.Hardware, _Form.Handle, flags, _PresentParameters);
            }
            catch (Exception e)
            {
                CLog.Error(e, "Error during D3D device creation.");
            }
            finally
            {
                if (_Device == null || _Device.IsDisposed)
                    CLog.Fatal("Something went wrong during device creating, please check if your graphics card drivers are up to date.");
            }
        }

        #region resize
        protected override void _DoResize()
        {
            // The window was minimized, so restore it to the last known size
            if (_Form.ClientSize.Width == 0 || _Form.ClientSize.Height == 0)
                _Form.ClientSize = _SizeBeforeMinimize;

            if (_H == _Form.ClientSize.Height && _W == _Form.ClientSize.Width && CConfig.Config.Graphics.ScreenAlignment == _CurrentAlignment)
                return;

            _CurrentAlignment = CConfig.Config.Graphics.ScreenAlignment;
            _H = _Form.ClientSize.Height;
            _W = _Form.ClientSize.Width;

            if (CConfig.Config.Graphics.Stretch != EOffOn.TR_CONFIG_ON)
                _AdjustAspect(false);

            _PresentParameters.BackBufferWidth = _Form.ClientSize.Width;
            _PresentParameters.BackBufferHeight = _Form.ClientSize.Height;
            if (_Run)
            {
                _ClearScreen();
                _Reset();
                _InitDevice();
                var vp = _Device.Viewport;
                vp.X = _X;
                vp.Y = _Y;
                vp.Width = _W;
                vp.Height = _H;
                _Device.Viewport = vp;
            }
            _SizeBeforeMinimize = _Form.ClientSize;
        }

        // ReSharper disable RedundantOverridenMember
        protected override void _EnterFullScreen()
        {
            //Borderless window instead of real fullscreen
            base._EnterFullScreen();
        }
        // ReSharper restore RedundantOverridenMember
        #endregion resize

        #region main stuff
        public override bool Init()
        {
            if (!base.Init())
                return false;

            if (_Device == null || _Device.IsDisposed)
                return false;

            _InitDevice();

            //White 1x1 texture used by DrawRect
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

            var stride = Marshal.SizeOf(typeof(STexturedColoredVertex));
            _VertexBuffer = new VertexBuffer(_Device, CSettings.VertexBufferElements * 4 * stride, Usage.WriteOnly | Usage.Dynamic,
                                             VertexFormat.Position | VertexFormat.Texture1 | VertexFormat.Diffuse, Pool.Default);

            try
            {
                _Device.SetStreamSource(0, _VertexBuffer, 0, stride);
                _Device.VertexDeclaration = STexturedColoredVertex.GetDeclaration(_Device);

                _Device.SetRenderState(RenderState.CullMode, Cull.None);
                _Device.SetRenderState(RenderState.AlphaBlendEnable, true);
                _Device.SetRenderState(RenderState.Lighting, false);
                _Device.SetRenderState(RenderState.DestinationBlend, Blend.InverseSourceAlpha);
                _Device.SetRenderState(RenderState.SourceBlend, Blend.SourceAlpha);

                if (_PresentParameters.MultiSampleType != MultisampleType.None)
                    _Device.SetRenderState(RenderState.MultisampleAntialias, true);

                _Device.SetSamplerState(0, SamplerState.MinFilter, TextureFilter.Linear);
                _Device.SetSamplerState(0, SamplerState.MagFilter, TextureFilter.Linear);
                _Device.SetSamplerState(0, SamplerState.MipFilter, TextureFilter.Linear);
                _Device.SetSamplerState(0, SamplerState.AddressU, TextureAddress.Clamp);
                _Device.SetSamplerState(0, SamplerState.AddressV, TextureAddress.Clamp);

                _Device.SetTextureStageState(0, TextureStage.AlphaArg1, TextureArgument.Texture);
                _Device.SetTextureStageState(0, TextureStage.AlphaArg2, TextureArgument.Diffuse);
                _Device.SetTextureStageState(0, TextureStage.AlphaOperation, TextureOperation.Modulate);
            }
            catch (SharpDX.SharpDXException e)
            {
                CLog.Error(e, "Failed to set device states");
            }

            var indices = new short[] { 0, 1, 2, 0, 2, 3 };
            _IndexBuffer = new IndexBuffer(_Device, 6 * sizeof(short), Usage.WriteOnly, Pool.Managed, true);
            using (var stream = _IndexBuffer.Lock(0, 0, LockFlags.None))
            {
                stream.WriteRange(indices);
            }
            _IndexBuffer.Unlock();
            _Device.Indices = _IndexBuffer;
        }

        protected override void _OnBeforeDraw()
        {
            try
            {
                _Device.BeginScene();
            }
            catch (SharpDX.SharpDXException e)
            {
                if (!_IsDeviceLost(e))
                    CLog.Error(e, "Failed to begin scene");
            }
        }

        protected override void _OnAfterDraw()
        {
            _RenderVertexBuffer();
            try
            {
                _Device.EndScene();
            }
            catch (SharpDX.SharpDXException e)
            {
                if (!_IsDeviceLost(e))
                    CLog.Error(e, "Failed to end scene");
            }

            try
            {
                _Device.Present();
            }
            catch (SharpDX.SharpDXException)
            {
                //Devices can get lost (Task Manager, UAC, ...). Reset and recreate default-pool objects.
                if (_Device.TestCooperativeLevel().Code == ResultCode.DeviceNotReset.Result.Code)
                {
                    _Reset();
                    _InitDevice();
                }
            }
            Application.DoEvents();
        }

        /// <summary>
        /// Resets the device, all objects in the Direct3D default pool get flushed and need to be recreated
        /// </summary>
        private void _Reset()
        {
            STexturedColoredVertex.DisposeDeclaration();
            if (_VertexBuffer != null)
                _VertexBuffer.Dispose();
            if (_IndexBuffer != null)
                _IndexBuffer.Dispose();
            try
            {
                _Device.Reset(_PresentParameters);
            }
            catch (SharpDX.SharpDXException e)
            {
                CLog.Error(e, "Failed to reset the device");
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

            try
            {
                _Device.SetTransform(TransformState.Projection, ref projection);
                _Device.SetTransform(TransformState.World, ref translate);
            }
            catch (SharpDX.SharpDXException e)
            {
                CLog.Error(e, "Failed to set transformation matrices");
            }
        }

        public override void Close()
        {
            base.Close();
            STexturedColoredVertex.DisposeDeclaration();
            _VertexBuffer.Dispose();
            _IndexBuffer.Dispose();
            _Device.Dispose();
            _D3D.Dispose();
        }

        public int GetScreenWidth()
        {
            return _Device.Viewport.Width;
        }

        public int GetScreenHeight()
        {
            return _Device.Viewport.Height;
        }

        protected override void _DrawTexture(CD3DTexture texture, SDrawCoords dc, SColorF color, bool isReflection = false)
        {
            //Direct3D9 expects pixel centers at the top left corner
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
                c2 = c;

            var vert = new STexturedColoredVertex[4];
            vert[0] = new STexturedColoredVertex(new Vector3(dc.Wx1, -dc.Wy1, dc.Wz), new Vector2(dc.Tx1, dc.Ty1), c);
            vert[1] = new STexturedColoredVertex(new Vector3(dc.Wx1, -dc.Wy2, dc.Wz), new Vector2(dc.Tx1, dc.Ty2), c2);
            vert[2] = new STexturedColoredVertex(new Vector3(dc.Wx2, -dc.Wy2, dc.Wz), new Vector2(dc.Tx2, dc.Ty2), c2);
            vert[3] = new STexturedColoredVertex(new Vector3(dc.Wx2, -dc.Wy1, dc.Wz), new Vector2(dc.Tx2, dc.Ty1), c);
            _AddToVertexBuffer(vert, texture.D3DTexture, _CalculateRotationMatrix(dc.Rotation, dc.Wx1, dc.Wx2, dc.Wy1, dc.Wy2));
        }

        private void _AddToVertexBuffer(STexturedColoredVertex[] vertices, Texture tex, Matrix rotation)
        {
            //The vertexbuffer is full, so flush it first
            if (_Vertices.Count >= CSettings.VertexBufferElements)
                _RenderVertexBuffer();

            _Vertices.Enqueue(vertices[0]);
            _Vertices.Enqueue(vertices[1]);
            _Vertices.Enqueue(vertices[2]);
            _Vertices.Enqueue(vertices[3]);
            _VerticesTextures.Enqueue(tex);
            _VerticesRotationMatrices.Enqueue(rotation);
        }

        private void _RenderVertexBuffer()
        {
            if (_Vertices.Count <= 0)
                return;

            try
            {
                //Lock once per frame and write all vertices at once
                using (var stream = _VertexBuffer.Lock(0, _Vertices.Count * Marshal.SizeOf(typeof(STexturedColoredVertex)), LockFlags.Discard))
                {
                    stream.WriteRange(_Vertices.ToArray());
                }
                _VertexBuffer.Unlock();

                for (var i = 0; i < _Vertices.Count; i += 4)
                {
                    var world = _VerticesRotationMatrices.Dequeue();
                    _Device.SetTransform(TransformState.World, ref world);
                    _Device.SetTexture(0, _VerticesTextures.Dequeue());
                    _Device.DrawIndexedPrimitive(PrimitiveType.TriangleList, i, 0, 4, 0, 2);
                }
            }
            catch (SharpDX.SharpDXException e)
            {
                if (!_IsDeviceLost(e))
                    CLog.Error(e, "Failed to draw quads");
            }
            finally
            {
                _Vertices.Clear();
                _VerticesTextures.Clear();
                _VerticesRotationMatrices.Clear();
            }
        }
        #endregion main stuff

        protected override void _ClearScreen()
        {
            try
            {
                //Nur Target: es gibt keinen Depth-Buffer (EnableAutoDepthStencil ist aus)
                _Device.Clear(ClearFlags.Target, new ColorBGRA { B = 0, G = 0, R = 0, A = 255 }, 1.0f, 0);
            }
            catch (SharpDX.SharpDXException e)
            {
                if (!_IsDeviceLost(e))
                    CLog.Error(e, "Failed to clear the backbuffer");
            }
        }

        #region screen capture (ohne D3DX)
        /// <summary>
        /// Reads the backbuffer into a BGRA byte array (alpha = 255). Works with multisampled backbuffers.
        /// </summary>
        private byte[] _GrabBackBuffer(int cropW, int cropH, out int w, out int h)
        {
            var fullW = _PresentParameters.BackBufferWidth;
            var fullH = _PresentParameters.BackBufferHeight;
            var format = _PresentParameters.BackBufferFormat;
            w = cropW > 0 ? Math.Min(cropW, fullW) : fullW;
            h = cropH > 0 ? Math.Min(cropH, fullH) : fullH;

            var data = new byte[w * h * 4];
            using (var back = _Device.GetBackBuffer(0, 0))
            using (var rt = Surface.CreateRenderTarget(_Device, fullW, fullH, format, MultisampleType.None, 0, false))
            using (var sys = Surface.CreateOffscreenPlain(_Device, fullW, fullH, format, Pool.SystemMemory))
            {
                _Device.StretchRectangle(back, rt, TextureFilter.None); //resolves multisampling
                _Device.GetRenderTargetData(rt, sys);

                var rect = sys.LockRectangle(LockFlags.ReadOnly);
                for (var y = 0; y < h; y++)
                    Marshal.Copy(rect.DataPointer + y * rect.Pitch, data, y * w * 4, w * 4);
                sys.UnlockRectangle();
            }
            for (var i = 3; i < data.Length; i += 4)
                data[i] = 255;
            return data;
        }

        public CTextureRef CopyScreen()
        {
            int w, h;
            var data = _GrabBackBuffer(_W, _H, out w, out h);
            var tex = _CreateTexture(new Size(w, h));
            _WriteDataToTexture(tex, data);
            return _GetTextureReference(w, h, tex);
        }

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
                int w, h;
                var data = _GrabBackBuffer(texture.DataSize.Width, texture.DataSize.Height, out w, out h);
                _WriteDataToTexture(texture, data);
            }
        }

        public void MakeScreenShot()
        {
            var file = CHelper.GetUniqueFileName(Path.Combine(CSettings.DataFolder, CSettings.FolderNameScreenshots), "Screenshot.png");

            int w, h;
            var data = _GrabBackBuffer(0, 0, out w, out h);
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                Marshal.Copy(data, 0, bd.Scan0, data.Length);
                bmp.UnlockBits(bd);
                bmp.Save(file, ImageFormat.Png);
            }
        }
        #endregion

        public void DrawRect(SColorF color, SRectF rect, bool allMonitors = true)
        {
            DrawTexture(_BlankTexture, rect, color, false, allMonitors);
        }

        public void DrawRectReflection(SColorF color, SRectF rect, float space, float height)
        {
            DrawTextureReflection(_BlankTexture, rect, color, rect, space, height);
        }

        protected override CD3DTexture _CreateTexture(Size dataSize)
        {
            if (dataSize.Width < 0)
                return new CD3DTexture(null, dataSize);
            return new CD3DTexture(_Device, dataSize, _CheckForNextPowerOf2(dataSize.Width), _CheckForNextPowerOf2(dataSize.Height));
        }

        protected override void _WriteDataToTexture(CD3DTexture texture, byte[] data)
        {
            var rect = texture.D3DTexture.LockRectangle(0, LockFlags.Discard);
            var rowWidth = 4 * texture.DataSize.Width;
            for (int row = 0, i = 0; i + rowWidth <= data.Length; i += rowWidth, row++)
                Marshal.Copy(data, i, rect.DataPointer + row * rect.Pitch, rowWidth);
            texture.D3DTexture.UnlockRectangle(0);
        }

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

                //Shift to center, rotate, shift back, then apply originTranslation
                return translationA * rotationMat * translationB * originTranslation;
            }
            return originTranslation;
        }

        [StructLayout(LayoutKind.Sequential)]
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
                if (_Declaration == null || _Declaration.IsDisposed)
                    _Declaration = new VertexDeclaration(device, _Elements);
                return _Declaration;
            }

            public static void DisposeDeclaration()
            {
                if (_Declaration != null && !_Declaration.IsDisposed)
                    _Declaration.Dispose();
                _Declaration = null;
            }
        }
    }
}
