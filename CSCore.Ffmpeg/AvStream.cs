using System;
using CSCore.Ffmpeg.Interops;

namespace CSCore.Ffmpeg
{
    internal sealed class AvStream : IDisposable
    {
        private readonly unsafe AVStream* _stream;
        private unsafe AVCodecContext* _codecContext;
        private readonly FfmpegProcessingOptions _options;

        private FfmpegResampler _resampler;
        private int _targetSampleRate;
        private int _targetChannels;
        private AVSampleFormat _targetFormat;
        private readonly bool _useFfmpegResampler;

        public unsafe AVStream Stream
        {
            get
            {
                if (_stream == null)
                    return default(AVStream);
                return *_stream;
            }
        }

        public unsafe AVCodecContext* CodecContextPtr => _codecContext;

        /// <summary>
        ///     Gets the FFmpeg resampler when FFmpeg processing is selected; otherwise <c>null</c> (CSCore path).
        /// </summary>
        public unsafe FfmpegResampler Resampler
        {
            get
            {
                if (!_useFfmpegResampler || _codecContext == null)
                    return null;
                if (_resampler == null)
                    _resampler = new FfmpegResampler(_codecContext, _targetSampleRate, _targetChannels, _targetFormat);
                return _resampler;
            }
        }

        public unsafe WaveFormat GetSuggestedWaveFormat()
        {
            if (_stream == null)
                throw new InvalidOperationException("No stream selected.");

            var par = _stream->codecpar;
            var sourceFormat = (AVSampleFormat)par->format;
            int sourceSampleRate = par->sample_rate;
            int sourceChannels = par->ch_layout.nb_channels;

            if (_useFfmpegResampler)
            {
                _targetSampleRate = _options.TargetSampleRate ?? sourceSampleRate;
                _targetChannels = _options.TargetChannels ?? sourceChannels;

                int targetBits;
                AudioEncoding targetEncoding;
                ResolveOutputFormat(_options.TargetSampleFormat, sourceFormat, out _targetFormat, out targetBits,
                    out targetEncoding);

                return new WaveFormat(_targetSampleRate, targetBits, _targetChannels, targetEncoding);
            }

            int bitsPerSample;
            AudioEncoding encoding;
            switch (sourceFormat)
            {
                case AVSampleFormat.AV_SAMPLE_FMT_U8:
                case AVSampleFormat.AV_SAMPLE_FMT_U8P:
                    bitsPerSample = 8;
                    encoding = AudioEncoding.Pcm;
                    break;
                case AVSampleFormat.AV_SAMPLE_FMT_S16:
                case AVSampleFormat.AV_SAMPLE_FMT_S16P:
                    bitsPerSample = 16;
                    encoding = AudioEncoding.Pcm;
                    break;
                case AVSampleFormat.AV_SAMPLE_FMT_S32:
                case AVSampleFormat.AV_SAMPLE_FMT_S32P:
                    bitsPerSample = 32;
                    encoding = AudioEncoding.Pcm;
                    break;
                case AVSampleFormat.AV_SAMPLE_FMT_FLT:
                case AVSampleFormat.AV_SAMPLE_FMT_FLTP:
                    bitsPerSample = 32;
                    encoding = AudioEncoding.IeeeFloat;
                    break;
                case AVSampleFormat.AV_SAMPLE_FMT_DBL:
                case AVSampleFormat.AV_SAMPLE_FMT_DBLP:
                    //dbl is converted by the AvFrame.DecodePacket method
                    bitsPerSample = 32;
                    encoding = AudioEncoding.IeeeFloat;
                    break;
                default:
                    throw new NotSupportedException("Audio Sample Format not supported.");
            }

            return new WaveFormat(sourceSampleRate, bitsPerSample, sourceChannels, encoding);
        }

        private static void ResolveOutputFormat(FfmpegSampleFormat requested, AVSampleFormat sourceFormat,
            out AVSampleFormat outFormat, out int bitsPerSample, out AudioEncoding encoding)
        {
            switch (requested)
            {
                case FfmpegSampleFormat.UnsignedByte:
                    outFormat = AVSampleFormat.AV_SAMPLE_FMT_U8;
                    bitsPerSample = 8;
                    encoding = AudioEncoding.Pcm;
                    return;
                case FfmpegSampleFormat.Short:
                    outFormat = AVSampleFormat.AV_SAMPLE_FMT_S16;
                    bitsPerSample = 16;
                    encoding = AudioEncoding.Pcm;
                    return;
                case FfmpegSampleFormat.Int32:
                    outFormat = AVSampleFormat.AV_SAMPLE_FMT_S32;
                    bitsPerSample = 32;
                    encoding = AudioEncoding.Pcm;
                    return;
                case FfmpegSampleFormat.Double:
                    outFormat = AVSampleFormat.AV_SAMPLE_FMT_DBL;
                    bitsPerSample = 64;
                    encoding = AudioEncoding.IeeeFloat;
                    return;
                case FfmpegSampleFormat.Float:
                    outFormat = AVSampleFormat.AV_SAMPLE_FMT_FLT;
                    bitsPerSample = 32;
                    encoding = AudioEncoding.IeeeFloat;
                    return;
                case FfmpegSampleFormat.Auto:
                    ResolveAutoFormat(sourceFormat, out outFormat, out bitsPerSample, out encoding);
                    return;
                default:
                    throw new NotSupportedException("Target sample format not supported.");
            }
        }

        private static void ResolveAutoFormat(AVSampleFormat sourceFormat, out AVSampleFormat outFormat,
            out int bitsPerSample, out AudioEncoding encoding)
        {
            switch (sourceFormat)
            {
                case AVSampleFormat.AV_SAMPLE_FMT_U8:
                case AVSampleFormat.AV_SAMPLE_FMT_U8P:
                    outFormat = AVSampleFormat.AV_SAMPLE_FMT_U8;
                    bitsPerSample = 8;
                    encoding = AudioEncoding.Pcm;
                    return;
                case AVSampleFormat.AV_SAMPLE_FMT_S16:
                case AVSampleFormat.AV_SAMPLE_FMT_S16P:
                    outFormat = AVSampleFormat.AV_SAMPLE_FMT_S16;
                    bitsPerSample = 16;
                    encoding = AudioEncoding.Pcm;
                    return;
                case AVSampleFormat.AV_SAMPLE_FMT_S32:
                case AVSampleFormat.AV_SAMPLE_FMT_S32P:
                    outFormat = AVSampleFormat.AV_SAMPLE_FMT_S32;
                    bitsPerSample = 32;
                    encoding = AudioEncoding.Pcm;
                    return;
                default:
                    //includes float and double sources -> emit packed float
                    outFormat = AVSampleFormat.AV_SAMPLE_FMT_FLT;
                    bitsPerSample = 32;
                    encoding = AudioEncoding.IeeeFloat;
                    return;
            }
        }

        public unsafe void FlushBuffers()
        {
            if (_codecContext != null)
                FfmpegCalls.AvCodecFlushBuffers(_codecContext);
            if (_resampler != null)
                _resampler.Reset();
        }

        public AvStream(IntPtr stream)
            : this(stream, null)
        {
        }

        public unsafe AvStream(IntPtr stream, FfmpegProcessingOptions options)
        {
            if (stream == IntPtr.Zero)
                throw new ArgumentNullException("stream");

            _stream = (AVStream*)stream;
            _options = options ?? FfmpegProcessingOptions.Default;
            _useFfmpegResampler = _options.UseFfmpegResampler;

            var par = _stream->codecpar;
            var decoder = FfmpegCalls.AvCodecFindDecoder(par->codec_id);
            _codecContext = FfmpegCalls.AvCodecAllocContext3(decoder);
            FfmpegCalls.AvCodecParametersToContext(_codecContext, par);
            FfmpegCalls.AvCodecOpen(_codecContext, decoder);
        }

        public unsafe void Dispose()
        {
            GC.SuppressFinalize(this);

            if (_resampler != null)
            {
                _resampler.Dispose();
                _resampler = null;
            }

            if (_codecContext != null)
            {
                AVCodecContext* ctx = _codecContext;
                _codecContext = null;
                FfmpegCalls.AvCodecFreeContext(&ctx);
            }
        }

        ~AvStream()
        {
            Dispose();
        }
    }
}