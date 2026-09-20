using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HapticAI.Core;
using HapticAI.Synth;

namespace HapticAI.Profiles
{
    /// <summary>
    /// The no-server path: the same palette the server would plan without a Gemini key (one
    /// profile per role, offline template prompts), synthesised on-device by the mock synth. The
    /// scene runs identically with or without a laptop; only the feel of the clips differs.
    /// </summary>
    public sealed class OfflineProfileSynth
    {
        readonly IHapticSynthProvider _synth;

        public OfflineProfileSynth(IHapticSynthProvider synth) => _synth = synth;

        /// <summary>The offline palette for a room, described by its surface labels ("TABLE, FLOOR").</summary>
        public static HapticProfileSet BuildSet(string surfaces)
        {
            var profiles = new List<HapticProfile>(HapticRoles.All.Length);
            foreach (var spec in HapticRoles.All)
            {
                profiles.Add(new HapticProfile(spec.Name, spec.Name, HapticRoles.OfflinePrompt(spec, surfaces),
                    spec.DurationSeconds, spec.Loop, 1f));
            }
            return new HapticProfileSet(SetIdFor(profiles), "offline", "offline", "on-device template palette", profiles);
        }

        /// <summary>Same recipe as the server: a hash over the prompts, so identical palettes share a store entry.</summary>
        public static string SetIdFor(IList<HapticProfile> profiles)
        {
            var sb = new StringBuilder("offline");
            foreach (var p in profiles) sb.Append('|').Append(p.Id).Append(':').Append(p.Prompt).Append(':').Append(p.DurationSeconds);
            return "off-" + Hashing.Fnv1a(sb.ToString()).ToString("x8");
        }

        /// <summary>Synthesise every profile in the library's set that still lacks a clip, loops first.</summary>
        public async Task SynthesiseMissingAsync(HapticProfileLibrary library, CancellationToken ct)
        {
            var set = library.Set;
            if (set == null) return;

            var pending = new List<HapticProfile>();
            foreach (var p in set.Profiles) if (!p.HasClip && p.Loop) pending.Add(p);
            foreach (var p in set.Profiles) if (!p.HasClip && !p.Loop) pending.Add(p);

            foreach (var profile in pending)
            {
                ct.ThrowIfCancellationRequested();
                var options = new HapticSynthOptions
                {
                    DurationSeconds = profile.DurationSeconds,
                    Seed = (int)(Hashing.Fnv1a(profile.Id) & 0x7FFFFFFF),
                };
                var waveform = await _synth.SynthesizeAsync(profile.Prompt, options, ct);
                library.ReceiveWaveform(set.SetId, profile.Id, "offline", waveform);
            }
        }
    }
}
