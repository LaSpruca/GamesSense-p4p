using System.Threading;
using System.Threading.Tasks;
using HapticAI.Core;
using HapticAI.Playback;
using HapticAI.Synth;

namespace HapticAI.Pipeline
{
    /// <summary>
    /// The end-to-end generation path for one event:
    ///   HapticEvent → <see cref="IHapticWaveformSource"/> (context + prompt chain + synthesis,
    ///   running either on the backend or on-device) → WaveformToAmplitudes → HapticClipData.
    /// The source is injected, so the same pipeline drives remote or offline generation and the
    /// director/cache/playback around it never change.
    /// </summary>
    public sealed class HapticPipeline
    {
        readonly IHapticWaveformSource _source;
        readonly int _targetSampleRateHz;
        readonly float _clipDurationSeconds;
        readonly float _amplitudeGain;
        readonly float _textureDepth;

        public HapticPipeline(IHapticWaveformSource source, int targetSampleRateHz,
            float clipDurationSeconds = 1.0f, float amplitudeGain = 1.4f, float textureDepth = 0f)
        {
            _source = source;
            _targetSampleRateHz = targetSampleRateHz;
            _clipDurationSeconds = clipDurationSeconds;
            _amplitudeGain = amplitudeGain;
            _textureDepth = textureDepth;
        }

        public async Task<HapticClipData> GenerateAsync(HapticEvent evt, string key, CancellationToken ct)
        {
            var opts = new HapticSynthOptions
            {
                DurationSeconds = _clipDurationSeconds,
                Seed = (int)(Hashing.Fnv1a(key) & 0x7FFFFFFF),
            };

            var result = await _source.GenerateAsync(evt, opts, ct);
            var amps = WaveformToAmplitudes.Convert(result.Waveform, _targetSampleRateHz, _amplitudeGain, _textureDepth);

            return new HapticClipData(key, amps, _targetSampleRateHz, result.Prompt, result.Trace,
                isDegraded: result.IsDegraded);
        }
    }
}
