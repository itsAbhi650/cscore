using System;
using System.Diagnostics;
using System.IO;
using CSCore.Ffmpeg.Interops;

namespace CSCore.Ffmpeg
{
    /// <summary>
    ///     A generic FFmpeg based audio encoder. It converts raw audio (in a given <see cref="WaveFormat" />) to a
    ///     compressed format such as MP3, AAC, FLAC or Opus and muxes it into the matching container.
    /// </summary>
    /// <remarks>
    ///     This is an optional FFmpeg capability. It does not affect or replace CSCore's own encoders; it simply lets the
    ///     caller choose FFmpeg for encoding when desired.
    /// </remarks>
    public sealed class FfmpegEncoder : IDisposable, IWriteable
    {
        private readonly WaveFormat _inputFormat;
        private readonly AVSampleFormat _inputSampleFormat;
        private readonly bool _ownsAvio;

        private FfmpegStream _ffmpegStream;
        private unsafe AVFormatContext* _formatContext;
        private unsafe AVCodecContext* _codecContext;
        private unsafe AVStream* _stream;
        private unsafe SwrContext* _swr;
        private unsafe AVAudioFifo* _fifo;
        private unsafe AVFrame* _frame;
        private unsafe AVPacket* _packet;

        private int _outSampleRate;
        private int _outChannels;
        private AVSampleFormat _outSampleFormat;
        private int _frameSize;
        private long _nextPts;
        private bool _disposed;

        /// <summary>
        ///     Initializes a new instance of the <see cref="FfmpegEncoder" /> class that writes to a file. The container and
        ///     the default codec are derived from the file extension.
        /// </summary>
        /// <param name="fileName">The output file name (e.g. <c>out.mp3</c>).</param>
        /// <param name="inputFormat">The <see cref="WaveFormat" /> of the raw audio passed to <see cref="Write" />.</param>
        /// <param name="bitRate">The target bit rate in bits per second.</param>
        /// <param name="codecName">
        ///     An optional FFmpeg encoder name (e.g. <c>libmp3lame</c>, <c>aac</c>, <c>flac</c>); when <c>null</c> the
        ///     container's default audio codec is used.
        /// </param>
        public unsafe FfmpegEncoder(string fileName, WaveFormat inputFormat, int bitRate = 192000,
            string codecName = null)
        {
            if (fileName == null)
                throw new ArgumentNullException("fileName");
            if (inputFormat == null)
                throw new ArgumentNullException("inputFormat");

            _inputFormat = inputFormat;
            _inputSampleFormat = GetInputSampleFormat(inputFormat);
            _ownsAvio = true;

            _formatContext = FfmpegCalls.AvformatAllocOutputContext(null, fileName);
            Initialize(codecName, bitRate);
            FfmpegCalls.AvioOpen(_formatContext, fileName);
            FfmpegCalls.AvformatWriteHeader(_formatContext);
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="FfmpegEncoder" /> class that writes to a <see cref="Stream" />.
        /// </summary>
        /// <param name="stream">The writeable output stream.</param>
        /// <param name="containerFormatName">The FFmpeg muxer/container name (e.g. <c>mp3</c>, <c>adts</c>, <c>flac</c>, <c>ipod</c>).</param>
        /// <param name="inputFormat">The <see cref="WaveFormat" /> of the raw audio passed to <see cref="Write" />.</param>
        /// <param name="bitRate">The target bit rate in bits per second.</param>
        /// <param name="codecName">
        ///     An optional FFmpeg encoder name; when <c>null</c> the container's default audio codec is used.
        /// </param>
        public unsafe FfmpegEncoder(Stream stream, string containerFormatName, WaveFormat inputFormat,
            int bitRate = 192000, string codecName = null)
        {
            if (stream == null)
                throw new ArgumentNullException("stream");
            if (!stream.CanWrite)
                throw new ArgumentException("Stream is not writeable.", "stream");
            if (String.IsNullOrEmpty(containerFormatName))
                throw new ArgumentNullException("containerFormatName");
            if (inputFormat == null)
                throw new ArgumentNullException("inputFormat");

            _inputFormat = inputFormat;
            _inputSampleFormat = GetInputSampleFormat(inputFormat);
            _ownsAvio = false;

            _formatContext = FfmpegCalls.AvformatAllocOutputContext(containerFormatName, null);
            _ffmpegStream = new FfmpegStream(stream);
            _formatContext->pb = (AVIOContext*)_ffmpegStream.AvioContext.ContextPtr;

            Initialize(codecName, bitRate);
            FfmpegCalls.AvformatWriteHeader(_formatContext);
        }

        /// <summary>
        ///     Gets the <see cref="WaveFormat" /> expected by <see cref="Write" />.
        /// </summary>
        public WaveFormat InputFormat
        {
            get { return _inputFormat; }
        }

        private unsafe void Initialize(string codecName, int bitRate)
        {
            AVCodec* encoder;
            if (!String.IsNullOrEmpty(codecName))
                encoder = FfmpegCalls.AvCodecFindEncoderByName(codecName);
            else
                encoder = FfmpegCalls.AvCodecFindEncoder(_formatContext->oformat->audio_codec);

            _stream = FfmpegCalls.AvformatNewStream(_formatContext, encoder);
            _codecContext = FfmpegCalls.AvCodecAllocContext3(encoder);

            _outSampleRate = _inputFormat.SampleRate;
            _outChannels = _inputFormat.Channels;
            _outSampleFormat = SelectSampleFormat(encoder);

            _codecContext->sample_rate = _outSampleRate;
            _codecContext->sample_fmt = _outSampleFormat;
            _codecContext->bit_rate = bitRate;
            FfmpegCalls.AvChannelLayoutDefault(&_codecContext->ch_layout, _outChannels);
            _codecContext->time_base = new AVRational { num = 1, den = _outSampleRate };

            //some containers require the global header flag on the codec
            if ((_formatContext->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                _codecContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

            FfmpegCalls.AvCodecOpen(_codecContext, encoder);
            FfmpegCalls.AvCodecParametersFromContext(_stream->codecpar, _codecContext);
            _stream->time_base = _codecContext->time_base;

            //variable-frame-size codecs report 0; pick a reasonable chunk size
            _frameSize = _codecContext->frame_size > 0 ? _codecContext->frame_size : 4096;

            AVChannelLayout inLayout = default(AVChannelLayout);
            FfmpegCalls.AvChannelLayoutDefault(&inLayout, _inputFormat.Channels);
            try
            {
                _swr = FfmpegCalls.SwrAllocSetOpts2(&_codecContext->ch_layout, _outSampleFormat, _outSampleRate,
                    &inLayout, _inputSampleFormat, _inputFormat.SampleRate);
                FfmpegCalls.SwrInit(_swr);
            }
            finally
            {
                FfmpegCalls.AvChannelLayoutUninit(&inLayout);
            }

            _fifo = FfmpegCalls.AvAudioFifoAlloc(_outSampleFormat, _outChannels, _frameSize);

            _packet = FfmpegCalls.AvPacketAlloc();
            _frame = FfmpegCalls.AvFrameAlloc();
            _frame->nb_samples = _frameSize;
            _frame->format = (int)_outSampleFormat;
            _frame->sample_rate = _outSampleRate;
            FfmpegCalls.AvChannelLayoutDefault(&_frame->ch_layout, _outChannels);
            FfmpegCalls.AvFrameGetBuffer(_frame, 0);
        }

        private unsafe AVSampleFormat SelectSampleFormat(AVCodec* encoder)
        {
            AVSampleFormat[] supported = FfmpegCalls.AvCodecGetSupportedSampleFormats(_codecContext, encoder);
            if (supported.Length == 0)
                return AVSampleFormat.AV_SAMPLE_FMT_FLTP; //null result => any format is accepted
            return supported[0];
        }

        /// <summary>
        ///     Encodes a block of raw audio in the <see cref="InputFormat" />.
        /// </summary>
        /// <param name="buffer">The raw audio.</param>
        /// <param name="offset">The zero-based offset into <paramref name="buffer" />.</param>
        /// <param name="count">The number of bytes to encode.</param>
        public unsafe void Write(byte[] buffer, int offset, int count)
        {
            if (_disposed)
                throw new ObjectDisposedException("FfmpegEncoder");
            if (buffer == null)
                throw new ArgumentNullException("buffer");
            if (count <= 0)
                return;

            int inputSamples = count / _inputFormat.BlockAlign;
            if (inputSamples <= 0)
                return;

            fixed (byte* pInput = &buffer[offset])
            {
                byte* inputPtr = pInput;
                BufferConvertedSamples(&inputPtr, inputSamples);
            }

            DrainFifo();
        }

        private unsafe void BufferConvertedSamples(byte** inputData, int inputSamples)
        {
            long delay = FfmpegCalls.SwrGetDelay(_swr, _inputFormat.SampleRate);
            int maxOutSamples = (int)FfmpegCalls.AvRescaleRnd(delay + inputSamples, _outSampleRate,
                _inputFormat.SampleRate, AVRounding.AV_ROUND_UP);
            if (maxOutSamples <= 0)
                return;

            byte** convertedData = null;
            try
            {
                int lineSize;
                int allocResult = ffmpeg.av_samples_alloc_array_and_samples(&convertedData, &lineSize, _outChannels,
                    maxOutSamples, _outSampleFormat, 0);
                FfmpegException.Try(allocResult, "av_samples_alloc_array_and_samples");

                int converted = FfmpegCalls.SwrConvert(_swr, convertedData, maxOutSamples, inputData, inputSamples);
                if (converted > 0)
                    FfmpegCalls.AvAudioFifoWrite(_fifo, (void**)convertedData, converted);
            }
            finally
            {
                if (convertedData != null)
                {
                    ffmpeg.av_freep(&convertedData[0]);
                    ffmpeg.av_freep(&convertedData);
                }
            }
        }

        private unsafe void DrainFifo()
        {
            while (FfmpegCalls.AvAudioFifoSize(_fifo) >= _frameSize)
                EncodeFifoFrame(_frameSize);
        }

        private unsafe void EncodeFifoFrame(int nbSamples)
        {
            FfmpegCalls.AvFrameMakeWritable(_frame);
            _frame->nb_samples = nbSamples;
            FfmpegCalls.AvAudioFifoRead(_fifo, (void**)_frame->extended_data, nbSamples);

            _frame->pts = _nextPts;
            _nextPts += nbSamples;

            EncodeFrame(_frame);
        }

        private unsafe void EncodeFrame(AVFrame* frame)
        {
            while (!FfmpegCalls.AvCodecSendFrame(_codecContext, frame))
                DrainPackets();
            DrainPackets();
        }

        private unsafe void DrainPackets()
        {
            while (FfmpegCalls.AvCodecReceivePacket(_codecContext, _packet))
            {
                FfmpegCalls.AvPacketRescaleTs(_packet, _codecContext->time_base, _stream->time_base);
                _packet->stream_index = _stream->index;
                FfmpegCalls.AvInterleavedWriteFrame(_formatContext, _packet);
                FfmpegCalls.FreePacket(_packet);
            }
        }

        private unsafe void Flush()
        {
            //extract any samples still buffered inside the resampler
            byte** convertedData = null;
            try
            {
                long delay = FfmpegCalls.SwrGetDelay(_swr, _outSampleRate);
                if (delay > 0)
                {
                    int lineSize;
                    int allocResult = ffmpeg.av_samples_alloc_array_and_samples(&convertedData, &lineSize,
                        _outChannels, (int)delay, _outSampleFormat, 0);
                    FfmpegException.Try(allocResult, "av_samples_alloc_array_and_samples");
                    int converted = FfmpegCalls.SwrConvert(_swr, convertedData, (int)delay, null, 0);
                    if (converted > 0)
                        FfmpegCalls.AvAudioFifoWrite(_fifo, (void**)convertedData, converted);
                }
            }
            finally
            {
                if (convertedData != null)
                {
                    ffmpeg.av_freep(&convertedData[0]);
                    ffmpeg.av_freep(&convertedData);
                }
            }

            DrainFifo();

            int remaining = FfmpegCalls.AvAudioFifoSize(_fifo);
            if (remaining > 0)
                EncodeFifoFrame(remaining);

            //flush the encoder by sending a null frame
            EncodeFrame(null);

            FfmpegCalls.AvWriteTrailer(_formatContext);
        }

        /// <summary>
        ///     Reads the whole <paramref name="source" /> and encodes it with the given <paramref name="encoder" />.
        /// </summary>
        /// <param name="encoder">The encoder to write to.</param>
        /// <param name="source">The audio source to encode.</param>
        public static void EncodeWholeSource(FfmpegEncoder encoder, IWaveSource source)
        {
            if (encoder == null)
                throw new ArgumentNullException("encoder");
            if (source == null)
                throw new ArgumentNullException("source");

            var buffer = new byte[source.WaveFormat.BytesPerSecond * 4];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                encoder.Write(buffer, 0, read);
        }

        /// <summary>
        ///     Creates a new <see cref="FfmpegEncoder" /> configured to produce an MP3 file.
        /// </summary>
        /// <param name="inputFormat">The <see cref="WaveFormat" /> of the raw audio to encode.</param>
        /// <param name="fileName">The output file name.</param>
        /// <param name="bitRate">The target bit rate in bits per second.</param>
        // ReSharper disable once InconsistentNaming
        public static FfmpegEncoder CreateMP3Encoder(WaveFormat inputFormat, string fileName, int bitRate = 192000)
        {
            return new FfmpegEncoder(fileName, inputFormat, bitRate);
        }

        private static AVSampleFormat GetInputSampleFormat(WaveFormat format)
        {
            if (format.WaveFormatTag == AudioEncoding.IeeeFloat)
            {
                if (format.BitsPerSample == 32)
                    return AVSampleFormat.AV_SAMPLE_FMT_FLT;
                if (format.BitsPerSample == 64)
                    return AVSampleFormat.AV_SAMPLE_FMT_DBL;
            }
            else if (format.WaveFormatTag == AudioEncoding.Pcm)
            {
                switch (format.BitsPerSample)
                {
                    case 8:
                        return AVSampleFormat.AV_SAMPLE_FMT_U8;
                    case 16:
                        return AVSampleFormat.AV_SAMPLE_FMT_S16;
                    case 32:
                        return AVSampleFormat.AV_SAMPLE_FMT_S32;
                }
            }

            throw new NotSupportedException("The input WaveFormat is not supported by the FFmpeg encoder.");
        }

        /// <summary>
        ///     Finalizes the encoding and releases all resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private unsafe void Dispose(bool disposing)
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                if (_formatContext != null && _codecContext != null)
                    Flush();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("FfmpegEncoder flush failed: " + ex.Message);
            }

            if (_frame != null)
            {
                AVFrame* frame = _frame;
                _frame = null;
                ffmpeg.av_frame_free(&frame);
            }

            if (_packet != null)
            {
                AVPacket* packet = _packet;
                _packet = null;
                ffmpeg.av_packet_free(&packet);
            }

            if (_fifo != null)
            {
                FfmpegCalls.AvAudioFifoFree(_fifo);
                _fifo = null;
            }

            if (_swr != null)
            {
                SwrContext* swr = _swr;
                _swr = null;
                FfmpegCalls.SwrFree(&swr);
            }

            if (_codecContext != null)
            {
                AVCodecContext* ctx = _codecContext;
                _codecContext = null;
                FfmpegCalls.AvCodecFreeContext(&ctx);
            }

            if (_formatContext != null)
            {
                if (_ownsAvio && _formatContext->pb != null)
                {
                    AVIOContext** pb = &_formatContext->pb;
                    FfmpegCalls.AvioClosep(pb);
                }
                else
                {
                    //custom stream avio is owned by _ffmpegStream; detach so it is not freed twice
                    _formatContext->pb = null;
                }

                FfmpegCalls.AvformatFreeContext(_formatContext);
                _formatContext = null;
            }

            if (disposing && _ffmpegStream != null)
            {
                _ffmpegStream.Dispose();
                _ffmpegStream = null;
            }
        }

        /// <summary>
        ///     Finalizes an instance of the <see cref="FfmpegEncoder" /> class.
        /// </summary>
        ~FfmpegEncoder()
        {
            Dispose(false);
        }
    }
}
