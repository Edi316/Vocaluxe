using FFmpeg.AutoGen;
using System;
using System.IO;
using System.Runtime.InteropServices;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.FFmpeg
{
    internal abstract unsafe class CFFmpegDecoderContextBase : IDisposable
    {
        private bool _Initialized;
        private Stream _SourceStream;
        private readonly object _SourceLock = new();
        private byte[] _ReadBuffer;

        private avio_alloc_context_read_packet _ReadSourceStreamCallback;
        private avio_alloc_context_seek _SeekSourceStreamCallback;
        private AVIOContext* _IOContext;
        private AVFormatContext* _FormatContext;
        private AVCodecContext* _CodecContext;
        private AVStream* _AvStream;
        private AVPacket* _Packet;
        private AVFrame* _Frame;

        private bool _Draining;
        private int _DecodeErrorCount;

        protected abstract AVMediaType _MediaType { get; }

        public TimeSpan Duration { get; private set; } = TimeSpan.Zero;

        public float FrameRate { get; private set; }

        public TimeSpan Position { get; private set; } = TimeSpan.Zero;

        #region Init

        public bool Initialize(Stream sourceStream)
        {
            if (_Initialized)
            {
                CLog.Error("Decoder context recycled");
                return false;
            }

            _SourceStream = sourceStream;

            if (!_InitIOContext())
            {
                _Free();
                return false;
            }

            if (!_InitFormatContext())
            {
                _Free();
                return false;
            }

            AVCodec* codec;
            if (!_ChooseStream(&codec))
            {
                _Free();
                return false;
            }

            if (!_InitCodeCodecContext(codec))
            {
                _Free();
                return false;
            }

            _Packet = ffmpeg.av_packet_alloc();
            _Frame = ffmpeg.av_frame_alloc();
            if (_Packet == null || _Frame == null)
            {
                CLog.Error("Unable to alloc packet/frame");
                _Free();
                return false;
            }

            Duration = _ComputeDuration();
            FrameRate = _ComputeFrameRate();

            if (!_InternalInit(_AvStream, _CodecContext))
            {
                _Free();
                return false;
            }

            _Initialized = true;
            return true;
        }

        protected abstract bool _InternalInit(AVStream* avStream, AVCodecContext* codecContext);

        private TimeSpan _ComputeDuration()
        {
            if (_AvStream->duration != ffmpeg.AV_NOPTS_VALUE && _AvStream->duration > 0)
            {
                return TimeSpan.FromMilliseconds(
                    ffmpeg.av_rescale_q(_AvStream->duration, _AvStream->time_base, new AVRational { num = 1, den = 1000 }));
            }

            if (_FormatContext->duration != ffmpeg.AV_NOPTS_VALUE && _FormatContext->duration > 0)
            {
                return TimeSpan.FromSeconds(_FormatContext->duration / (double)ffmpeg.AV_TIME_BASE);
            }

            return TimeSpan.Zero;
        }

        private float _ComputeFrameRate()
        {
            var rate = _AvStream->avg_frame_rate;
            if (rate.num > 0 && rate.den > 0)
            {
                return rate.num / (float)rate.den;
            }

            rate = _AvStream->r_frame_rate;
            if (rate.num > 0 && rate.den > 0)
            {
                return rate.num / (float)rate.den;
            }

            return 0;
        }

        private static void _LogError(string message, int errorCode)
        {
            CLog.Error($"{message}: {FFmpegHelper.ErrorToString(errorCode)} ({errorCode})");
        }

        private int _ReadSourceStream(void* opaque, byte* buffer, int bufferSize)
        {
            try
            {
                lock (_SourceLock)
                {
                    if (_SourceStream == null || _ReadBuffer == null)
                    {
                        return ffmpeg.AVERROR_EOF;
                    }

                    var count = Math.Min(bufferSize, _ReadBuffer.Length);
                    var read = _SourceStream.Read(_ReadBuffer, 0, count);
                    if (read <= 0)
                    {
                        return ffmpeg.AVERROR_EOF;
                    }

                    Marshal.Copy(_ReadBuffer, 0, (IntPtr)buffer, read);
                    return read;
                }
            }
            catch (Exception e)
            {
                CLog.Error("Error reading source stream: " + e.Message);
                return ffmpeg.AVERROR_EXTERNAL;
            }
        }

        private long _SeekSourceStream(void* opaque, long offset, int whence)
        {
            try
            {
                lock (_SourceLock)
                {
                    if (_SourceStream == null)
                    {
                        return -1;
                    }

                    if (whence == ffmpeg.AVSEEK_SIZE)
                    {
                        return _SourceStream.Length;
                    }

                    whence &= ~ffmpeg.AVSEEK_FORCE;
                    if (whence < 0 || whence > 2)
                    {
                        return -1;
                    }

                    return _SourceStream.Seek(offset, (SeekOrigin)whence);
                }
            }
            catch (Exception e)
            {
                CLog.Error("Error seeking source stream: " + e.Message);
                return -1;
            }
        }

        private bool _InitIOContext()
        {
            if (!_SourceStream.CanRead)
            {
                CLog.Error("The source stream is not readable");
                return false;
            }

            _ReadBuffer = new byte[FFmpegHelper.IOBufferSize];
            _ReadSourceStreamCallback = _ReadSourceStream;
            _SeekSourceStreamCallback = _SourceStream.CanSeek ? _SeekSourceStream : null;

            var ioBuffer = (byte*)ffmpeg.av_malloc(FFmpegHelper.IOBufferSize);
            if (ioBuffer == null)
            {
                CLog.Error("Unable to alloc the AVIO buffer");
                return false;
            }

            _IOContext = ffmpeg.avio_alloc_context(ioBuffer, FFmpegHelper.IOBufferSize, 0, null, _ReadSourceStreamCallback, null, _SeekSourceStreamCallback);
            if (_IOContext == null)
            {
                ffmpeg.av_free(ioBuffer);
                CLog.Error("Unable to alloc the AVIO context");
                return false;
            }

            return true;
        }

        private bool _InitFormatContext()
        {
            _FormatContext = ffmpeg.avformat_alloc_context();
            if (_FormatContext == null)
            {
                CLog.Error("Unable to alloc the format context");
                return false;
            }

            _FormatContext->pb = _IOContext;
            int result;
            fixed (AVFormatContext** formatContext = &_FormatContext)
            {
                result = ffmpeg.avformat_open_input(formatContext, null, null, null);
            }

            if (result < 0)
            {
                _LogError("Error getting format context from video stream", result);
                return false;
            }

            return true;
        }

        private bool _ChooseStream(AVCodec** codec)
        {
            var result = ffmpeg.avformat_find_stream_info(_FormatContext, null);
            if (result < 0)
            {
                _LogError("Could not find stream info in video stream", result);
                return false;
            }

            var matchingStreamIndex = ffmpeg.av_find_best_stream(_FormatContext, _MediaType, -1, -1, codec, 0);
            if (matchingStreamIndex < 0)
            {
                CLog.Error($"Unable to find a stream with {_MediaType}");
                return false;
            }

            _AvStream = _FormatContext->streams[matchingStreamIndex];
            return true;
        }

        private bool _InitCodeCodecContext(AVCodec* codec)
        {
            _CodecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (_CodecContext == null)
            {
                CLog.Error("Unable to alloc the codec context");
                return false;
            }

            var result = ffmpeg.avcodec_parameters_to_context(_CodecContext, _AvStream->codecpar);
            if (result < 0)
            {
                _LogError("Unable to fill codec parameters", result);
                return false;
            }

            _CodecContext->thread_count = Math.Min(Environment.ProcessorCount, 4);

            result = ffmpeg.avcodec_open2(_CodecContext, codec, null);
            if (result < 0)
            {
                _LogError("Unable to open the codec", result);
                return false;
            }

            return true;
        }

        #endregion

        #region Process

        protected abstract bool _ProcessFrame(AVFrame* frame);

        private bool _NextFrame()
        {
            if (_CodecContext == null || _FormatContext == null || _Frame == null)
            {
                return false;
            }

            while (true)
            {
                var ret = ffmpeg.avcodec_receive_frame(_CodecContext, _Frame);
                if (ret == 0)
                {
                    return true;
                }

                if (ret == ffmpeg.AVERROR_EOF)
                {
                    return false;
                }

                if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    _LogError("Error receiving frame from decoder", ret);
                    return false;
                }

                if (!_FeedDecoder())
                {
                    return false;
                }
            }
        }

        private bool _FeedDecoder()
        {
            if (_Draining)
            {
                return false;
            }

            while (true)
            {
                var ret = ffmpeg.av_read_frame(_FormatContext, _Packet);
                if (ret < 0)
                {
                    _Draining = true;
                    ffmpeg.avcodec_send_packet(_CodecContext, null);
                    return true;
                }

                try
                {
                    if (_Packet->stream_index != _AvStream->index)
                    {
                        continue;
                    }

                    ret = ffmpeg.avcodec_send_packet(_CodecContext, _Packet);
                    if (ret >= 0 || ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        return true;
                    }

                    if (_DecodeErrorCount++ < 10)
                    {
                        _LogError("Error sending packet to decoder", ret);
                    }
                }
                finally
                {
                    ffmpeg.av_packet_unref(_Packet);
                }
            }
        }

        private void _ComputePosition(AVFrame* frame)
        {
            var timestamp = frame->best_effort_timestamp;
            if (timestamp == ffmpeg.AV_NOPTS_VALUE)
            {
                return;
            }

            var elapsedMs = ffmpeg.av_rescale_q(timestamp, _AvStream->time_base, new AVRational { num = 1, den = 1000 });
            Position = TimeSpan.FromMilliseconds(elapsedMs);
        }

        public bool GetFrame()
        {
            if (!_NextFrame())
            {
                return false;
            }

            try
            {
                _ComputePosition(_Frame);
                return _ProcessFrame(_Frame);
            }
            finally
            {
                ffmpeg.av_frame_unref(_Frame);
            }
        }

        public bool Seek(bool backward, TimeSpan targetPos)
        {
            if (_FormatContext == null || _CodecContext == null || _AvStream == null)
            {
                return false;
            }

            var flags = backward ? ffmpeg.AVSEEK_FLAG_BACKWARD : 0;
            var timestamp = ffmpeg.av_rescale_q((long)targetPos.TotalMilliseconds, new AVRational { num = 1, den = 1000 }, _AvStream->time_base);

            var ret = ffmpeg.av_seek_frame(_FormatContext, _AvStream->index, timestamp, flags);
            if (ret < 0)
            {
                _LogError("Seek failed", ret);
                return false;
            }

            ffmpeg.avcodec_flush_buffers(_CodecContext);
            _Draining = false;
            Position = targetPos;
            return true;
        }

        public bool DecodeUntil(TimeSpan target)
        {
            try
            {
                while (_NextFrame())
                {
                    try
                    {
                        _ComputePosition(_Frame);
                        if (Position >= target)
                        {
                            return _ProcessFrame(_Frame);
                        }
                    }
                    finally
                    {
                        ffmpeg.av_frame_unref(_Frame);
                    }
                }
            }
            catch (Exception e)
            {
                CLog.Error("Error decoding until target: " + e.Message);
            }

            return false;
        }

        public bool DropWithSkip(int frameDropCount)
        {
            frameDropCount += frameDropCount / 16;
            try
            {
                for (var i = 0; i < frameDropCount - 1; i++)
                {
                    if (!_NextFrame())
                    {
                        return false;
                    }

                    _ComputePosition(_Frame);
                    ffmpeg.av_frame_unref(_Frame);
                }

                return GetFrame();
            }
            catch (Exception e)
            {
                CLog.Error("Error skipping frame: " + e.Message);
                return false;
            }
        }

        #endregion

        #region Free

        protected abstract void _InternalFree();

        private void _Free()
        {
            if (_Packet != null)
            {
                fixed (AVPacket** packet = &_Packet)
                {
                    ffmpeg.av_packet_free(packet);
                }
            }

            if (_Frame != null)
            {
                fixed (AVFrame** frame = &_Frame)
                {
                    ffmpeg.av_frame_free(frame);
                }
            }

            if (_FormatContext != null)
            {
                fixed (AVFormatContext** formatContext = &_FormatContext)
                {
                    ffmpeg.avformat_close_input(formatContext);
                }
            }

            _InternalFree();

            if (_CodecContext != null)
            {
                fixed (AVCodecContext** codecContext = &_CodecContext)
                {
                    ffmpeg.avcodec_free_context(codecContext);
                }
            }

            if (_IOContext != null)
            {
                ffmpeg.av_freep(&_IOContext->buffer);
                fixed (AVIOContext** ioContext = &_IOContext)
                {
                    ffmpeg.avio_context_free(ioContext);
                }
            }

            _AvStream = null;
            lock (_SourceLock)
            {
                _ReadSourceStreamCallback = null;
                _SeekSourceStreamCallback = null;
                _SourceStream = null;
                _ReadBuffer = null;
            }
        }

        ~CFFmpegDecoderContextBase()
        {
            _Free();
        }

        public void Dispose()
        {
            _Free();
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
