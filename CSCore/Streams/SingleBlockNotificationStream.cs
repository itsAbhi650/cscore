using System;
using System.Diagnostics;

namespace CSCore.Streams
{
    /// <summary>
    /// Fire the <see cref="SingleBlockRead"/> event after every block read.
    /// </summary>
    public class SingleBlockNotificationStream : SampleAggregatorBase
    {
        private readonly ISampleSource _source;
        private readonly long _fileLength;
        private long _bufsize;
        private long _fileRead;
        private long _prevRead;

        /// <summary>
        /// Occurs when the <see cref="Read"/> method reads a block.
        /// </summary>
        /// <remarks>If the <see cref="Read"/> method reads <c>n</c> during a single call, the <see cref="SingleBlockRead"/> event will get fired <c>n</c> times.</remarks>
        public event EventHandler<SingleBlockReadEventArgs> SingleBlockRead;

        /// <summary>
        /// Occurs when the <see cref="Read"/> method is about to reach the end of the stream.
        /// </summary>
        public event EventHandler<SingleBlockStreamAlmostFinishedEventArgs> SingleBlockStreamAlmostFinished;

        /// <summary>
        /// Occurs when the <see cref="Read"/> method reaches the end of the stream.
        /// </summary>
        public event EventHandler<SingleBlockStreamFinishedEventArgs> SingleBlockStreamFinished;

        /// <summary>
        /// Initializes a new instance of the <see cref="SingleBlockNotificationStream"/> class.
        /// </summary>
        /// <param name="source">Underlying base source which provides audio data.</param>
        /// <exception cref="System.ArgumentNullException">source</exception>
        public SingleBlockNotificationStream(ISampleSource source)
            : base(source)
        {
            if (source == null)
                throw new ArgumentNullException("source");

            _source = source;
            _fileLength = source.Length;
            _bufsize = 0L;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SingleBlockNotificationStream"/> class.
        /// </summary>
        /// <param name="source">Underlying base source which provides audio data.</param>
        /// <param name="bufsize">Estimate of the number of samples buffered downstream. Avoids a pause between plays.</param>
        /// <exception cref="System.ArgumentNullException">source</exception>
        public SingleBlockNotificationStream(ISampleSource source, long bufsize)
            : base(source)
        {
            if (source == null)
                throw new ArgumentNullException("source");

            _source = source;
            _fileLength = source.Length;
            _bufsize = bufsize;
        }

        /// <summary>
        /// Reads a sequence of samples from the <see cref="SampleAggregatorBase" /> and advances the position within the stream by
        /// the number of samples read. Fires the <see cref="SingleBlockRead"/> event for each block it reads (one block = (number of channels) samples).
        /// </summary>
        /// <param name="buffer">An array of floats. When this method returns, the <paramref name="buffer" /> contains the specified
        /// float array with the values between <paramref name="offset" /> and (<paramref name="offset" /> +
        /// <paramref name="count" /> - 1) replaced by the floats read from the current source.</param>
        /// <param name="offset">The zero-based offset in the <paramref name="buffer" /> at which to begin storing the data
        /// read from the current stream.</param>
        /// <param name="count">The maximum number of samples to read from the current source.</param>
        /// <returns>
        /// The total number of samples read into the buffer.
        /// </returns>
        public override int Read(float[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);

            EventHandler<SingleBlockReadEventArgs> singleBlockRead = SingleBlockRead;
            EventHandler<SingleBlockStreamAlmostFinishedEventArgs> singleBlockStreamAlmostFinished = SingleBlockStreamAlmostFinished;
            EventHandler<SingleBlockStreamFinishedEventArgs> singleBlockStreamFinished = SingleBlockStreamFinished;

            if (read != 0 && singleBlockRead != null)
            {
                int channels = WaveFormat.Channels;
                for (int n = 0; n < read; n += channels)
                {
                    singleBlockRead(this, new SingleBlockReadEventArgs(buffer, offset + n, channels));
                }
            }

            // Almost finished: account for the estimated downstream buffer so the event fires ahead of the real eof.
            _fileRead = _source.Position + _bufsize;
            if (_fileRead >= _fileLength && singleBlockStreamFinished != null)
            {
                Debug.WriteLine("almost eof");
                if (singleBlockStreamAlmostFinished != null)
                    singleBlockStreamAlmostFinished(this, new SingleBlockStreamAlmostFinishedEventArgs());
                _bufsize = -100000L;
            }

            // Finished: real end of stream.
            _fileRead = _source.Position;
            if (_fileRead >= _fileLength && singleBlockStreamFinished != null)
            {
                Debug.WriteLine("real eof");
                singleBlockStreamFinished(this, new SingleBlockStreamFinishedEventArgs());
            }

            // Fallback: streams that stall near the end without exposing a matching position.
            _fileRead = _source.Position + 10000L;
            if (_fileRead >= _fileLength && singleBlockStreamFinished != null)
            {
                if (read == 0 && _prevRead == 0L)
                {
                    Debug.WriteLine("probable eof");
                    singleBlockStreamFinished(this, new SingleBlockStreamFinishedEventArgs());
                }
            }

            _prevRead = read;
            return read;
        }
    }
}
