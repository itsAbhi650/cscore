using System;
using CSCore.Ffmpeg.Interops;

namespace CSCore.Ffmpeg
{
    internal sealed class AvFrame : IDisposable
    {
        private readonly AvFormatContext _formatContext;
        private unsafe AVFrame* _frame;

        public unsafe AvFrame(AvFormatContext formatContext)
        {
            _formatContext = formatContext;
            _frame = FfmpegCalls.AvFrameAlloc();
        }

        public unsafe int ReadNextFrame(out double seconds, ref byte[] buffer)
        {
            var stream = _formatContext.SelectedStream.Stream;
            var codecCtx = _formatContext.SelectedStream.CodecContextPtr;
            AVPacket* packet = FfmpegCalls.AvPacketAlloc();
            int offset = 0;
            seconds = 0;
            try
            {
                do
                {
                    packet->data = null;
                    packet->size = 0;

                    if (!FfmpegCalls.AvReadFrame(_formatContext, packet))
                        break;

                    if (packet->stream_index != stream.index)
                    {
                        FfmpegCalls.FreePacket(packet);
                        continue;
                    }

                    seconds = packet->pts * stream.time_base.num / (double)stream.time_base.den;

                    try
                    {
                        FfmpegCalls.AvCodecSendPacket(codecCtx, packet);
                        while (FfmpegCalls.AvCodecReceiveFrame(codecCtx, _frame))
                        {
                            try
                            {
                                int bufferLength = DecodeFrame(ref buffer, offset);
                                offset += bufferLength;
                            }
                            catch (FfmpegException)
                            {
                                break;
                            }
                        }
                    }
                    catch (FfmpegException) { }
                    finally
                    {
                        FfmpegCalls.FreePacket(packet);
                    }
                } while (offset <= 0);
            }
            finally
            {
                FfmpegCalls.AvPacketFree(&packet);
            }

            return offset;
        }

        private unsafe int DecodeFrame(ref byte[] buffer, int offset)
        {
            var resampler = _formatContext.SelectedStream.Resampler;
            if (resampler != null)
                return resampler.Convert(_frame, ref buffer, offset);

            int dataSize = FfmpegCalls.AvGetBytesPerSample((AVSampleFormat)_frame->format);
            int size = FfmpegCalls.AvSamplesGetBufferSize(_frame->ch_layout.nb_channels, _frame);
            if (buffer == null || buffer.Length < offset + size)
            {
                byte[] bufferTemp = new byte[offset + size];
                if (buffer != null)
                    Buffer.BlockCopy(buffer, 0, bufferTemp, 0, buffer.Length);
                buffer = bufferTemp;
            }

            if (IsPlanar((AVSampleFormat) _frame->format))
            {
                for (int c = 0; c < _frame->ch_layout.nb_channels; c++)
                {
                    for (int i = 0; i < _frame->nb_samples; i++)
                    {
                        if (dataSize == 1)
                        {
                            buffer[offset + i * _frame->ch_layout.nb_channels + c] = _frame->extended_data[c][i];
                        }
                        else if (dataSize == 2)
                        {
                            buffer[offset + i * dataSize * _frame->ch_layout.nb_channels + c * dataSize] =
                                _frame->extended_data[c][i * dataSize];
                            buffer[offset + i * dataSize * _frame->ch_layout.nb_channels + c * dataSize + 1] =
                                _frame->extended_data[c][i * dataSize + 1];
                        }
                        else if (dataSize == 4)
                        {
                            buffer[offset + i * dataSize * _frame->ch_layout.nb_channels + c * dataSize] =
                                _frame->extended_data[c][i * dataSize];
                            buffer[offset + i * dataSize * _frame->ch_layout.nb_channels + c * dataSize + 1] =
                                _frame->extended_data[c][i * dataSize + 1];
                            buffer[offset + i * dataSize * _frame->ch_layout.nb_channels + c * dataSize + 2] =
                                _frame->extended_data[c][i * dataSize + 2];
                            buffer[offset + i * dataSize * _frame->ch_layout.nb_channels + c * dataSize + 3] =
                                _frame->extended_data[c][i * dataSize + 3];
                        }
                        else if (dataSize == 8)
                        {
                            buffer[offset + i * dataSize * _frame->ch_layout.nb_channels + c * dataSize] =
                                _frame->extended_data[c][i * dataSize];
                            buffer[offset + i * dataSize * _frame->ch_layout.nb_channels + c * dataSize + 1] =
                                _frame->extended_data[c][i * dataSize + 1];
                            buffer[offset + i * dataSize * _frame->ch_layout.nb_channels + c * dataSize + 2] =
                                _frame->extended_data[c][i * dataSize + 2];
                            buffer[offset + i * dataSize * _frame->ch_layout.nb_channels + c * dataSize + 3] =
                                _frame->extended_data[c][i * dataSize + 3];
                            buffer[offset + i * dataSize * _frame->ch_layout.nb_channels + c * dataSize + 4] =
                                _frame->extended_data[c][i * dataSize + 4];
                            buffer[offset + i * dataSize * _frame->ch_layout.nb_channels + c * dataSize + 5] =
                                _frame->extended_data[c][i * dataSize + 5];
                            buffer[offset + i * dataSize * _frame->ch_layout.nb_channels + c * dataSize + 6] =
                                _frame->extended_data[c][i * dataSize + 6];
                            buffer[offset + i * dataSize * _frame->ch_layout.nb_channels + c * dataSize + 7] =
                                _frame->extended_data[c][i * dataSize + 7];
                        }
                    }
                }

                size = dataSize * _frame->ch_layout.nb_channels * _frame->nb_samples;
            }
            else
            {
                for (int i = 0; i < size; i++)
                {
                    buffer[i + offset] = _frame->extended_data[0][i];
                }
            }

            if (dataSize == 8)
            {
                //dbl
                size = ConvertDblToFloat(buffer, offset, size);
            }

            return size;
        }

        private unsafe int ConvertDblToFloat(byte[] buffer, int offset, int count)
        {
            byte[] fltBuffer = new byte[count / 2];
            fixed (void* pbuf = &buffer[offset])
            fixed(void* pout = &fltBuffer[0])
            {
                float* pflt = (float*) pout;
                double* pdbl = (double*) pbuf;
                for (int i = 0; i < count / 8; i++)
                {
                    pflt[i] = (float)pdbl[i];
                }
            }
            Array.Clear(buffer, offset, count);
            Buffer.BlockCopy(fltBuffer, 0, buffer, offset, fltBuffer.Length);
            return fltBuffer.Length;
        }

        private bool IsPlanar(AVSampleFormat sampleFormat)
        {
            return sampleFormat == AVSampleFormat.AV_SAMPLE_FMT_U8P ||
                   sampleFormat == AVSampleFormat.AV_SAMPLE_FMT_S16P ||
                   sampleFormat == AVSampleFormat.AV_SAMPLE_FMT_S32P ||
                   sampleFormat == AVSampleFormat.AV_SAMPLE_FMT_FLTP ||
                   sampleFormat == AVSampleFormat.AV_SAMPLE_FMT_DBLP;
        }

        public unsafe void Dispose()
        {
            GC.SuppressFinalize(this);
            if (_frame != null)
            {
                FfmpegCalls.AvFrameFree(_frame);
                _frame = null;
            }
        }

        ~AvFrame()
        {
            Dispose();
        }
    }
}