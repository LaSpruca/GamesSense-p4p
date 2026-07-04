using System.Threading;
using System.Threading.Tasks;
using HapticAI.Core;
using HapticAI.Playback;
using HapticAI.Synth;

namespace HapticAI.Pipeline
{
    /// <summary>
    /// The end-to-end generation path for one event:
    ///   cues → ContextBuilder → PromptChain (Gemini) → IHapticSynthProvider (HaptiGen) →
    ///   WaveformToAmplitudes → HapticClipData.
    /// Every stage is injected, so any one can be swapped without touching the others.
    /// </summary>
    public sealed class HapticPipeline
    {
        readonly ContextBuilder _context;
        readonly PromptChain _prompts;
        readonly IHapticSynthProvider _synth;
        readonly int _targetSampleRateHz;
        readonly float _clipDurationSeconds;
        readonly float _amplitudeGain;

        public HapticPipeline(ContextBuilder context, PromptChain prompts, IHapticSynthProvider synth,
            int targetSampleRateHz, float clipDurationSeconds = 1.0f, float amplitudeGain = 1.4f)
        {
            _context = context;
            _prompts = prompts;
            _synth = synth;
            _targetSampleRateHz = targetSampleRateHz;
            _clipDurationSeconds = clipDurationSeconds;
            _amplitudeGain = amplitudeGain;
        }

        public async Task<HapticClipData> GenerateAsync(HapticEvent evt, string key, CancellationToken ct)
        {
            var ctx = _context.Build(evt);
            var promptState = await _prompts.RunAsync(ctx, ct);

            var opts = new HapticSynthOptions
            {
                DurationSeconds = _clipDurationSeconds,
                Seed = (int)(Hashing.Fnv1a(key) & 0x7FFFFFFF),
            };
            var waveform = await _synth.SynthesizeAsync(promptState.Current, opts, ct);
            var amps = WaveformToAmplitudes.Convert(waveform, _targetSampleRateHz, _amplitudeGain);

            return new HapticClipData(key, amps, _targetSampleRateHz, promptState.Current, promptState.Trace);
        }
    }
}
