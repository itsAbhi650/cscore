using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using CSCore.Ffmpeg.Interops;

namespace CSCore.Ffmpeg
{
    internal class FfmpegCalls
    {
        [Flags]
        public enum SeekFlags
        {
            SeekSet = 0,
            SeekCur = 1,
            SeekEnd = 2,
            SeekSize = 0x10000,
            SeekForce = 0x20000
        }

        public delegate int AvioReadData(IntPtr opaque, IntPtr buffer, int bufferSize);

        public delegate int AvioWriteData(IntPtr opaque, IntPtr buffer, int bufferSize);

        public delegate long AvioSeek(IntPtr opaque, long offset, SeekFlags whence);

        static FfmpegCalls()
        {
            string platform;
            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT:
                case PlatformID.Win32S:
                case PlatformID.Win32Windows:
                    platform = "windows";
                    break;
                case PlatformID.Unix:
                case PlatformID.MacOSX:
                    platform = "unix";
                    break;
                default:
                    throw new PlatformNotSupportedException();
            }

            var assemblyDirectory = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            if (assemblyDirectory != null)
            {
                string path = Path.Combine(
                    assemblyDirectory,
                    Path.Combine("FFmpeg", Path.Combine("bin",
                        Path.Combine(platform, IntPtr.Size == 8 ? "x64" : "x86"))));

                InteropHelper.RegisterLibrariesSearchPath(path);
            }

            FfmpegConfigurationSection ffmpegSettings = null;
            try
            {
                ffmpegSettings = (FfmpegConfigurationSection)ConfigurationManager.GetSection("ffmpeg");
            }
            catch (ConfigurationException)
            {
                //the optional ffmpeg config section could not be loaded; continue with defaults
            }
            if (ffmpegSettings != null)
            {
                if (!String.IsNullOrEmpty(ffmpegSettings.HttpProxy))
                {
                    Environment.SetEnvironmentVariable("http_proxy", ffmpegSettings.HttpProxy);
                    Environment.SetEnvironmentVariable("no_proxy", ffmpegSettings.ProxyWhitelist);
                }
                if (ffmpegSettings.LogLevel != null)
                {
                    FfmpegUtils.LogLevel = ffmpegSettings.LogLevel.Value;
                }
            }

            ffmpeg.avformat_network_init();
        }

        internal static unsafe AVOutputFormat[] GetOutputFormats()
        {
            List<AVOutputFormat> formats = new List<AVOutputFormat>();
            void* opaque = null;
            AVOutputFormat* format;
            while ((format = ffmpeg.av_muxer_iterate(&opaque)) != null)
                formats.Add(*format);
            return formats.ToArray();
        }

        internal static unsafe AVInputFormat[] GetInputFormats()
        {
            List<AVInputFormat> formats = new List<AVInputFormat>();
            void* opaque = null;
            AVInputFormat* format;
            while ((format = ffmpeg.av_demuxer_iterate(&opaque)) != null)
                formats.Add(*format);
            return formats.ToArray();
        }

        internal static unsafe List<AVCodecID> GetCodecOfCodecTag(AVCodecTag** codecTag)
        {
            List<AVCodecID> codecs = new List<AVCodecID>();
            uint i = 0;
            AVCodecID codecId;
            while ((codecId = ffmpeg.av_codec_get_id(codecTag, i++)) != AVCodecID.AV_CODEC_ID_NONE)
                codecs.Add(codecId);
            return codecs;
        }

        internal static unsafe IntPtr AvMalloc(int bufferSize)
        {
            void* buffer = ffmpeg.av_malloc((ulong) bufferSize);
            IntPtr ptr = new IntPtr(buffer);
            if (ptr == IntPtr.Zero)
                throw new OutOfMemoryException("Could not allocate memory.");
            return ptr;
        }


        internal static unsafe void AvFree(IntPtr buffer)
        {
            ffmpeg.av_free((void*) buffer);
        }

        internal static unsafe AVIOContext* AvioAllocContext(AvioBuffer buffer, bool writeable, IntPtr userData,
            AvioReadData readData, AvioWriteData writeData, AvioSeek seek)
        {
            byte* bufferPtr = (byte*) buffer.Buffer;

            var avioContext = ffmpeg.avio_alloc_context(
                bufferPtr,
                buffer.BufferSize,
                writeable ? 1 : 0,
                (void*) userData,
                readData, writeData, seek);
            if (avioContext == null)
            {
                throw new FfmpegException("Could not allocate avio-context.", "avio_alloc_context");
            }

            return avioContext;
        }

        internal static unsafe AVFormatContext* AvformatAllocContext()
        {
            var formatContext = ffmpeg.avformat_alloc_context();
            if (formatContext == null)
            {
                throw new FfmpegException("Could not allocate avformat-context.", "avformat_alloc_context");
            }

            return formatContext;
        }

        internal static unsafe void AvformatOpenInput(AVFormatContext** formatContext, AvioContext avioContext)
        {
            (*formatContext)->pb = (AVIOContext*) avioContext.ContextPtr;
            int result = ffmpeg.avformat_open_input(formatContext, "DUMMY-FILENAME", null, null);
            FfmpegException.Try(result, "avformat_open_input");
        }

        internal static unsafe void AvformatOpenInput(AVFormatContext** formatContext, string url)
        {
            int result = ffmpeg.avformat_open_input(formatContext, url, null, null);
            FfmpegException.Try(result, "avformat_open_input");
        }

        internal static unsafe void AvformatCloseInput(AVFormatContext** formatContext)
        {
            ffmpeg.avformat_close_input(formatContext);
        }

        internal static unsafe void AvFormatFindStreamInfo(AVFormatContext* formatContext)
        {
            int result = ffmpeg.avformat_find_stream_info(formatContext, null);
            FfmpegException.Try(result, "avformat_find_stream_info");
        }

        internal static unsafe int AvFindBestStreamInfo(AVFormatContext* formatContext)
        {
            int result = ffmpeg.av_find_best_stream(
                formatContext,
                AVMediaType.AVMEDIA_TYPE_AUDIO,
                -1, -1, null, 0);
            FfmpegException.Try(result, "av_find_best_stream");

            return result; //stream index
        }

        internal static unsafe AVCodec* AvCodecFindDecoder(AVCodecID codecId)
        {
            var decoder = ffmpeg.avcodec_find_decoder(codecId);
            if (decoder == null)
                throw new FfmpegException(String.Format("Failed to find a decoder for CodecId {0}.", codecId), "avcodec_find_decoder");
            return decoder;
        }

        internal static unsafe void AvCodecOpen(AVCodecContext* codecContext, AVCodec* codec)
        {
            int result = ffmpeg.avcodec_open2(codecContext, codec, null);
            FfmpegException.Try(result, "avcodec_open2");
        }

        internal static unsafe AVFrame* AvFrameAlloc()
        {
            var frame = ffmpeg.av_frame_alloc();
            if (frame == null)
            {
                throw new FfmpegException("Could not allocate frame.", "av_frame_alloc");
            }

            return frame;
        }

        internal static unsafe void AvFrameFree(AVFrame* frame)
        {
            ffmpeg.av_frame_free(&frame);
        }

        internal static unsafe AVPacket* AvPacketAlloc()
        {
            var pkt = ffmpeg.av_packet_alloc();
            if (pkt == null)
                throw new FfmpegException("Could not allocate packet.", "av_packet_alloc");
            return pkt;
        }

        internal static unsafe void AvPacketFree(AVPacket** packet)
        {
            ffmpeg.av_packet_free(packet);
        }

        internal static unsafe void FreePacket(AVPacket* packet)
        {
            ffmpeg.av_packet_unref(packet);
        }

        internal static unsafe bool AvReadFrame(AvFormatContext formatContext, AVPacket* packet)
        {
            int result = ffmpeg.av_read_frame((AVFormatContext*) formatContext.FormatPtr, packet);
            return result >= 0;
        }

        internal static unsafe AVCodecContext* AvCodecAllocContext3(AVCodec* codec)
        {
            var ctx = ffmpeg.avcodec_alloc_context3(codec);
            if (ctx == null)
                throw new FfmpegException("Could not allocate codec context.", "avcodec_alloc_context3");
            return ctx;
        }

        internal static unsafe void AvCodecParametersToContext(AVCodecContext* ctx, AVCodecParameters* par)
        {
            int result = ffmpeg.avcodec_parameters_to_context(ctx, par);
            FfmpegException.Try(result, "avcodec_parameters_to_context");
        }

        internal static unsafe void AvCodecFreeContext(AVCodecContext** ctx)
        {
            ffmpeg.avcodec_free_context(ctx);
        }

        // Returns false when the decoder needs more input (EAGAIN) or stream is finished (EOF).
        internal static unsafe bool AvCodecSendPacket(AVCodecContext* codecContext, AVPacket* packet)
        {
            const int AverroreAgain = -11;
            int result = ffmpeg.avcodec_send_packet(codecContext, packet);
            if (result == AverroreAgain)
                return false;
            FfmpegException.Try(result, "avcodec_send_packet");
            return true;
        }

        // Returns false when no frame is available yet (EAGAIN) or stream is finished (EOF).
        internal static unsafe bool AvCodecReceiveFrame(AVCodecContext* codecContext, AVFrame* frame)
        {
            const int AverroreAgain = -11;
            const int AverroreEof = -541478725;
            int result = ffmpeg.avcodec_receive_frame(codecContext, frame);
            if (result == AverroreAgain || result == AverroreEof)
                return false;
            FfmpegException.Try(result, "avcodec_receive_frame");
            return true;
        }

        internal static int AvGetBytesPerSample(AVSampleFormat sampleFormat)
        {
            int dataSize = ffmpeg.av_get_bytes_per_sample(sampleFormat);
            if (dataSize <= 0)
            {
                throw new FfmpegException("Could not calculate data size.");
            }
            return dataSize;
        }

        internal static bool AvSampleFmtIsPlanar(AVSampleFormat sampleFormat)
        {
            return ffmpeg.av_sample_fmt_is_planar(sampleFormat) == 1;
        }

        internal static unsafe void AvCodecFlushBuffers(AVCodecContext* codecContext)
        {
            ffmpeg.avcodec_flush_buffers(codecContext);
        }

        internal static long AvRescaleRnd(long a, long b, long c, AVRounding rounding)
        {
            return ffmpeg.av_rescale_rnd(a, b, c, rounding);
        }

        internal static unsafe void AvChannelLayoutDefault(AVChannelLayout* channelLayout, int channels)
        {
            ffmpeg.av_channel_layout_default(channelLayout, channels);
        }

        internal static unsafe void AvChannelLayoutUninit(AVChannelLayout* channelLayout)
        {
            ffmpeg.av_channel_layout_uninit(channelLayout);
        }

        internal static unsafe SwrContext* SwrAllocSetOpts2(AVChannelLayout* outLayout, AVSampleFormat outFormat,
            int outSampleRate, AVChannelLayout* inLayout, AVSampleFormat inFormat, int inSampleRate)
        {
            SwrContext* swr = null;
            int result = ffmpeg.swr_alloc_set_opts2(&swr, outLayout, outFormat, outSampleRate, inLayout, inFormat,
                inSampleRate, 0, null);
            FfmpegException.Try(result, "swr_alloc_set_opts2");
            if (swr == null)
                throw new FfmpegException("Could not allocate resampler context.", "swr_alloc_set_opts2");
            return swr;
        }

        internal static unsafe void SwrInit(SwrContext* swr)
        {
            int result = ffmpeg.swr_init(swr);
            FfmpegException.Try(result, "swr_init");
        }

        internal static unsafe int SwrConvert(SwrContext* swr, byte** outData, int outCount, byte** inData, int inCount)
        {
            int result = ffmpeg.swr_convert(swr, outData, outCount, inData, inCount);
            FfmpegException.Try(result, "swr_convert");
            return result;
        }

        internal static unsafe long SwrGetDelay(SwrContext* swr, long baseRate)
        {
            return ffmpeg.swr_get_delay(swr, baseRate);
        }

        internal static unsafe void SwrFree(SwrContext** swr)
        {
            ffmpeg.swr_free(swr);
        }

        internal static unsafe AVFormatContext* AvformatAllocOutputContext(string formatName, string fileName)
        {
            AVFormatContext* ctx = null;
            int result = ffmpeg.avformat_alloc_output_context2(&ctx, null, formatName, fileName);
            FfmpegException.Try(result, "avformat_alloc_output_context2");
            if (ctx == null)
                throw new FfmpegException("Could not allocate output format context.", "avformat_alloc_output_context2");
            return ctx;
        }

        internal static unsafe void AvformatFreeContext(AVFormatContext* ctx)
        {
            ffmpeg.avformat_free_context(ctx);
        }

        internal static unsafe AVCodec* AvCodecFindEncoder(AVCodecID codecId)
        {
            var encoder = ffmpeg.avcodec_find_encoder(codecId);
            if (encoder == null)
                throw new FfmpegException(String.Format("Failed to find an encoder for CodecId {0}.", codecId), "avcodec_find_encoder");
            return encoder;
        }

        internal static unsafe AVCodec* AvCodecFindEncoderByName(string name)
        {
            var encoder = ffmpeg.avcodec_find_encoder_by_name(name);
            if (encoder == null)
                throw new FfmpegException(String.Format("Failed to find an encoder named '{0}'.", name), "avcodec_find_encoder_by_name");
            return encoder;
        }

        internal static unsafe AVStream* AvformatNewStream(AVFormatContext* ctx, AVCodec* codec)
        {
            var stream = ffmpeg.avformat_new_stream(ctx, codec);
            if (stream == null)
                throw new FfmpegException("Could not allocate output stream.", "avformat_new_stream");
            return stream;
        }

        internal static unsafe AVSampleFormat[] AvCodecGetSupportedSampleFormats(AVCodecContext* ctx, AVCodec* codec)
        {
            void* configs = null;
            int count = 0;
            int result = ffmpeg.avcodec_get_supported_config(ctx, codec, AVCodecConfig.AV_CODEC_CONFIG_SAMPLE_FORMAT,
                0, &configs, &count);
            FfmpegException.Try(result, "avcodec_get_supported_config");

            if (configs == null)
                return new AVSampleFormat[0]; //null => every format is supported

            var formats = new List<AVSampleFormat>();
            AVSampleFormat* p = (AVSampleFormat*)configs;
            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                    formats.Add(p[i]);
            }
            else
            {
                while (*p != AVSampleFormat.AV_SAMPLE_FMT_NONE)
                {
                    formats.Add(*p);
                    p++;
                }
            }
            return formats.ToArray();
        }

        internal static unsafe void AvCodecParametersFromContext(AVCodecParameters* par, AVCodecContext* ctx)
        {
            int result = ffmpeg.avcodec_parameters_from_context(par, ctx);
            FfmpegException.Try(result, "avcodec_parameters_from_context");
        }

        // Returns false when the encoder cannot accept input right now (EAGAIN).
        internal static unsafe bool AvCodecSendFrame(AVCodecContext* ctx, AVFrame* frame)
        {
            const int AverroreAgain = -11;
            int result = ffmpeg.avcodec_send_frame(ctx, frame);
            if (result == AverroreAgain)
                return false;
            FfmpegException.Try(result, "avcodec_send_frame");
            return true;
        }

        // Returns false when no packet is available yet (EAGAIN) or the encoder is fully flushed (EOF).
        internal static unsafe bool AvCodecReceivePacket(AVCodecContext* ctx, AVPacket* packet)
        {
            const int AverroreAgain = -11;
            int result = ffmpeg.avcodec_receive_packet(ctx, packet);
            if (result == AverroreAgain || result == ffmpeg.AVERROR_EOF)
                return false;
            FfmpegException.Try(result, "avcodec_receive_packet");
            return true;
        }

        internal static unsafe void AvFrameGetBuffer(AVFrame* frame, int align)
        {
            int result = ffmpeg.av_frame_get_buffer(frame, align);
            FfmpegException.Try(result, "av_frame_get_buffer");
        }

        internal static unsafe void AvFrameMakeWritable(AVFrame* frame)
        {
            int result = ffmpeg.av_frame_make_writable(frame);
            FfmpegException.Try(result, "av_frame_make_writable");
        }

        internal static unsafe void AvInterleavedWriteFrame(AVFormatContext* ctx, AVPacket* packet)
        {
            int result = ffmpeg.av_interleaved_write_frame(ctx, packet);
            FfmpegException.Try(result, "av_interleaved_write_frame");
        }

        internal static unsafe void AvformatWriteHeader(AVFormatContext* ctx)
        {
            int result = ffmpeg.avformat_write_header(ctx, null);
            FfmpegException.Try(result, "avformat_write_header");
        }

        internal static unsafe void AvWriteTrailer(AVFormatContext* ctx)
        {
            int result = ffmpeg.av_write_trailer(ctx);
            FfmpegException.Try(result, "av_write_trailer");
        }

        internal static unsafe void AvioOpen(AVFormatContext* ctx, string url)
        {
            AVIOContext* pb = null;
            int result = ffmpeg.avio_open(&pb, url, ffmpeg.AVIO_FLAG_WRITE);
            FfmpegException.Try(result, "avio_open");
            ctx->pb = pb;
        }

        internal static unsafe void AvioClosep(AVIOContext** pb)
        {
            ffmpeg.avio_closep(pb);
        }

        internal static unsafe void AvPacketRescaleTs(AVPacket* packet, AVRational src, AVRational dst)
        {
            ffmpeg.av_packet_rescale_ts(packet, src, dst);
        }

        internal static unsafe AVAudioFifo* AvAudioFifoAlloc(AVSampleFormat sampleFormat, int channels, int nbSamples)
        {
            var fifo = ffmpeg.av_audio_fifo_alloc(sampleFormat, channels, nbSamples < 1 ? 1 : nbSamples);
            if (fifo == null)
                throw new FfmpegException("Could not allocate audio fifo.", "av_audio_fifo_alloc");
            return fifo;
        }

        internal static unsafe int AvAudioFifoWrite(AVAudioFifo* fifo, void** data, int nbSamples)
        {
            int result = ffmpeg.av_audio_fifo_write(fifo, data, nbSamples);
            FfmpegException.Try(result, "av_audio_fifo_write");
            return result;
        }

        internal static unsafe int AvAudioFifoRead(AVAudioFifo* fifo, void** data, int nbSamples)
        {
            int result = ffmpeg.av_audio_fifo_read(fifo, data, nbSamples);
            FfmpegException.Try(result, "av_audio_fifo_read");
            return result;
        }

        internal static unsafe int AvAudioFifoSize(AVAudioFifo* fifo)
        {
            return ffmpeg.av_audio_fifo_size(fifo);
        }

        internal static unsafe void AvAudioFifoFree(AVAudioFifo* fifo)
        {
            ffmpeg.av_audio_fifo_free(fifo);
        }

        internal static long AvRescaleQ(long a, AVRational bq, AVRational cq)
        {
            return ffmpeg.av_rescale_q(a, bq, cq);
        }

        internal static string AvGetCodecName(AVCodecID codecId)
        {
            return ffmpeg.avcodec_get_name(codecId);
        }

        internal static string AvGetSampleFormatName(AVSampleFormat sampleFormat)
        {
            return ffmpeg.av_get_sample_fmt_name(sampleFormat);
        }

        internal static unsafe string AvChannelLayoutDescribe(AVChannelLayout* channelLayout)
        {
            byte* buffer = stackalloc byte[256];
            int result = ffmpeg.av_channel_layout_describe(channelLayout, buffer, 256);
            if (result < 0)
                return null;
            return Marshal.PtrToStringAnsi(new IntPtr(buffer));
        }

        internal static unsafe List<string> GetEncoderNames(AVMediaType mediaType)
        {
            var names = new List<string>();
            void* opaque = null;
            AVCodec* codec;
            while ((codec = ffmpeg.av_codec_iterate(&opaque)) != null)
            {
                if (codec->type != mediaType)
                    continue;
                if (ffmpeg.av_codec_is_encoder(codec) == 0)
                    continue;
                var name = Marshal.PtrToStringAnsi((IntPtr)codec->name);
                if (!String.IsNullOrEmpty(name))
                    names.Add(name);
            }
            return names;
        }

        internal static unsafe int AvSamplesGetBufferSize(int channels, AVFrame* frame)
        {
            int result = ffmpeg.av_samples_get_buffer_size(null, channels, frame->nb_samples,
                (AVSampleFormat) frame->format, 1);
            FfmpegException.Try(result, "av_samples_get_buffer_size");
            return result;
        }

        internal static unsafe void AvFormatSeekFile(AvFormatContext formatContext, double time)
        {
            int result = ffmpeg.avformat_seek_file((AVFormatContext*) formatContext.FormatPtr,
                formatContext.BestAudioStreamIndex, long.MinValue, (long) time, (long) time, 0);

            FfmpegException.Try(result, "avformat_seek_file");
        }

        internal static unsafe string AvStrError(int errorCode)
        {
            byte* buffer = stackalloc byte[500];
            int result = ffmpeg.av_strerror(errorCode, buffer, 500);
            if (result < 0)
                return "No description available.";
            var errorMessage = Marshal.PtrToStringAnsi(new IntPtr(buffer)).Trim();
#if DEBUG
            Debug.WriteLineIf(Debugger.IsAttached, errorMessage);
#endif
            return errorMessage;
        }

        internal static void SetLogLevel(LogLevel level)
        {
            ffmpeg.av_log_set_level((int) level);
        }

        internal static LogLevel GetLogLevel()
        {
            return (LogLevel) ffmpeg.av_log_get_level();
        }

        // Signature matches av_log_set_callback_callback in new FFmpeg.AutoGen
        internal unsafe delegate void LogCallback(void* ptr, int level, string fmt, byte* vl);

        // Rooted to keep the native function pointer alive for the lifetime of the callback.
        private static av_log_set_callback_callback _nativeLogCallback;

        internal static unsafe void SetLogCallback(LogCallback callback)
        {
            _nativeLogCallback = callback == null
                ? null
                : new av_log_set_callback_callback((ptr, level, fmt, vl) => callback(ptr, level, fmt, vl));
            ffmpeg.av_log_set_callback(_nativeLogCallback);
        }

        internal static unsafe LogCallback GetDefaultLogCallback()
        {
            return (ptr, level, fmt, vl) =>
            {
                ffmpeg.av_log_default_callback(ptr, level, fmt, vl);
            };
        }

        internal static unsafe string FormatLine(void* avcl, int level, string fmt, byte* vl,
            ref int printPrefix)
        {
            string line = String.Empty;

            const int bufferSize = 0x400;
            byte* buffer = stackalloc byte[bufferSize];
            fixed (int* ppp = &printPrefix)
            {
                int result = ffmpeg.av_log_format_line2(avcl, level, fmt, vl, buffer, bufferSize, ppp);
                if (result < 0)
                {
                    Debug.WriteLine("av_log_format_line2 failed with " + result.ToString("x8"));
                    return line;
                }

                line = Marshal.PtrToStringAnsi(new IntPtr(buffer));
                if (line != null && result > 0)
                    line = line.Substring(0, result);
                return line;
            }
        }
    }
}
