using System;
using FFmpeg.AutoGen;
using Vocaluxe.Base;
using Vocaluxe.Lib.FFmpeg;
using VocaluxeLib;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.Video.FFmpeg
{
    internal unsafe class CFFmpegVideoDecoderContext : CFFmpegDecoderContextBase
    {
        public const AVPixelFormat OutputFormat = AVPixelFormat.AV_PIX_FMT_BGRA;

        // Bilinear ist deutlich guenstiger als Bicubic und fuer Hintergrundvideos ausreichend
        private const int _ScaleFlags = (int)SwsFlags.SWS_BILINEAR;

        private SwsContext* _SwsContext;
        private byte_ptrArray4 _Buffer;
        private int_array4 _BufferLineSize;

        public int FrameBufferSize { get; private set; }

        public byte* DecodedFrameBuffer => _Buffer[0];

        public int FrameWidth { get; private set; }

        public int FrameHeight { get; private set; }

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

        protected override bool _InternalInit(AVStream* avStream, AVCodecContext* codecContext)
        {
            var (width, height) = _ComputeFinalSize(avStream);
            FrameWidth = width;
            FrameHeight = height;

            FrameBufferSize = ffmpeg.av_image_alloc(ref _Buffer, ref _BufferLineSize, width,
                height, OutputFormat, 1);
            if (FrameBufferSize < 0)
            {
                CLog.Error("Unable to alloc frame buffer");
                return false;
            }

            _SwsContext = ffmpeg.sws_getContext(codecContext->width, codecContext->height, codecContext->pix_fmt,
                width, height, OutputFormat, _ScaleFlags, null, null, null);
            if (_SwsContext == null)
            {
                CLog.Error("Unable to alloc SWS context");
                return false;
            }

            return true;
        }

        protected override bool _ProcessFrame(AVFrame* frame)
        {
            // Falls sich Aufloesung oder Pixelformat mitten im Stream aendern, wird der Kontext neu erzeugt
            _SwsContext = ffmpeg.sws_getCachedContext(_SwsContext, frame->width, frame->height, (AVPixelFormat)frame->format,
                FrameWidth, FrameHeight, OutputFormat, _ScaleFlags, null, null, null);
            if (_SwsContext == null)
            {
                CLog.Error("Unable to alloc SWS context");
                return false;
            }

            if (ffmpeg.sws_scale(_SwsContext, frame->data,
                    frame->linesize,
                    0,
                    frame->height, _Buffer,
                    _BufferLineSize) < 0)
            {
                CLog.Error("Error when scaling the video frame");
                return false;
            }

            return true;
        }

        protected override void _InternalFree()
        {
            ffmpeg.sws_freeContext(_SwsContext);
            _SwsContext = null;

            if (_Buffer[0] != null)
            {
                fixed (byte_ptrArray4* buffer = &_Buffer)
                {
                    ffmpeg.av_freep(buffer);
                }
            }
        }
    }
}
