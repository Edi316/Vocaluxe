using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Vocaluxe.Base;
using Vocaluxe.Lib.FFmpeg;
using VocaluxeLib;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.Video.FFmpeg
{
    internal unsafe class CFFmpegVideoDecoderContext : CFFmpegDecoderContextBase
    {
        // Bilinear ist deutlich guenstiger als Bicubic und fuer Hintergrundvideos ausreichend
        private const int _ScaleFlags = (int)SwsFlags.SWS_BILINEAR;
        private const int _BufferAlignment = 64;

        private AVPixelFormat _OutputFormat;
        private SwsContext* _SwsContext;
        // Fertiger Frame im Ausgabeformat. Wird per Referenz (ohne Kopie) an den Ringpuffer uebergeben.
        private AVFrame* _OutputFrame;
        // Puffer fuer konvertierte Frames werden wiederverwendet, statt fuer jeden Frame neu allokiert
        private AVBufferPool* _OutputPool;
        private int _OutputPoolSize;
        private int _ScaleErrorCount;

        // Hardware-Decoding
        private AVBufferRef* _HwDeviceContext;
        private AVPixelFormat _HwPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        private AVCodecContext_get_format _GetFormatCallback; // Referenz halten, solange FFmpeg den Funktionszeiger nutzt
        private volatile bool _HwFallbackLogged;
        // Frame im Systemspeicher, in den der GPU-Frame kopiert wird (wird wiederverwendet)
        private AVFrame* _TransferFrame;
        private int _TransferErrorCount;

        // Hardware-Geraete werden einmal erzeugt und von allen Videos gemeinsam genutzt (Erzeugen kostet Zeit, mehrere Videos laufen parallel)
        private static readonly object _SharedDevicesLock = new();
        private static readonly Dictionary<AVHWDeviceType, IntPtr> _SharedDevices = new();
        private static readonly HashSet<AVHWDeviceType> _FailedDeviceTypes = new();

        /// <summary>
        ///     Breite der ausgegebenen Frames (nach optionalem Herunterskalieren)
        /// </summary>
        public int FrameWidth { get; private set; }

        /// <summary>
        ///     Hoehe der ausgegebenen Frames (nach optionalem Herunterskalieren)
        /// </summary>
        public int FrameHeight { get; private set; }

        /// <summary>
        ///     Der zuletzt mit GetFrame/DecodeUntil/DropWithSkip erzeugte Frame (YUV420P oder BGRA)
        /// </summary>
        public AVFrame* OutputFrame => _OutputFrame;

        protected override AVMediaType _MediaType => AVMediaType.AVMEDIA_TYPE_VIDEO;

        private static (int, int) _ComputeFinalSize(AVStream* avStream)
        {
            var streamHeight = avStream->codecpar->height;
            if (CConfig.Config.FFmpeg.VideoDownscaleResolution == EVideoDownscaleResolution.TR_CONFIG_SCALING_DISABLED)
            {
                return (avStream->codecpar->width, streamHeight);
            }

            var maxHeight = (int)CConfig.Config.FFmpeg.VideoDownscaleResolution;
            decimal sizeFactor = 1;
            if (streamHeight > maxHeight)
            {
                sizeFactor = (decimal)maxHeight / streamHeight;
            }

            return (Math.Max(1, (int)(avStream->codecpar->width * sizeFactor)), Math.Max(1, (int)(streamHeight * sizeFactor)));
        }

        protected override void _ConfigureCodecContext(AVCodec* codec, AVCodecContext* codecContext)
        {
            // 0 = FFmpeg waehlt automatisch (alle Kerne). Frame- und Slice-Threading, je nachdem was der Codec kann.
            codecContext->thread_count = Math.Max(0, CConfig.Config.FFmpeg.DecoderThreads);
            codecContext->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;

            if (CConfig.Config.FFmpeg.HardwareDecoding == EOffOn.TR_CONFIG_ON)
            {
                _InitHardwareDecoding(codec, codecContext);
            }
        }

        #region Hardware decoding
        /// <summary>
        ///     Moegliche Hardware-Decoder in der Reihenfolge ihrer Prioritaet
        /// </summary>
        private static AVHWDeviceType[] _GetHardwareDeviceTypes()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 };
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX };
            }

            return new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, AVHWDeviceType.AV_HWDEVICE_TYPE_VDPAU };
        }

        private static AVPixelFormat _FindHardwarePixelFormat(AVCodec* codec, AVHWDeviceType type)
        {
            const int hwConfigMethodHwDeviceCtx = 0x01; // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX
            for (var i = 0;; i++)
            {
                var config = ffmpeg.avcodec_get_hw_config(codec, i);
                if (config == null)
                {
                    return AVPixelFormat.AV_PIX_FMT_NONE;
                }

                if ((config->methods & hwConfigMethodHwDeviceCtx) != 0 && config->device_type == type)
                {
                    return config->pix_fmt;
                }
            }
        }

        /// <summary>
        ///     AV1: FFmpeg waehlt standardmaessig libdav1d (nur Software). Der eingebaute Decoder "av1" kann dagegen nur mit
        ///     Hardware-Beschleunigung decodieren. Er wird bevorzugt, wenn ein passendes Hardware-Geraet verfuegbar ist; ob die
        ///     Grafikkarte AV1 wirklich kann, zeigt erst der Test mit dem ersten Frame (sonst Rueckfall auf libdav1d).
        /// </summary>
        protected override AVCodec* _GetPreferredCodec(AVCodec* defaultCodec)
        {
            if (CConfig.Config.FFmpeg.HardwareDecoding != EOffOn.TR_CONFIG_ON || defaultCodec->id != AVCodecID.AV_CODEC_ID_AV1)
            {
                return null;
            }

            var hardwareOnlyCodec = ffmpeg.avcodec_find_decoder_by_name("av1");
            if (hardwareOnlyCodec == null || hardwareOnlyCodec == defaultCodec)
            {
                return null;
            }

            foreach (var type in _GetHardwareDeviceTypes())
            {
                if (_FindHardwarePixelFormat(hardwareOnlyCodec, type) == AVPixelFormat.AV_PIX_FMT_NONE)
                {
                    continue;
                }

                var device = _GetSharedDevice(type);
                if (device != null)
                {
                    ffmpeg.av_buffer_unref(&device);
                    return hardwareOnlyCodec;
                }
            }

            return null;
        }

        protected override bool _IsProbeFrameValid(AVFrame* frame)
        {
            return frame->hw_frames_ctx != null;
        }

        private void _InitHardwareDecoding(AVCodec* codec, AVCodecContext* codecContext)
        {
            // Kann nach einem fehlgeschlagenen Test eines anderen Decoders erneut aufgerufen werden
            if (_HwDeviceContext != null)
            {
                fixed (AVBufferRef** deviceContext = &_HwDeviceContext)
                {
                    ffmpeg.av_buffer_unref(deviceContext);
                }
            }

            _HwPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
            _HwFallbackLogged = false;

            foreach (var type in _GetHardwareDeviceTypes())
            {
                var pixelFormat = _FindHardwarePixelFormat(codec, type);
                if (pixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
                {
                    continue; // Codec unterstuetzt diesen Hardware-Typ nicht
                }

                var deviceContext = _GetSharedDevice(type);
                if (deviceContext == null)
                {
                    continue;
                }

                _HwDeviceContext = deviceContext;
                _HwPixelFormat = pixelFormat;
                codecContext->hw_device_ctx = ffmpeg.av_buffer_ref(deviceContext);
                _GetFormatCallback = _GetFormat;
                codecContext->get_format = _GetFormatCallback;
                CLog.Information($"Using hardware decoding with {ffmpeg.av_hwdevice_get_type_name(type)} for {ffmpeg.avcodec_get_name(codec->id)}");
                return;
            }
        }

        /// <summary>
        ///     Liefert eine neue Referenz auf das gemeinsame Hardware-Geraet des Typs oder null, wenn es nicht verfuegbar ist
        /// </summary>
        private static AVBufferRef* _GetSharedDevice(AVHWDeviceType type)
        {
            lock (_SharedDevicesLock)
            {
                if (_FailedDeviceTypes.Contains(type))
                {
                    return null;
                }

                if (!_SharedDevices.TryGetValue(type, out var device))
                {
                    AVBufferRef* deviceContext = null;
                    var ret = ffmpeg.av_hwdevice_ctx_create(&deviceContext, type, null, null, 0);
                    if (ret < 0)
                    {
                        CLog.Information($"Hardware decoding with {ffmpeg.av_hwdevice_get_type_name(type)} not available: {FFmpegHelper.ErrorToString(ret)}");
                        _FailedDeviceTypes.Add(type);
                        return null;
                    }

                    device = (IntPtr)deviceContext;
                    _SharedDevices.Add(type, device);
                }

                return ffmpeg.av_buffer_ref((AVBufferRef*)device);
            }
        }

        /// <summary>
        ///     Wird von FFmpeg (ggf. aus einem Decoder-Thread) aufgerufen, um das Ausgabeformat zu waehlen.
        ///     Schlaegt die Initialisierung der Hardware fehl (z.B. Profil nicht unterstuetzt), entfernt FFmpeg das
        ///     Hardware-Format aus der Liste und ruft die Funktion erneut auf -> Software-Decoding.
        /// </summary>
        private AVPixelFormat _GetFormat(AVCodecContext* context, AVPixelFormat* formats)
        {
            var fallback = AVPixelFormat.AV_PIX_FMT_NONE;
            for (var format = formats; *format != AVPixelFormat.AV_PIX_FMT_NONE; format++)
            {
                if (*format == _HwPixelFormat)
                {
                    return *format;
                }

                if (fallback == AVPixelFormat.AV_PIX_FMT_NONE)
                {
                    var descriptor = ffmpeg.av_pix_fmt_desc_get(*format);
                    if (descriptor != null && (descriptor->flags & ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) == 0)
                    {
                        fallback = *format;
                    }
                }
            }

            if (!_HwFallbackLogged)
            {
                _HwFallbackLogged = true;
                CLog.Information("Hardware decoding is not supported for this video, using software decoding");
            }

            return fallback;
        }

        /// <summary>
        ///     Kopiert einen GPU-Frame in den Systemspeicher (NV12 bzw. P010)
        /// </summary>
        private AVFrame* _TransferHardwareFrame(AVFrame* frame)
        {
            if (_TransferFrame->buf[0] != null && (_TransferFrame->width != frame->width || _TransferFrame->height != frame->height))
            {
                ffmpeg.av_frame_unref(_TransferFrame);
            }

            var ret = ffmpeg.av_hwframe_transfer_data(_TransferFrame, frame, 0);
            if (ret < 0 && _TransferFrame->buf[0] != null)
            {
                // Wiederverwendeter Puffer passt nicht (mehr) -> neu allokieren lassen
                ffmpeg.av_frame_unref(_TransferFrame);
                ret = ffmpeg.av_hwframe_transfer_data(_TransferFrame, frame, 0);
            }

            if (ret < 0)
            {
                if (_TransferErrorCount++ < 10)
                {
                    CLog.Error($"Unable to transfer the video frame from the GPU: {FFmpegHelper.ErrorToString(ret)} ({ret})");
                }

                return null;
            }

            // Nur die benoetigten Eigenschaften uebernehmen (av_frame_copy_props wuerde auf dem wiederverwendeten Frame Side-Data ansammeln)
            _TransferFrame->pts = frame->pts;
            _TransferFrame->best_effort_timestamp = frame->best_effort_timestamp;
            _TransferFrame->colorspace = frame->colorspace;
            _TransferFrame->color_range = frame->color_range;
            _TransferFrame->color_primaries = frame->color_primaries;
            _TransferFrame->color_trc = frame->color_trc;
            _TransferFrame->chroma_location = frame->chroma_location;
            _TransferFrame->sample_aspect_ratio = frame->sample_aspect_ratio;
            return _TransferFrame;
        }
        #endregion Hardware decoding

        protected override bool _InternalInit(AVStream* avStream, AVCodecContext* codecContext)
        {
            var (width, height) = _ComputeFinalSize(avStream);
            FrameWidth = width;
            FrameHeight = height;

            // Mit YUV-Unterstuetzung des Renderers entfaellt die teure Umrechnung nach RGB auf der CPU,
            // und es muessen nur 1,5 statt 4 Byte pro Pixel hochgeladen werden
            _OutputFormat = CDraw.SupportsYuvVideo ? AVPixelFormat.AV_PIX_FMT_YUV420P : AVPixelFormat.AV_PIX_FMT_BGRA;

            _OutputFrame = ffmpeg.av_frame_alloc();
            _TransferFrame = ffmpeg.av_frame_alloc();
            if (_OutputFrame == null || _TransferFrame == null)
            {
                CLog.Error("Unable to alloc output frame");
                return false;
            }

            // Ohne sws_init_context wird der Kontext dynamisch anhand der Frames konfiguriert (FFmpeg >= 8).
            // Nur so nutzt swscale mehrere Threads, mit sws_scale() laeuft die Umrechnung immer auf einem Kern.
            _SwsContext = ffmpeg.sws_alloc_context();
            if (_SwsContext == null)
            {
                CLog.Error("Unable to alloc SWS context");
                return false;
            }

            ffmpeg.av_opt_set_int(_SwsContext, "threads", 0, 0); // 0 = automatisch
            ffmpeg.av_opt_set_int(_SwsContext, "sws_flags", _ScaleFlags, 0);

            CLog.Information($"Video {avStream->codecpar->width}x{avStream->codecpar->height} ({ffmpeg.avcodec_get_name(codecContext->codec_id)}) -> " +
                             $"{width}x{height} {ffmpeg.av_get_pix_fmt_name(_OutputFormat)}");
            return true;
        }

        /// <summary>
        ///     Setzt fehlende Farbinformationen so, wie sie auch spaeter bei der Umrechnung nach RGB angenommen werden
        /// </summary>
        private static void _ApplyColorDefaults(AVFrame* frame)
        {
            if (frame->colorspace == AVColorSpace.AVCOL_SPC_UNSPECIFIED)
            {
                frame->colorspace = frame->height >= 720 ? AVColorSpace.AVCOL_SPC_BT709 : AVColorSpace.AVCOL_SPC_SMPTE170M;
            }

            if (frame->color_range == AVColorRange.AVCOL_RANGE_UNSPECIFIED)
            {
                var format = (AVPixelFormat)frame->format;
                var isJpeg = format == AVPixelFormat.AV_PIX_FMT_YUVJ420P || format == AVPixelFormat.AV_PIX_FMT_YUVJ422P || format == AVPixelFormat.AV_PIX_FMT_YUVJ444P;
                frame->color_range = isJpeg ? AVColorRange.AVCOL_RANGE_JPEG : AVColorRange.AVCOL_RANGE_MPEG;
            }
        }

        /// <summary>
        ///     Versieht _OutputFrame mit einem (wiederverwendeten) Puffer im Ausgabeformat
        /// </summary>
        private bool _AllocOutputFrame()
        {
            var size = ffmpeg.av_image_get_buffer_size(_OutputFormat, FrameWidth, FrameHeight, _BufferAlignment);
            if (size < 0)
            {
                CLog.Error("Invalid output frame size");
                return false;
            }

            if (_OutputPool == null || _OutputPoolSize != size)
            {
                if (_OutputPool != null)
                {
                    fixed (AVBufferPool** pool = &_OutputPool)
                    {
                        ffmpeg.av_buffer_pool_uninit(pool);
                    }
                }

                _OutputPool = ffmpeg.av_buffer_pool_init((ulong)size, null);
                _OutputPoolSize = size;
                if (_OutputPool == null)
                {
                    CLog.Error("Unable to alloc output buffer pool");
                    return false;
                }
            }

            var buffer = ffmpeg.av_buffer_pool_get(_OutputPool);
            if (buffer == null)
            {
                CLog.Error("Unable to get output buffer");
                return false;
            }

            _OutputFrame->buf[0] = buffer;
            _OutputFrame->format = (int)_OutputFormat;
            _OutputFrame->width = FrameWidth;
            _OutputFrame->height = FrameHeight;

            var data = new byte_ptrArray4();
            var lineSize = new int_array4();
            if (ffmpeg.av_image_fill_arrays(ref data, ref lineSize, buffer->data, _OutputFormat, FrameWidth, FrameHeight, _BufferAlignment) < 0)
            {
                CLog.Error("Unable to set up output frame");
                ffmpeg.av_frame_unref(_OutputFrame);
                return false;
            }

            for (uint i = 0; i < 4; i++)
            {
                _OutputFrame->data[i] = data[i];
                _OutputFrame->linesize[i] = lineSize[i];
            }

            _OutputFrame->extended_data = (byte**)&_OutputFrame->data;
            return true;
        }

        protected override bool _ProcessFrame(AVFrame* frame)
        {
            ffmpeg.av_frame_unref(_OutputFrame);
            if (frame->hw_frames_ctx != null)
            {
                frame = _TransferHardwareFrame(frame);
                if (frame == null)
                {
                    return false;
                }
            }

            _ApplyColorDefaults(frame);

            if (frame->format == (int)_OutputFormat && frame->width == FrameWidth && frame->height == FrameHeight)
            {
                // Decoder liefert bereits das Ausgabeformat (typisch fuer 8-Bit H.264/HEVC/VP9/AV1 mit YUV-Ausgabe):
                // nur eine weitere Referenz auf den Frame, keine Kopie
                return ffmpeg.av_frame_ref(_OutputFrame, frame) >= 0;
            }

            if (!_AllocOutputFrame())
            {
                return false;
            }

            // Gleiche Farbeigenschaften wie die Quelle -> swscale rechnet nur das Format um (z.B. 10 Bit -> 8 Bit, NV12 -> YUV420P, Skalierung)
            ffmpeg.av_frame_copy_props(_OutputFrame, frame);
            if (_OutputFormat == AVPixelFormat.AV_PIX_FMT_BGRA)
            {
                _OutputFrame->colorspace = AVColorSpace.AVCOL_SPC_RGB;
                _OutputFrame->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            }

            var ret = ffmpeg.sws_scale_frame(_SwsContext, _OutputFrame, frame);
            if (ret < 0)
            {
                if (_ScaleErrorCount++ < 10)
                {
                    CLog.Error($"Error when scaling the video frame from {ffmpeg.av_get_pix_fmt_name((AVPixelFormat)frame->format)}: " +
                               $"{FFmpegHelper.ErrorToString(ret)} ({ret})");
                }

                ffmpeg.av_frame_unref(_OutputFrame);
                return false;
            }

            return true;
        }

        protected override void _InternalFree()
        {
            if (_SwsContext != null)
            {
                fixed (SwsContext** swsContext = &_SwsContext)
                {
                    ffmpeg.sws_free_context(swsContext);
                }
            }

            if (_OutputFrame != null)
            {
                fixed (AVFrame** frame = &_OutputFrame)
                {
                    ffmpeg.av_frame_free(frame);
                }
            }

            if (_TransferFrame != null)
            {
                fixed (AVFrame** frame = &_TransferFrame)
                {
                    ffmpeg.av_frame_free(frame);
                }
            }

            // Der Codec-Kontext haelt eine eigene Referenz, die mit ihm freigegeben wird
            if (_HwDeviceContext != null)
            {
                fixed (AVBufferRef** deviceContext = &_HwDeviceContext)
                {
                    ffmpeg.av_buffer_unref(deviceContext);
                }
            }

            // Der Pool wird erst freigegeben, wenn alle Puffer (z.B. noch im Ringpuffer) zurueckgegeben wurden
            if (_OutputPool != null)
            {
                fixed (AVBufferPool** pool = &_OutputPool)
                {
                    ffmpeg.av_buffer_pool_uninit(pool);
                }
            }
        }
    }
}
