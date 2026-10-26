using FFmpeg.AutoGen;
using Vocaluxe.Lib.FFmpeg;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.Sound.Playback.FFmpeg
{
    internal sealed unsafe class CFFmpegAudioDecoderContext : CFFmpegDecoderContextBase
    {
        // Output is always packed (interleaved) signed 16 bit:
        // - PortAudio has an issue (fixed only on master, never released) with buffers limited to 16 bits
        //   https://github.com/PortAudio/portaudio/pull/774
        // - Consumers do not get any information about the sample format, they expect interleaved 16 bit data
        // - A packed format has exactly one data plane, so a single byte* is a valid output buffer
        private const AVSampleFormat _OutputFormat = AVSampleFormat.AV_SAMPLE_FMT_S16;

        private SwrContext* _SwrContext;
        private byte* _Buffer;
        private int _BufferCapacitySamples;
        private AVChannelLayout* _ChannelLayout;

        public int SamplesRate { get; private set; }

        public int ChannelCount => _ChannelLayout != null ? _ChannelLayout->nb_channels : 0;

        /// <summary>
        /// Number of valid bytes in <see cref="Buffer"/> for the last decoded frame
        /// </summary>
        public int BufferSize { get; private set; }

        public byte* Buffer => _Buffer;

        protected override AVMediaType _MediaType => AVMediaType.AVMEDIA_TYPE_AUDIO;

        protected override bool _InternalInit(AVStream* avStream, AVCodecContext* codecContext)
        {
            SamplesRate = codecContext->sample_rate;
            _ChannelLayout = &codecContext->ch_layout;
            BufferSize = 0;

            if (ChannelCount <= 0)
            {
                CLog.Error("Invalid channel count");
                return false;
            }

            // Only skip the resampler if the decoder already outputs exactly what we need.
            // Planar formats (e.g. FLTP for MP3/AAC, S16P) always have to be converted.
            if (codecContext->sample_fmt == _OutputFormat)
            {
                return true;
            }

            fixed (SwrContext** swrContext = &_SwrContext)
            {
                if (ffmpeg.swr_alloc_set_opts2(swrContext,
                        _ChannelLayout,
                        _OutputFormat,
                        SamplesRate,
                        _ChannelLayout,
                        codecContext->sample_fmt,
                        SamplesRate,
                        0, null) < 0)
                {
                    CLog.Error("Could not allocate resampler context");
                    return false;
                }
            }

            if (ffmpeg.swr_init(_SwrContext) < 0)
            {
                CLog.Error("Could not init resampler");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Makes sure the output buffer can hold at least <paramref name="samples"/> samples per channel.
        /// The frame size is not fixed (codecContext->frame_size may be 0, e.g. for PCM, and frames may vary in size),
        /// so the buffer grows on demand.
        /// </summary>
        private bool _EnsureBufferCapacity(int samples)
        {
            if (_Buffer != null && samples <= _BufferCapacitySamples)
            {
                return true;
            }

            _FreeBuffer();

            int lineSize;
            fixed (byte** buffer = &_Buffer)
            {
                if (ffmpeg.av_samples_alloc(buffer, &lineSize, ChannelCount, samples, _OutputFormat, 1) < 0)
                {
                    CLog.Error("Unable to allocate the output buffer");
                    return false;
                }
            }

            _BufferCapacitySamples = samples;
            return true;
        }

        protected override bool _ProcessFrame(AVFrame* frame)
        {
            BufferSize = 0;
            int samples = frame->nb_samples;

            if (_SwrContext != null)
            {
                int maxOutSamples = ffmpeg.swr_get_out_samples(_SwrContext, frame->nb_samples);
                if (maxOutSamples < 0 || !_EnsureBufferCapacity(maxOutSamples))
                {
                    return false;
                }

                fixed (byte** buffer = &_Buffer)
                {
                    samples = ffmpeg.swr_convert(_SwrContext, buffer, maxOutSamples, frame->extended_data, frame->nb_samples);
                }

                if (samples < 0)
                {
                    return false;
                }
            }
            else
            {
                if (!_EnsureBufferCapacity(samples))
                {
                    return false;
                }

                fixed (byte** buffer = &_Buffer)
                {
                    if (ffmpeg.av_samples_copy(buffer, frame->extended_data, 0, 0, samples, ChannelCount, _OutputFormat) < 0)
                    {
                        return false;
                    }
                }
            }

            // Use the real amount of data of this frame, not the buffer capacity (last frame is usually shorter)
            int size = ffmpeg.av_samples_get_buffer_size(null, ChannelCount, samples, _OutputFormat, 1);
            if (size < 0)
            {
                return false;
            }

            BufferSize = size;
            return true;
        }

        private void _FreeBuffer()
        {
            if (_Buffer != null)
            {
                fixed (byte** buffer = &_Buffer)
                {
                    ffmpeg.av_freep(buffer);
                }
            }

            _BufferCapacitySamples = 0;
            BufferSize = 0;
        }

        protected override void _InternalFree()
        {
            fixed (SwrContext** swrContext = &_SwrContext)
            {
                ffmpeg.swr_free(swrContext);
            }

            _FreeBuffer();
            _ChannelLayout = null;
        }
    }
}
