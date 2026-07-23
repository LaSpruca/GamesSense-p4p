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
    /// Turns a text prompt into a vibrotactile <see cref="Waveform"/> on-device. The only remaining
    /// implementation is <see cref="MockHapticSynth"/>, the local offline stand-in. The real HaptiGen
    /// synthesis now runs on the backend (server/hapticgen_server.py) and reaches the scene through
    /// the remote waveform source, so it no longer needs an on-device provider.
    /// </summary>
    public interface IHapticSynthProvider
    {
        Task<Waveform> SynthesizeAsync(string prompt, HapticSynthOptions options, CancellationToken ct);
    }
}
