namespace HapticAI.Synth
{
    /// <summary>
    /// A mono signal as produced by HaptiGen (an audio-rate vibrotactile waveform). Samples are
    /// nominally in [-1, 1]. Converted to controller amplitudes by
    /// <see cref="Playback.WaveformToAmplitudes"/>.
    /// </summary>
    public sealed class Waveform
    {
        public readonly float[] Samples;
        public readonly int SampleRateHz;

        public Waveform(float[] samples, int sampleRateHz)
        {
            Samples = samples ?? new float[0];
            SampleRateHz = sampleRateHz > 0 ? sampleRateHz : 1;
        }

        public float DurationSeconds => Samples.Length / (float)SampleRateHz;
    }
}
