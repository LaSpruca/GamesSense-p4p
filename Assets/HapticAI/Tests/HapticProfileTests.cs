using System.Threading;
using System.Threading.Tasks;
using HapticAI.Core;
using HapticAI.Net;
using HapticAI.Playback;
using HapticAI.Profiles;
using HapticAI.Synth;
using NUnit.Framework;
using UnityEngine;

namespace HapticAI.Tests
{
    /// <summary>
    /// EditMode tests for the pure haptics logic — no headset, no network, no server required.
    /// </summary>
    public class HapticProfileTests
    {
        // --- Rendering ---------------------------------------------------------------------

        [Test]
        public void WaveformToAmplitudes_ResamplesAndBounds()
        {
            var samples = new float[1000];
            for (int i = 0; i < samples.Length; i++) samples[i] = 1f;
            var wf = new Waveform(samples, 1000);

            byte[] amps = WaveformToAmplitudes.Convert(wf, targetRateHz: 100);

            Assert.AreEqual(100, amps.Length, "should resample to target rate * duration");
            foreach (var a in amps)
                Assert.AreEqual(255, a, "full-scale input with gain should saturate to 255");
        }

        [Test]
        public void WaveformToAmplitudes_TextureDepthModulatesAFlatEnvelope()
        {
            var samples = new float[4000];
            for (int i = 0; i < samples.Length; i++) samples[i] = 0.5f;
            var wf = new Waveform(samples, 8000);

            byte[] flat = WaveformToAmplitudes.Convert(wf, 320, gain: 1.4f, textureDepth: 0f);
            byte[] textured = WaveformToAmplitudes.Convert(wf, 320, gain: 1.4f, textureDepth: 0.55f);

            Assert.AreEqual(flat.Length, textured.Length);
            foreach (var a in flat) Assert.AreEqual(flat[0], a);
            int min = 255, max = 0;
            foreach (var a in textured) { if (a < min) min = a; if (a > max) max = a; }
            Assert.Greater(max - min, 40, "textured output should swing well beyond the flat envelope");
        }

        [Test]
        public void WavCodec_RoundTripsPcm16()
        {
            var samples = new float[64];
            for (int i = 0; i < samples.Length; i++) samples[i] = (i % 2 == 0) ? 0.5f : -0.5f;
            var decoded = WavCodec.Decode(WavCodec.Encode(new Waveform(samples, 16000)));

            Assert.AreEqual(16000, decoded.SampleRateHz);
            Assert.AreEqual(samples.Length, decoded.Samples.Length);
            for (int i = 0; i < samples.Length; i++)
                Assert.That(decoded.Samples[i], Is.EqualTo(samples[i]).Within(0.001f));
        }

        [Test]
        public async Task MockSynth_IsDeterministicForSamePromptAndSeed()
        {
            var synth = new MockHapticSynth();
            var opts = new HapticSynthOptions { DurationSeconds = 0.5f, Seed = 7 };

            var w1 = await synth.SynthesizeAsync("crackling fire", opts, CancellationToken.None);
            var w2 = await synth.SynthesizeAsync("crackling fire", opts, CancellationToken.None);
            var w3 = await synth.SynthesizeAsync("gentle hiss", opts, CancellationToken.None);

            CollectionAssert.AreEqual(w1.Samples, w2.Samples);
            CollectionAssert.AreNotEqual(w1.Samples, w3.Samples);
        }

        // --- Roles and fallbacks --------------------------------------------------------------

        [Test]
        public void Roles_EveryRoleHasAFallbackClipOfRoughlyItsLength()
        {
            foreach (var spec in HapticRoles.All)
            {
                var clip = FallbackHaptics.ForRole(spec.Name, 320);
                Assert.IsTrue(clip.IsFallback);
                Assert.Greater(clip.Amplitudes.Length, 0, spec.Name);
                Assert.LessOrEqual(clip.DurationSeconds, spec.DurationSeconds + 0.3f, spec.Name);
                int peak = 0;
                foreach (var a in clip.Amplitudes) if (a > peak) peak = a;
                Assert.Greater(peak, 40, $"{spec.Name} stand-in should be perceptible");
            }
            Assert.IsTrue(HapticRoles.IsLoop(HapticRoles.SprayBase));
            Assert.IsFalse(HapticRoles.IsLoop(HapticRoles.Ignite));
            Assert.IsFalse(HapticRoles.IsLoop("not-a-role"));
        }

        [Test]
        public void Fallbacks_SprayShapesAreDistinctFromEachOther()
        {
            // The whole point of per-role stand-ins: base must not feel like top before the palette lands.
            var b = FallbackHaptics.ForRole(HapticRoles.SprayBase, 320).Amplitudes;
            var t = FallbackHaptics.ForRole(HapticRoles.SprayTop, 320).Amplitudes;
            var d = FallbackHaptics.ForRole(HapticRoles.SprayDry, 320).Amplitudes;
            Assert.Greater(Mean(b), Mean(t), "base should be stronger than top");
            Assert.Greater(Mean(b), Mean(d), "base should be stronger than dry");
        }

        static float Mean(byte[] a) { float s = 0; foreach (var v in a) s += v; return s / a.Length; }

        // --- Selector -------------------------------------------------------------------------

        [Test]
        public void Selector_MapsEachSituationToItsRole()
        {
            Assert.AreEqual(HapticProfileSelector.IdleKey, HapticProfileSelector.CoarseKey(new SprayReading()));
            Assert.IsTrue(HapticProfileSelector.Rules(new SprayReading()).IsSilent);

            var dry = new SprayReading { Spraying = true, Trigger = 0.5f, HitRate = 0f };
            Assert.AreEqual("dry|t1", HapticProfileSelector.CoarseKey(dry));
            Assert.AreEqual(HapticRoles.SprayDry, HapticProfileSelector.Rules(dry).Role);

            var top = new SprayReading { Spraying = true, Trigger = 0.9f, HitRate = 80f, HitQuality = 0.25f };
            Assert.AreEqual("top|t2", HapticProfileSelector.CoarseKey(top));
            Assert.AreEqual(HapticRoles.SprayTop, HapticProfileSelector.Rules(top).Role);

            var baseHit = new SprayReading { Spraying = true, Trigger = 0.9f, HitRate = 140f, HitQuality = 0.9f };
            Assert.AreEqual(HapticRoles.SprayBase, HapticProfileSelector.Rules(baseHit).Role);

            var sweep = new SprayReading { Spraying = true, Trigger = 1f, HitRate = 150f, HitQuality = 0.85f, Sweeping = true };
            Assert.AreEqual("sweep|t2", HapticProfileSelector.CoarseKey(sweep));
            Assert.AreEqual(HapticRoles.SpraySweep, HapticProfileSelector.Rules(sweep).Role);

            var soak = new SprayReading { Spraying = true, Trigger = 0.7f, HitRate = 5f, Soaking = true };
            Assert.AreEqual(HapticRoles.SpraySoak, HapticProfileSelector.Rules(soak).Role);

            // Sweeping only counts when the foam is actually on the base.
            var sweepingButHigh = new SprayReading { Spraying = true, Trigger = 1f, HitRate = 150f, HitQuality = 0.2f, Sweeping = true };
            Assert.AreEqual(HapticRoles.SprayTop, HapticProfileSelector.Rules(sweepingButHigh).Role);
        }

        static SprayReading SprayAt(float hitRate, float quality, bool sweeping = false, bool soaking = false) =>
            new SprayReading
            {
                Spraying = true, Trigger = 0.7f, HitRate = hitRate, HitQuality = quality,
                Sweeping = sweeping, Soaking = soaking,
            };

        [Test]
        public void Selector_SprayIsOneStrongLevelEverywhere()
        {
            foreach (var r in new[]
            {
                SprayAt(100f, 1f, sweeping: true), SprayAt(100f, 0.7f), SprayAt(100f, 0.3f),
                SprayAt(0f, 0f, soaking: true), SprayAt(0f, 0f),
            })
                Assert.That(HapticProfileSelector.Rules(r).Gain, Is.EqualTo(HapticProfileSelector.SprayGain).Within(0.001f));
            Assert.AreEqual("rules", HapticProfileSelector.Rules(SprayAt(0f, 0f)).Source);
        }

        [Test]
        public void Selector_SteadyOnTheBaseSlowPulseSlightlyOffFastPulseWayOff()
        {
            Assert.AreEqual(0f, HapticProfileSelector.Rules(SprayAt(100f, 1f, sweeping: true)).PulseHz);
            Assert.AreEqual(0f, HapticProfileSelector.Rules(SprayAt(100f, 0.8f)).PulseHz);
            Assert.AreEqual(0f, HapticProfileSelector.Rules(SprayAt(0f, 0f, soaking: true)).PulseHz);

            Assert.AreEqual(HapticProfileSelector.SlowPulseHz, HapticProfileSelector.Rules(SprayAt(100f, 0.3f)).PulseHz);
            Assert.AreEqual(HapticProfileSelector.FastPulseHz, HapticProfileSelector.Rules(SprayAt(0f, 0f)).PulseHz);
            Assert.Less(HapticProfileSelector.SlowPulseHz, HapticProfileSelector.FastPulseHz);
        }

        [Test]
        public void Selector_GainIgnoresTriggerDepth()
        {
            var light = HapticProfileSelector.Rules(new SprayReading { Spraying = true, Trigger = 0.2f, HitRate = 100f, HitQuality = 1f });
            var hard = HapticProfileSelector.Rules(new SprayReading { Spraying = true, Trigger = 1f, HitRate = 100f, HitQuality = 1f });
            Assert.That(hard.Gain, Is.EqualTo(light.Gain).Within(0.001f));
        }

        [Test]
        public void Selector_RuleGainsSitInsideTheirGeminiBands()
        {
            foreach (var r in new[]
            {
                new SprayReading { Spraying = true, HitRate = 100f, HitQuality = 1f, Sweeping = true },
                new SprayReading { Spraying = true, HitRate = 100f, HitQuality = 0.6f },
                new SprayReading { Spraying = true, HitRate = 100f, HitQuality = 1f },
                new SprayReading { Spraying = true, HitRate = 100f, HitQuality = 0f },
                new SprayReading { Spraying = true, HitRate = 100f, HitQuality = 0.59f },
                new SprayReading { Spraying = true, Soaking = true },
                new SprayReading { Spraying = true },
            })
            {
                var sel = HapticProfileSelector.Rules(r);
                HapticProfileSelector.GainBand(HapticProfileSelector.ContactOf(sel.StateKey), out float min, out float max);
                Assert.That(sel.Gain, Is.InRange(min, max), sel.StateKey);
            }
        }

        // --- Library and store ----------------------------------------------------------------

        static HapticProfileSet MakeSet(string id = "set-1")
        {
            var profiles = new System.Collections.Generic.List<HapticProfile>
            {
                new HapticProfile(HapticRoles.SprayBase, HapticRoles.SprayBase, "dense chug", 1.5f, true, 0.9f),
                new HapticProfile(HapticRoles.Ignite, HapticRoles.Ignite, "sharp whump", 0.8f, false, 1f),
            };
            return new HapticProfileSet(id, "scene", "gemini-live", "notes", profiles);
        }

        static Waveform Tone(float seconds = 0.5f)
        {
            var s = new float[(int)(seconds * 8000)];
            for (int i = 0; i < s.Length; i++) s[i] = 0.6f * Mathf.Sin(i * 0.1f);
            return new Waveform(s, 8000);
        }

        [Test]
        public void Library_UsesStandInUntilTheClipLandsThenTheClip()
        {
            var library = new HapticProfileLibrary(320, 1.4f, 0.5f, store: null);
            var missing = library.AdoptManifest(MakeSet());
            Assert.AreEqual(2, missing.Count, "nothing on disk, so every clip is missing");

            var before = library.ClipFor(HapticRoles.SprayBase, out float gain, out bool fallback);
            Assert.IsTrue(fallback);
            Assert.AreEqual(1f, gain, "stand-ins play at unity gain");

            Assert.IsFalse(library.ReceiveWaveform("other-set", HapticRoles.SprayBase, "srv", Tone()),
                "a clip for a different set must be ignored");
            Assert.IsTrue(library.ReceiveWaveform("set-1", HapticRoles.SprayBase, "srv", Tone()));

            var after = library.ClipFor(HapticRoles.SprayBase, out gain, out fallback);
            Assert.IsFalse(fallback);
            Assert.AreNotSame(before, after);
            Assert.That(gain, Is.EqualTo(0.9f).Within(0.001f), "the planner's gain travels with the clip");
            Assert.AreEqual(1, library.ClipCount);
        }

        [Test]
        public void Store_RoundTripsAPaletteSoTheNextSessionNeedsNoClips()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "haptic-profile-store-test-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var store = new HapticProfileStore("test-profile", root);
                var first = new HapticProfileLibrary(320, 1.4f, 0.5f, store);
                first.AdoptManifest(MakeSet());
                first.ReceiveWaveform("set-1", HapticRoles.SprayBase, "hapticgen", Tone());
                first.ReceiveWaveform("set-1", HapticRoles.Ignite, "hapticgen", Tone(0.3f));

                var second = new HapticProfileLibrary(320, 1.4f, 0.5f, new HapticProfileStore("test-profile", root));
                var missing = second.AdoptManifest(MakeSet());
                Assert.AreEqual(0, missing.Count, "a warm start should need no clips from the server");
                Assert.AreEqual(2, second.ClipCount);
                var clip = second.ClipFor(HapticRoles.Ignite, out _, out bool fallback);
                Assert.IsFalse(fallback);
                CollectionAssert.AreEqual(first.ClipFor(HapticRoles.Ignite, out _, out _).Amplitudes, clip.Amplitudes);
                Assert.AreEqual("hapticgen", second.ProfileFor(HapticRoles.Ignite).ClipSource);
            }
            finally
            {
                if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
            }
        }

        [Test]
        public void Store_ProfilesSeparateMockFromServerAndGainChanges()
        {
            Assert.AreNotEqual(HapticProfileStore.ProfileFor(320, 1.4f, 0.5f, "mock"),
                HapticProfileStore.ProfileFor(320, 1.4f, 0.5f, "srv"));
            Assert.AreNotEqual(HapticProfileStore.ProfileFor(320, 1.4f, 0.5f, "srv"),
                HapticProfileStore.ProfileFor(320, 0.7f, 0.5f, "srv"));
        }

        [Test]
        public void OfflineSet_CoversEveryRoleWithAStableId()
        {
            var a = OfflineProfileSynth.BuildSet("TABLE, FLOOR");
            var b = OfflineProfileSynth.BuildSet("TABLE, FLOOR");
            var c = OfflineProfileSynth.BuildSet("COUCH");
            Assert.AreEqual(HapticRoles.All.Length, a.Profiles.Count);
            Assert.AreEqual(a.SetId, b.SetId, "same room, same palette id");
            Assert.AreNotEqual(a.SetId, c.SetId, "different room, different palette id");
            foreach (var spec in HapticRoles.All) Assert.IsNotNull(a.ByRole(spec.Name), spec.Name);
        }

        // --- Wire ------------------------------------------------------------------------------

        [Test]
        public void Wire_MessagesSerialiseWithTheServerFieldNames()
        {
            string state = JsonUtility.ToJson(HapticWire.State.From(new SprayReading
            {
                Spraying = true, Trigger = 0.876f, HitRate = 123.456f, HitQuality = 0.9f, Sweeping = true, Stage = 3,
            }));
            StringAssert.Contains("\"type\":\"state\"", state);
            // JsonUtility prints floats at full binary precision (0.8799999952...), so check the
            // rounded prefix rather than an exact decimal.
            StringAssert.Contains("\"hit_rate\":123.4", state);
            StringAssert.Contains("\"trigger\":0.8", state);
            StringAssert.DoesNotContain("123.456", state);
            StringAssert.Contains("\"sweeping\":true", state);
            StringAssert.Contains("\"stage\":3", state);
            Assert.AreEqual("state", HapticWire.TypeOf(state));

            var select = JsonUtility.FromJson<HapticWire.Select>(
                "{\"type\":\"select\",\"profile\":\"spray_base\",\"gain\":0.8,\"source\":\"gemini\",\"reason\":\"on the base\",\"state_key\":\"base|t2\"}");
            Assert.AreEqual("spray_base", select.profile);
            Assert.That(select.gain, Is.EqualTo(0.8f).Within(0.001f));
            Assert.AreEqual("base|t2", select.state_key);

            var profiles = JsonUtility.FromJson<HapticWire.Profiles>(
                "{\"type\":\"profiles\",\"set_id\":\"abc\",\"scene_key\":\"k\",\"source\":\"gemini-live\",\"notes\":\"n\"," +
                "\"profiles\":[{\"id\":\"ignite\",\"role\":\"ignite\",\"prompt\":\"p\",\"duration_s\":0.8,\"loop\":false,\"gain\":0.7}]}");
            Assert.AreEqual(1, profiles.profiles.Length);
            Assert.AreEqual("ignite", profiles.profiles[0].id);
            Assert.IsFalse(profiles.profiles[0].loop);
        }

        [Test]
        public void SessionClient_DerivesTheSocketUrlFromTheServiceUrl()
        {
            Assert.AreEqual("ws://192.168.1.5:8080/ws", HapticSessionClient.ToSocketUri("http://192.168.1.5:8080").ToString());
            Assert.AreEqual("ws://192.168.1.5:8080/ws", HapticSessionClient.ToSocketUri("http://192.168.1.5:8080/").ToString());
            Assert.AreEqual("wss://laptop.local/ws", HapticSessionClient.ToSocketUri("https://laptop.local").ToString());
            Assert.AreEqual("ws://localhost:8080/ws", HapticSessionClient.ToSocketUri("ws://localhost:8080/ws").ToString());
        }
    }
}
