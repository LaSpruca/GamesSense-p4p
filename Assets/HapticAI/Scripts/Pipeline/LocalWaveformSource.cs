using System.Threading;
using System.Threading.Tasks;
using HapticAI.Core;
using HapticAI.Synth;

namespace HapticAI.Pipeline
{
    /// <summary>
    /// Fully on-device generation: context brief → offline prompt template → mock synth. Used when
    /// no haptic service URL is configured, and as the fallback inside
    /// <see cref="RemoteWaveformSource"/> when the backend is unreachable, so the scene always
    /// produces a real generated waveform even with no server. This is the same path the headset
    /// used to run for its offline mode; the real Gemini chain now lives on the backend instead.
    /// </summary>
    public sealed class LocalWaveformSource : IHapticWaveformSource
    {
        readonly ContextBuilder _context;
        readonly PromptChain _prompts;
        readonly IHapticSynthProvider _synth;

        public LocalWaveformSource(ContextBuilder context, PromptChain prompts, IHapticSynthProvider synth)
        {
            _context = context;
            _prompts = prompts;
            _synth = synth;
        }

        public async Task<WaveformResult> GenerateAsync(HapticEvent evt, HapticSynthOptions options, CancellationToken ct)
        {
            var ctx = _context.Build(evt);
            var promptState = await _prompts.RunAsync(ctx, ct);
            var waveform = await _synth.SynthesizeAsync(promptState.Current, options, ct);
            return new WaveformResult
            {
                Waveform = waveform,
                Prompt = promptState.Current,
                Trace = promptState.Trace,
            };
        }
    }
}
