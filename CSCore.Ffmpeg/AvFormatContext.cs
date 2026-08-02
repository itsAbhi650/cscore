using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using CSCore.Ffmpeg.Interops;

namespace CSCore.Ffmpeg
{
    internal class AvFormatContext : IDisposable
    {
        private unsafe AVFormatContext* _formatContext;
        private AvStream _stream;
        private readonly FfmpegProcessingOptions _options;
        private readonly bool _sourceSeekable;

        public unsafe IntPtr FormatPtr
        {
            get { return (IntPtr) _formatContext; }
        }

        public int BestAudioStreamIndex { get; private set; }

        public AvStream SelectedStream
        {
            get { return _stream; }
        }

        public unsafe bool CanSeek
        {
            get
            {
                if (_formatContext == null)
                    return false;
                //the demuxer flags genuinely unseekable inputs (e.g. some live streams)
                if ((_formatContext->ctx_flags & ffmpeg.AVFMTCTX_UNSEEKABLE) != 0)
                    return false;
                return _sourceSeekable;
            }
        }

        public double LengthInSeconds
        {
            get
            {
                if (SelectedStream == null || SelectedStream.Stream.duration < 0)
                    return 0;
                var timebase = SelectedStream.Stream.time_base;
                if (timebase.den == 0)
                    return 0;
                return SelectedStream.Stream.duration * timebase.num / (double) timebase.den;
            }
        }

        public unsafe AVFormatContext FormatContext
        {
            get
            {
                if (_formatContext == null)
                    return default(AVFormatContext);
                return *_formatContext;
            }
        }

        public Dictionary<string,string> Metadata { get; private set; }

        public unsafe AvFormatContext(FfmpegStream stream)
            : this(stream, null)
        {
        }

        public unsafe AvFormatContext(FfmpegStream stream, FfmpegProcessingOptions options)
        {
            _options = options ?? FfmpegProcessingOptions.Default;
            _sourceSeekable = stream.CanSeek;
            _formatContext = FfmpegCalls.AvformatAllocContext();
            fixed (AVFormatContext** pformatContext = &_formatContext)
            {
                FfmpegCalls.AvformatOpenInput(pformatContext, stream.AvioContext);
            }
            Initialize();
        }

        public unsafe AvFormatContext(string url)
            : this(url, null)
        {
        }

        public unsafe AvFormatContext(string url, FfmpegProcessingOptions options)
        {
            _options = options ?? FfmpegProcessingOptions.Default;
            _sourceSeekable = true; //local files and range-capable URLs are seekable; the demuxer vetoes live streams via ctx_flags
            _formatContext = FfmpegCalls.AvformatAllocContext();
            fixed (AVFormatContext** pformatContext = &_formatContext)
            {
                FfmpegCalls.AvformatOpenInput(pformatContext, url);
            }
            Initialize();
        }

        private unsafe void Initialize()
        {
            FfmpegCalls.AvFormatFindStreamInfo(_formatContext);
            BestAudioStreamIndex = FfmpegCalls.AvFindBestStreamInfo(_formatContext);
             _stream = new AvStream((IntPtr)_formatContext->streams[BestAudioStreamIndex], _options);

            Metadata = new Dictionary<string, string>();
            if (_formatContext->metadata != null)
            {
                AVDictionaryEntry* entry = null;
                while ((entry = ffmpeg.av_dict_iterate(_formatContext->metadata, entry)) != null)
                {
                    var key = Marshal.PtrToStringAnsi((IntPtr)entry->key);
                    if (key != null)
                        Metadata[key] = Marshal.PtrToStringAnsi((IntPtr)entry->value);
                }
            }
        }

        public void SeekFile(double seconds)
        {
            var streamTimeBase = SelectedStream.Stream.time_base;
            var time = seconds * streamTimeBase.den / streamTimeBase.num;

            FfmpegCalls.AvFormatSeekFile(this, time);

            //discard buffered decoder/resampler state so no stale samples leak out after the seek
            SelectedStream.FlushBuffers();
        }

        public unsafe void Dispose()
        {
            GC.SuppressFinalize(this);

            if (SelectedStream != null)
            {
                SelectedStream.Dispose();
                _stream = null;
            }

            if (_formatContext != null)
            {
                fixed (AVFormatContext** pformatContext = &_formatContext)
                {
                    FfmpegCalls.AvformatCloseInput(pformatContext);
                }

                _formatContext = null;
                BestAudioStreamIndex = 0;
            }
        }

        ~AvFormatContext()
        {
            Dispose();
        }
    }
}