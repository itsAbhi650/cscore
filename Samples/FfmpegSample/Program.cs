using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CSCore;
using CSCore.CoreAudioAPI;
using CSCore.DSP;
using CSCore.Ffmpeg;
using CSCore.SoundOut;
using CSCore.Streams;

namespace FfmpegSample
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args != null && args.Length >= 1 && args[0] == "--selftest")
            {
                Environment.ExitCode = SelfTest(args);
                return;
            }

            if (Debugger.IsAttached)
            {
                //seems like while a debugger is attached the ffmpeg log does not appear in the console
                FfmpegUtils.LogToDefaultLogger = false;
                FfmpegUtils.FfmpegLogReceived += (s, e) =>
                {
                    Console.Error.Write(e.Message);
                };
            }

            EnumerateSupportedFormats();

            const string DefaultStream =
                @"http://stream.srg-ssr.ch/m/rsj/aacp_96";

            Console.WriteLine("01 - File");
            Console.WriteLine("02 - Stream");
            Console.WriteLine("99 - Test-Stream");

            int choice;
            do
            {
                Int32.TryParse(Console.ReadLine(), out choice);
            } while (choice != 1 && choice != 2 && choice != 99);

            Stream stream = null;
            string url = null;

            if (choice == 1)
            {
                OpenFileDialog openFileDialog = new OpenFileDialog();
                if (openFileDialog.ShowDialog() != DialogResult.OK)
                    return;

                stream = File.OpenRead(openFileDialog.FileName);
            }
            else if (choice == 2)
            {
                Console.WriteLine("Enter a stream url:");
                url = Console.ReadLine();
            }
            else
            {
                url = DefaultStream;
            }

            //we could also easily pass the filename as url
            //but since we want to test the decoding of System.IO.Stream, we
            //pass a FileStream as argument.
            IWaveSource ffmpegDecoder = stream == null
                ? new FfmpegDecoder(url)
                : new FfmpegDecoder(stream);

            using (stream)
            using (ffmpegDecoder)
            using (var wasapiOut = new WasapiOut())
            {
                wasapiOut.Initialize(ffmpegDecoder);
                wasapiOut.Play();

                Console.ReadKey();
            }
        }

        private static int SelfTest(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: FfmpegSample.exe --selftest <inputAudioFile> [outputMp3]");
                return 2;
            }

            string input = args[1];
            string outMp3 = args.Length >= 3
                ? args[2]
                : Path.Combine(Path.GetTempPath(), "cscore_ffmpeg_selftest.mp3");

            int failures = 0;
            double originalSeconds = 0;

            // 1. Basic decode (CSCore path)
            try
            {
                using (var dec = new FfmpegDecoder(input))
                {
                    originalSeconds = dec.Length / (double)dec.WaveFormat.BytesPerSecond;
                    Console.WriteLine("Decoded format : " + dec.WaveFormat);
                    Console.WriteLine("Duration       : " + originalSeconds.ToString("0.00") + "s, CanSeek=" + dec.CanSeek);
                    foreach (var kv in dec.Metadata)
                        Console.WriteLine("  meta " + kv.Key + " = " + kv.Value);

                    long total = 0;
                    long nonZero = 0;
                    var buffer = new byte[dec.WaveFormat.BytesPerSecond];
                    int read;
                    while ((read = dec.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += read;
                        for (int i = 0; i < read; i++)
                            if (buffer[i] != 0) nonZero++;
                    }
                    failures += Check("Decode reads audio data", total > 0, total + " bytes");
                    failures += Check("Decoded audio is not all-silence", nonZero > 0, nonZero + " non-zero bytes");
                }
            }
            catch (Exception ex) { failures += Fail("Basic decode", ex); }

            // 2. Seeking (via a seekable FileStream, which also exercises the flush-on-seek path)
            try
            {
                using (var fs = File.OpenRead(input))
                using (var dec = new FfmpegDecoder(fs))
                {
                    failures += Check("CanSeek reported true for a seekable stream", dec.CanSeek, "CanSeek=" + dec.CanSeek);
                    if (dec.Length > 0)
                    {
                        long target = dec.Length / 2;
                        target -= target % dec.WaveFormat.BlockAlign;
                        dec.Position = target; //triggers seek + decoder/resampler flush
                        var buffer = new byte[dec.WaveFormat.BlockAlign * 1024];
                        int read = dec.Read(buffer, 0, buffer.Length);
                        failures += Check("Seek to middle + read (flush-on-seek)", read > 0 && dec.Position >= target, "pos=" + dec.Position);

                        dec.Position = 0;
                        int readAfter = dec.Read(buffer, 0, buffer.Length);
                        failures += Check("Seek back to start + read", readAfter > 0, "pos=" + dec.Position);
                    }
                    else
                    {
                        Console.WriteLine("[SKIP] Seek (unknown length)");
                    }
                }
            }
            catch (Exception ex) { failures += Fail("Seeking", ex); }

            // 3. FFmpeg resampler path (pick-and-choose: AudioProcessor.Ffmpeg)
            try
            {
                var opts = new FfmpegProcessingOptions
                {
                    Resampler = AudioProcessor.Ffmpeg,
                    TargetSampleRate = 44100,
                    TargetChannels = 2,
                    TargetSampleFormat = FfmpegSampleFormat.Float
                };
                using (var dec = new FfmpegDecoder(input, opts))
                {
                    bool fmtOk = dec.WaveFormat.SampleRate == 44100 &&
                                 dec.WaveFormat.Channels == 2 &&
                                 dec.WaveFormat.WaveFormatTag == AudioEncoding.IeeeFloat &&
                                 dec.WaveFormat.BitsPerSample == 32;
                    failures += Check("FFmpeg resampler output = 44100/2/Float32", fmtOk, dec.WaveFormat.ToString());

                    long total = 0;
                    var buffer = new byte[dec.WaveFormat.BytesPerSecond];
                    int read;
                    while ((read = dec.Read(buffer, 0, buffer.Length)) > 0)
                        total += read;
                    double seconds = total / (double)dec.WaveFormat.BytesPerSecond;
                    failures += Check("FFmpeg-resampled duration ~= original",
                        originalSeconds <= 0 || Math.Abs(seconds - originalSeconds) < Math.Max(0.5, originalSeconds * 0.05),
                        seconds.ToString("0.00") + "s vs " + originalSeconds.ToString("0.00") + "s");
                }
            }
            catch (Exception ex) { failures += Fail("FFmpeg resampler path", ex); }

            // 4. Encoder -> MP3
            try
            {
                using (var dec = new FfmpegDecoder(input))
                using (var enc = FfmpegEncoder.CreateMP3Encoder(dec.WaveFormat, outMp3, 192000))
                {
                    FfmpegEncoder.EncodeWholeSource(enc, dec);
                }
                var fi = new FileInfo(outMp3);
                failures += Check("Encoder produced a non-trivial MP3", fi.Exists && fi.Length > 1024,
                    outMp3 + " (" + (fi.Exists ? fi.Length + " bytes" : "missing") + ")");
            }
            catch (Exception ex) { failures += Fail("MP3 encoding", ex); }

            // 5. Round-trip: re-decode the produced MP3
            try
            {
                if (File.Exists(outMp3))
                {
                    using (var dec = new FfmpegDecoder(outMp3))
                    {
                        double seconds = dec.Length / (double)dec.WaveFormat.BytesPerSecond;
                        failures += Check("Re-decode MP3 duration ~= original",
                            originalSeconds <= 0 || Math.Abs(seconds - originalSeconds) < Math.Max(1.0, originalSeconds * 0.10),
                            seconds.ToString("0.00") + "s vs " + originalSeconds.ToString("0.00") + "s");
                    }
                }
            }
            catch (Exception ex) { failures += Fail("MP3 round-trip decode", ex); }

            // 6. Encoder discovery
            try
            {
                var encoders = FfmpegUtils.GetAudioEncoderNames().ToList();
                bool hasMp3 = encoders.Any(n => n.IndexOf("mp3", StringComparison.OrdinalIgnoreCase) >= 0);
                failures += Check("Audio encoders enumerated (mp3 present)", encoders.Count > 0 && hasMp3, encoders.Count + " encoders");
            }
            catch (Exception ex) { failures += Fail("Encoder enumeration", ex); }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
            return failures == 0 ? 0 : 1;
        }

        private static int Check(string name, bool ok, string detail)
        {
            Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + name + (String.IsNullOrEmpty(detail) ? "" : " -> " + detail));
            return ok ? 0 : 1;
        }

        private static int Fail(string name, Exception ex)
        {
            Console.WriteLine("[FAIL] " + name + " -> " + ex.GetType().Name + ": " + ex.Message);
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                Console.WriteLine("         inner: " + inner.GetType().Name + ": " + inner.Message);
            return 1;
        }

        private static void EnumerateSupportedFormats()
        {
            Console.BufferHeight = 1500;

            foreach (var format in FfmpegUtils.GetInputFormats())
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(format.Name);
                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.WriteLine(format.LongName);
                Console.ResetColor();

                string extensions = String.Empty;
                if (format.FileExtensions.Count > 0)
                    extensions = format.FileExtensions.Aggregate((x, y) => x + ", " + y);
                Console.WriteLine("Extensions: " + extensions);
                Console.WriteLine("Codecs");
                foreach (var supportedCodec in format.Codecs)
                {
                    Console.WriteLine(" -" + supportedCodec);
                }
            }
        }
    }
}
