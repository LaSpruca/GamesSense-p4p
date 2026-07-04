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
