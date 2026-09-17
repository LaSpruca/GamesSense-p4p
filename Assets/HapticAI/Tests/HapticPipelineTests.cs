using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HapticAI.Cache;
using HapticAI.Core;
using HapticAI.Pipeline;
using HapticAI.Playback;
using HapticAI.Synth;
using NUnit.Framework;

namespace HapticAI.Tests
{
    /// <summary>
    /// EditMode tests for the pure pipeline logic — no headset, no network, no servers required.
    /// </summary>
    public class HapticPipelineTests
    {
        [Test]
        public void WaveformToAmplitudes_ResamplesAndBounds()
        {
            // 1 s of full-scale signal at 1000 Hz, downsampled to 100 Hz.
            var samples = new float[1000];
            for (int i = 0; i < samples.Length; i++) samples[i] = 1f;
            var wf = new Waveform(samples, 1000);

            byte[] amps = WaveformToAmplitudes.Convert(wf, targetRateHz: 100);

            Assert.AreEqual(100, amps.Length, "should resample to target rate * duration");
            foreach (var a in amps)
                Assert.AreEqual(255, a, "full-scale input with gain should saturate to 255");
        }

        [Test]
        public void WaveformToAmplitudes_EmptyInput_ReturnsEmpty()
        {
            var amps = WaveformToAmplitudes.Convert(new Waveform(new float[0], 16000), 320);
            Assert.AreEqual(0, amps.Length);
        }

        [Test]
        public void Cache_BucketsNearbyCuesToSameKey()
        {
            var cache = new HapticClipCache(buckets: 4);
            string a = cache.KeyFor(new HapticEvent(HapticEventType.SprayTick).WithNumber("triggerDepth", 0.71f));
            string b = cache.KeyFor(new HapticEvent(HapticEventType.SprayTick).WithNumber("triggerDepth", 0.73f));
            string c = cache.KeyFor(new HapticEvent(HapticEventType.SprayTick).WithNumber("triggerDepth", 0.98f));

            Assert.AreEqual(a, b, "0.71 and 0.73 fall in the same bucket");
            Assert.AreNotEqual(a, c, "0.98 is a different bucket");
        }

        [Test]
        public void Cache_IgnoresFallbackClips()
        {
            var cache = new HapticClipCache();
            var key = "k";
            cache.Put(new HapticClipData(key, new byte[] { 1 }, 320, isFallback: true));
            Assert.IsFalse(cache.TryGet(key, out _), "fallback clips must not poison the cache");
        }

        [Test]
        public void Cache_CollapsesDistancesThatProduceTheSamePrompt()
        {
            // The backend describes distance with three descriptors only, so two fires that differ
            // by a few metres generate an identical clip and must share a cache entry.
            var cache = new HapticClipCache();
            var far1 = new HapticEvent(HapticEventType.Spread).WithNumber("distance", 4.0f);
            var far2 = new HapticEvent(HapticEventType.Spread).WithNumber("distance", 4.3f);
            Assert.AreEqual(cache.KeyFor(far1), cache.KeyFor(far2));

            // ...but the three bands themselves must stay distinct.
            var close = new HapticEvent(HapticEventType.Spread).WithNumber("distance", 0.2f);
            var arm = new HapticEvent(HapticEventType.Spread).WithNumber("distance", 1.0f);
            Assert.AreNotEqual(cache.KeyFor(close), cache.KeyFor(arm));
            Assert.AreNotEqual(cache.KeyFor(arm), cache.KeyFor(far1));
        }

        [Test]
        public void Store_RoundTripsClipsAcrossSessions()
        {
            // The whole point of the store: a fresh cache (i.e. a new play session) must come back
            // populated, so it issues no backend requests at all.
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "haptic-clip-store-test-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var store = new HapticClipStore("test-profile", root);
                var written = new HapticClipCache(4, store);
                var amplitudes = new byte[] { 0, 64, 255, 12 };
                written.Put(new HapticClipData("Ignite|intensity=3", amplitudes, 320, "a sharp jolt"));

                var reloaded = new HapticClipCache(4, new HapticClipStore("test-profile", root));
                Assert.AreEqual(1, reloaded.Count, "clip should survive into a new session");
                Assert.IsTrue(reloaded.TryGet("Ignite|intensity=3", out var clip));
                CollectionAssert.AreEqual(amplitudes, clip.Amplitudes);
                Assert.AreEqual(320, clip.SampleRateHz);
                Assert.AreEqual("a sharp jolt", clip.Prompt);
            }
            finally
            {
                if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
            }
        }

        [Test]
        public void Store_SeparatesProfilesSoGainChangesInvalidate()
        {
            // Amplitude gain / duration / sample rate change the bytes but not the cache key, so a
            // different profile must not serve clips baked under the old settings.
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "haptic-clip-store-test-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var a = HapticClipStore.ProfileFor(320, 1.0f, 1.4f, 0.5f, "srv");
                var b = HapticClipStore.ProfileFor(320, 1.0f, 0.7f, 0.5f, "srv");
                Assert.AreNotEqual(a, b, "different gain must produce a different profile");
                Assert.AreNotEqual(a, HapticClipStore.ProfileFor(320, 1.0f, 1.4f, 0.9f, "srv"),
                    "different texture depth must produce a different profile");

                new HapticClipCache(4, new HapticClipStore(a, root))
                    .Put(new HapticClipData("k", new byte[] { 9 }, 320));

                var other = new HapticClipCache(4, new HapticClipStore(b, root));
                Assert.AreEqual(0, other.Count, "a different profile must not reuse the old clips");
            }
            finally
            {
                if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
            }
        }

        [Test]
        public async Task PromptChain_RunsStagesInOrderThreadingOutput()
        {
            var ctx = new HapticContext(new HapticEvent(HapticEventType.Ignite));
            var chain = new PromptChain(new IPromptStage[]
            {
                new AppendStage("A"),
                new AppendStage("B"),
            });

            var state = await chain.RunAsync(ctx, CancellationToken.None);

            Assert.AreEqual(2, state.Trace.Count);
            StringAssert.Contains("[A]", state.Trace[0]);
            StringAssert.Contains("[B]", state.Trace[1]);
            StringAssert.EndsWith("+A+B", state.Current);
        }

        [Test]
        public async Task MockSynth_IsDeterministicForSamePromptAndSeed()
        {
            var synth = new MockHapticSynth();
            var opts = new HapticSynthOptions { DurationSeconds = 0.5f, Seed = 7 };

            var w1 = await synth.SynthesizeAsync("crackling fire", opts, CancellationToken.None);
            var w2 = await synth.SynthesizeAsync("crackling fire", opts, CancellationToken.None);
            var w3 = await synth.SynthesizeAsync("gentle hiss", opts, CancellationToken.None);

            CollectionAssert.AreEqual(w1.Samples, w2.Samples, "same prompt+seed must be repeatable");
            Assert.Greater(w1.Samples.Length, 0);
            CollectionAssert.AreNotEqual(w1.Samples, w3.Samples, "different prompts should differ");
        }

        [Test]
        public void ContextBuilder_ContributorsPopulateBrief()
        {
            var builder = new ContextBuilder(new List<IContextContributor>
            {
                new EventTypeContributor(),
                new SurfaceMaterialContributor(),
                new FireIntensityContributor(),
            });

            var evt = new HapticEvent(HapticEventType.Ignite)
                .WithTag("surface", "TABLE")
                .WithNumber("intensity", 0.9f);

            string brief = builder.Build(evt).ToBrief();

            StringAssert.Contains("Ignite", brief);
            StringAssert.Contains("table", brief);
            StringAssert.Contains("intense", brief);
        }

        [Test]
        public void WavCodec_RoundTripsPcm16()
        {
            var samples = new float[64];
            for (int i = 0; i < samples.Length; i++) samples[i] = (i % 2 == 0) ? 0.5f : -0.5f;
            var wf = new Waveform(samples, 16000);

            var decoded = WavCodec.Decode(WavCodec.Encode(wf));

            Assert.AreEqual(16000, decoded.SampleRateHz);
            Assert.AreEqual(samples.Length, decoded.Samples.Length);
            for (int i = 0; i < samples.Length; i++)
                Assert.That(decoded.Samples[i], Is.EqualTo(samples[i]).Within(0.001f));
        }

        [Test]
        public void Store_SeparatesMockClipsFromBackendClips()
        {
            // Run once with no service URL and the on-device mock fills the cache; without this
            // split, a later run WITH a working backend would find every event already cached and
            // never call it, so the fix would look like it had done nothing.
            Assert.AreNotEqual(
                HapticClipStore.ProfileFor(320, 1.0f, 1.4f, 0.5f, "mock"),
                HapticClipStore.ProfileFor(320, 1.0f, 1.4f, 0.5f, "srv"),
                "mock and backend clips must not share a cache directory");
        }

        [Test]
        public void WaveformToAmplitudes_TextureDepthModulatesAFlatEnvelope()
        {
            // A constant-amplitude signal is the clearest case: its envelope is flat, so any
            // variation in the output can only have come from the re-imposed texture.
            var samples = new float[4000];
            for (int i = 0; i < samples.Length; i++) samples[i] = 0.5f;
            var wf = new Waveform(samples, 8000);

            byte[] flat = WaveformToAmplitudes.Convert(wf, 320, gain: 1.4f, textureDepth: 0f);
            byte[] textured = WaveformToAmplitudes.Convert(wf, 320, gain: 1.4f, textureDepth: 0.55f);

            Assert.AreEqual(flat.Length, textured.Length);
            foreach (var a in flat)
                Assert.AreEqual(flat[0], a, "depth 0 must reproduce the plain peak envelope");

            int min = 255, max = 0;
            foreach (var a in textured)
            {
                if (a < min) min = a;
                if (a > max) max = a;
            }
            Assert.Greater(max - min, 40, "textured output should swing well beyond the flat envelope");
        }

        /// <summary>Test double: appends "+name" to the running text so ordering is observable.</summary>
        sealed class AppendStage : IPromptStage
        {
            public string Name { get; }
            public AppendStage(string name) => Name = name;

            public Task<PromptState> RunAsync(PromptState state, CancellationToken ct)
            {
                state.Record(Name, (state.Current ?? "") + "+" + Name);
                return Task.FromResult(state);
            }
        }
    }
}
