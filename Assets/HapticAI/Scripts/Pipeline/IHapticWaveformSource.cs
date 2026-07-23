using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HapticAI.Core;
using HapticAI.Synth;

namespace HapticAI.Pipeline
{
    /// <summary>
    /// The result of turning an event into a vibrotactile signal: the waveform plus the prompt
    /// (and per-stage trace) that produced it, kept for on-device debugging.
    /// </summary>
    public struct WaveformResult
    {
        public Waveform Waveform;
        public string Prompt;
        public IReadOnlyList<string> Trace;
    }

    /// <summary>
    /// Turns a world <see cref="HapticEvent"/> straight into a <see cref="Waveform"/>. This is the
    /// seam the prompting move introduced: everything between "what happened" and "the waveform"
    /// (context building + the LLM prompt chain + synthesis) now lives behind this one call, so it
    /// can run either on the backend (<see cref="RemoteWaveformSource"/>) or on-device
    /// (<see cref="LocalWaveformSource"/>) without the rest of the pipeline knowing which.
    /// </summary>
    public interface IHapticWaveformSource
    {
        Task<WaveformResult> GenerateAsync(HapticEvent evt, HapticSynthOptions options, CancellationToken ct);
    }
}
