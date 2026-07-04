using System;
using System.Threading;
using System.Threading.Tasks;
using HapticAI.Core;

namespace HapticAI.Synth
{
    /// <summary>
    /// A local, deterministic HaptiGen stand-in. It derives a couple of oscillators plus a noisy
    /// "crackle" from a stable hash of the prompt, so different prompts yield audibly different —
    /// but repeatable — waveforms. Lets the whole pipeline run on-device with no GPU server.
    /// </summary>
    public sealed class MockHapticSynth : IHapticSynthProvider
    {
        readonly int _sampleRate;
        readonly int _simulatedLatencyMs;

        public MockHapticSynth(int sampleRate = 8000, int simulatedLatencyMs = 0)
        {
            _sampleRate = sampleRate;
            _simulatedLatencyMs = simulatedLatencyMs;
        }

        public async Task<Waveform> SynthesizeAsync(string prompt, HapticSynthOptions options, CancellationToken ct)
        {
            if (_simulatedLatencyMs > 0)
                await Task.Delay(_simulatedLatencyMs, ct);

            uint h = Hashing.Fnv1a(prompt) ^ (uint)options.Seed;
            var rng = new DeterministicRng(h);

            float duration = options.DurationSeconds <= 0f ? 1f : options.DurationSeconds;
            int n = Math.Max(1, (int)(duration * _sampleRate));
            var samples = new float[n];

            float baseFreq = 40f + (h % 160);           // 40..200 Hz carrier
            float wobble = 2f + rng.NextFloat() * 8f;    // amplitude wobble
            float crackle = 0.2f + rng.NextFloat() * 0.6f;
            float decay = 2.5f + rng.NextFloat() * 4f;   // exponential fall-off

            for (int i = 0; i < n; i++)
            {
                float t = i / (float)_sampleRate;
                float env = (float)Math.Exp(-decay * t) * (0.6f + 0.4f * (float)Math.Sin(2 * Math.PI * wobble * t));
                float carrier = (float)Math.Sin(2 * Math.PI * baseFreq * t);
                float noise = (rng.NextFloat() * 2f - 1f) * crackle;
                samples[i] = Clamp(env * (carrier + noise) * 0.8f);
            }

            return new Waveform(samples, _sampleRate);
        }

        static float Clamp(float v) => v < -1f ? -1f : v > 1f ? 1f : v;

        /// <summary>Small xorshift so mock output never depends on UnityEngine.Random (main-thread only).</summary>
        struct DeterministicRng
        {
            uint _state;
            public DeterministicRng(uint seed) => _state = seed == 0 ? 0x9E3779B9 : seed;
            public float NextFloat()
            {
                _state ^= _state << 13; _state ^= _state >> 17; _state ^= _state << 5;
                return (_state & 0xFFFFFF) / (float)0x1000000;
            }
        }
    }
}
