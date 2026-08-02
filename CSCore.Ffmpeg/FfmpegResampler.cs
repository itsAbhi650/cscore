using System;
using CSCore.Ffmpeg.Interops;

namespace CSCore.Ffmpeg
{
    /// <summary>
    ///     Wraps a libswresample <c>SwrContext</c> to convert decoded frames to a fixed interleaved output format.
    /// </summary>
    internal sealed class FfmpegResampler : IDisposable
    {
        private readonly int _outSampleRate;
        private readonly int _outChannels;
        private readonly AVSampleFormat _outFormat;
        private readonly int _outBytesPerSample;
        private readonly int _sourceSampleRate;

        private unsafe SwrContext* _swr;
        private int _inSampleRate;

        public unsafe FfmpegResampler(AVCodecContext* codecContext, int outSampleRate, int outChannels,
            AVSampleFormat outFormat)
        {
            _outSampleRate = outSampleRate;
            _outChannels = outChannels;
            _outFormat = outFormat;
            _outBytesPerSample = FfmpegCalls.AvGetBytesPerSample(outFormat);
            _sourceSampleRate = codecContext->sample_rate;
        }

        /// <summary>
        ///     Converts a single decoded frame into the target format and appends it to <paramref name="buffer" /> at the
        ///     given <paramref name="offset" />. Returns the number of bytes written.
        /// </summary>
        public unsafe int Convert(AVFrame* frame, ref byte[] buffer, int offset)
        {
            EnsureInitialized(frame);

            long delay = FfmpegCalls.SwrGetDelay(_swr, _inSampleRate);
            int maxOutSamples = (int) FfmpegCalls.AvRescaleRnd(delay + frame->nb_samples, _outSampleRate,
                _inSampleRate, AVRounding.AV_ROUND_UP);
            if (maxOutSamples <= 0)
                return 0;

            int maxOutBytes = maxOutSamples * _outChannels * _outBytesPerSample;
            EnsureBufferCapacity(ref buffer, offset + maxOutBytes);

            int convertedSamples;
            fixed (byte* pOut = &buffer[offset])
            {
                byte* outData = pOut;
                convertedSamples = FfmpegCalls.SwrConvert(_swr, &outData, maxOutSamples, frame->extended_data,
                    frame->nb_samples);
            }

            return convertedSamples * _outChannels * _outBytesPerSample;
        }

        /// <summary>
        ///     Discards any buffered samples, e.g. after a seek. The context is rebuilt on the next <see cref="Convert" />.
        /// </summary>
        public unsafe void Reset()
        {
            FreeContext();
        }

        private unsafe void EnsureInitialized(AVFrame* frame)
        {
            if (_swr != null)
                return;

            _inSampleRate = frame->sample_rate != 0 ? frame->sample_rate : _sourceSampleRate;
            var inFormat = (AVSampleFormat) frame->format;

            AVChannelLayout outLayout = default(AVChannelLayout);
            FfmpegCalls.AvChannelLayoutDefault(&outLayout, _outChannels);
            try
            {
                _swr = FfmpegCalls.SwrAllocSetOpts2(&outLayout, _outFormat, _outSampleRate, &frame->ch_layout,
                    inFormat, _inSampleRate);
                FfmpegCalls.SwrInit(_swr);
            }
            finally
            {
                FfmpegCalls.AvChannelLayoutUninit(&outLayout);
            }
        }

        private static void EnsureBufferCapacity(ref byte[] buffer, int requiredLength)
        {
            if (buffer != null && buffer.Length >= requiredLength)
                return;

            byte[] resized = new byte[requiredLength];
            if (buffer != null)
                Buffer.BlockCopy(buffer, 0, resized, 0, buffer.Length);
            buffer = resized;
        }

        private unsafe void FreeContext()
        {
            if (_swr != null)
            {
                SwrContext* swr = _swr;
                _swr = null;
                FfmpegCalls.SwrFree(&swr);
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            FreeContext();
        }

        ~FfmpegResampler()
        {
            FreeContext();
        }
    }
}
