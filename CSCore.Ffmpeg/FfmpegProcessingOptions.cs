using System;

namespace CSCore.Ffmpeg
{
    /// <summary>
    ///     Selects which engine performs an audio processing step.
    /// </summary>
    public enum AudioProcessor
    {
        /// <summary>
        ///     Let CSCore handle the processing (e.g. via its own DSP chain). The <see cref="FfmpegDecoder" /> only decodes
        ///     and normalizes the sample format, leaving resampling and channel conversion to CSCore.
        /// </summary>
        CSCore,

        /// <summary>
        ///     Let FFmpeg (libswresample) handle the processing inside the <see cref="FfmpegDecoder" />.
        /// </summary>
        Ffmpeg
    }

    /// <summary>
    ///     The interleaved sample format produced when FFmpeg performs the resampling/conversion.
    /// </summary>
    public enum FfmpegSampleFormat
    {
        /// <summary>
        ///     Derive a sensible packed format from the source (planar formats become their packed equivalent).
        /// </summary>
        Auto,

        /// <summary>Unsigned 8 bit PCM.</summary>
        UnsignedByte,

        /// <summary>Signed 16 bit PCM.</summary>
        Short,

        /// <summary>Signed 32 bit PCM.</summary>
        Int32,

        /// <summary>32 bit IEEE floating point.</summary>
        Float,

        /// <summary>64 bit IEEE floating point.</summary>
        Double
    }

    /// <summary>
    ///     Lets the caller pick and choose between CSCore and FFmpeg for individual audio processing steps performed by the
    ///     <see cref="FfmpegDecoder" />.
    /// </summary>
    public sealed class FfmpegProcessingOptions
    {
        private static FfmpegProcessingOptions _default = new FfmpegProcessingOptions();

        /// <summary>
        ///     Gets or sets the options used when no explicit options are supplied to a <see cref="FfmpegDecoder" />.
        /// </summary>
        public static FfmpegProcessingOptions Default
        {
            get { return _default; }
            set { _default = value ?? new FfmpegProcessingOptions(); }
        }

        /// <summary>
        ///     Gets or sets which engine performs sample-rate conversion, sample-format conversion and channel mixing.
        ///     Defaults to <see cref="AudioProcessor.CSCore" />, which preserves the original decode behavior.
        /// </summary>
        public AudioProcessor Resampler { get; set; }

        /// <summary>
        ///     Gets or sets the target sample rate when <see cref="Resampler" /> is <see cref="AudioProcessor.Ffmpeg" />.
        ///     <c>null</c> keeps the source sample rate.
        /// </summary>
        public int? TargetSampleRate { get; set; }

        /// <summary>
        ///     Gets or sets the target channel count when <see cref="Resampler" /> is <see cref="AudioProcessor.Ffmpeg" />.
        ///     <c>null</c> keeps the source channel count.
        /// </summary>
        public int? TargetChannels { get; set; }

        /// <summary>
        ///     Gets or sets the interleaved output sample format used when <see cref="Resampler" /> is
        ///     <see cref="AudioProcessor.Ffmpeg" />. Defaults to <see cref="FfmpegSampleFormat.Float" />.
        /// </summary>
        public FfmpegSampleFormat TargetSampleFormat { get; set; }

        /// <summary>
        ///     Initializes a new instance of the <see cref="FfmpegProcessingOptions" /> class using CSCore for all
        ///     processing steps.
        /// </summary>
        public FfmpegProcessingOptions()
        {
            Resampler = AudioProcessor.CSCore;
            TargetSampleFormat = FfmpegSampleFormat.Float;
        }

        internal bool UseFfmpegResampler
        {
            get { return Resampler == AudioProcessor.Ffmpeg; }
        }

        internal FfmpegProcessingOptions Clone()
        {
            return new FfmpegProcessingOptions
            {
                Resampler = Resampler,
                TargetSampleRate = TargetSampleRate,
                TargetChannels = TargetChannels,
                TargetSampleFormat = TargetSampleFormat
            };
        }
    }
}
