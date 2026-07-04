using System.Threading;
using System.Threading.Tasks;

namespace HapticAI.Synth
{
    public struct HapticSynthOptions
    {
        public float DurationSeconds;
        public int Seed;

        public static HapticSynthOptions Default => new HapticSynthOptions { DurationSeconds = 1.0f, Seed = 0 };
    }

    /// <summary>
    /// Turns a text prompt into a vibrotactile <see cref="Waveform"/>. The real implementation
    /// (<see cref="HaptiGenHttpSynth"/>) calls a hosted HaptiGen server; <see cref="MockHapticSynth"/>
    /// is a fully local stand-in so the scene runs with no server. Swap providers via config.
    /// </summary>
    public interface IHapticSynthProvider
    {
        Task<Waveform> SynthesizeAsync(string prompt, HapticSynthOptions options, CancellationToken ct);
    }
}
