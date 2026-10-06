using FFmpeg.AutoGen;
using Vocaluxe.Base;
using Vocaluxe.Lib.FFmpeg;
using VocaluxeLib;
using VocaluxeLib.Log;

namespace Vocaluxe.Lib.Sound.Playback.FFmpeg
{
    internal sealed unsafe class CFFmpegAudioDecoderContext : CFFmpegDecoderContextBase
    {
        private SwrContext* _SwrContext;
        private byte* _Buffer;
        private int _BufferLineSize;
        private AVChannelLayout* _ChannelLayout;
        private AVSampleFormat _OutputFormat;

        public int SamplesRate { get; private set; }

        public int ChannelCount => _ChannelLayout != null ? _ChannelLayout->nb_channels : 0;

        public int BufferSize { get; private set; }

        public byte* Buffer => _Buffer;

        protected override AVMediaType _MediaType => AVMediaType.AVMEDIA_TYPE_AUDIO;

        protected override bool _InternalInit(AVStream* avStream, AVCodecContext* codecContext)
        {
            SamplesRate = codecContext->sample_rate;
            _ChannelLayout = &codecContext->ch_layout;

            // If we use port audio, there is an issue fixed only on the master branch and never released regarding buffer size limited to 16bits
            // To make songs compatible, let's resampling them to 16bits
            // https://github.com/PortAudio/portaudio/pull/774
            _OutputFormat = codecContext->sample_fmt;
            if (CConfig.Config.Sound.PlayBackLib == EPlaybackLib.PortAudio && !_Is16BitsFormat(codecContext->sample_fmt))
            {
                _OutputFormat = AVSampleFormat.AV_SAMPLE_FMT_S16;
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
            }
            
            fixed (byte** buffer = &_Buffer)
            fixed (int* bufferLineSize = &_BufferLineSize)
            {
                BufferSize =
                    ffmpeg.av_samples_alloc(buffer, bufferLineSize, _ChannelLayout->nb_channels, codecContext->frame_size, _OutputFormat, 1);
            }

            if (BufferSize < 0)
            {
                CLog.Error("Unable to allocate the output buffer");
                return false;
            }
            return true;
        }

        private static bool _Is16BitsFormat(AVSampleFormat sampleFormat)
        {
            return sampleFormat is AVSampleFormat.AV_SAMPLE_FMT_S16 or AVSampleFormat.AV_SAMPLE_FMT_S16P;
        }

        protected override bool _ProcessFrame(AVFrame* frame)
        {
            fixed (byte** buffer = &_Buffer)
            {
                if (_SwrContext != null)
                {
                    if (ffmpeg.swr_convert(_SwrContext, buffer, frame->nb_samples, (byte**)&frame->data, frame->nb_samples) < 0)
                    {
                        return false;
                    }
                }
                else
                {
					// Since this part is only an optimisation for OpenAL, I didn't take time to test it locally (because of a lot missing configurations)
					// If there is an issue, should only be related to the way buffer are copied between linear and non linear buffers (just another method to use)
                    if (ffmpeg.av_samples_copy(buffer, (byte**)&frame->data, 0, 0, frame->nb_samples, _ChannelLayout->nb_channels, _OutputFormat) < 0)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        protected override void _InternalFree()
        {
            if (_SwrContext != null)
            {
                fixed (SwrContext** swrContext = &_SwrContext)
                {
                    ffmpeg.swr_free(swrContext);
                }
            }

            if (_Buffer != null)
            {
                fixed (byte** buffer = &_Buffer)
                {
                    ffmpeg.av_freep(buffer);
                }
            }

            _ChannelLayout = null;
        }
    }
}
