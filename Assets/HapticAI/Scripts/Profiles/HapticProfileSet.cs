using System;
using System.Collections.Generic;
using HapticAI.Core;

namespace HapticAI.Profiles
{
    /// <summary>
    /// One haptic profile: a role, the prompt that describes it, and - once it has been
    /// synthesised and delivered - the clip. A profile without a clip is played through the
    /// procedural stand-in for its role (<see cref="Playback.FallbackHaptics"/>), so the trainee
    /// feels something from the first frame and the real clip simply takes over when it lands.
    /// </summary>
    public sealed class HapticProfile
    {
        public readonly string Id;
        public readonly string Role;
        public readonly string Prompt;
        public readonly float DurationSeconds;
        public readonly bool Loop;
        /// <summary>The planner's suggested level for this profile, 0..1. Multiplied by the live selection gain.</summary>
        public readonly float Gain;

        public HapticClipData Clip { get; private set; }
        /// <summary>Where the clip came from: the server's backend tag, "offline", or null until it arrives.</summary>
        public string ClipSource { get; private set; }
        public bool HasClip => Clip != null && Clip.Amplitudes.Length > 0;

        public HapticProfile(string id, string role, string prompt, float durationSeconds, bool loop, float gain)
        {
            Id = id;
            Role = role;
            Prompt = prompt;
            DurationSeconds = durationSeconds > 0f ? durationSeconds : 1f;
            Loop = loop;
            Gain = gain <= 0f ? 1f : Math.Min(1f, gain);
        }

        public void SetClip(HapticClipData clip, string source)
        {
            Clip = clip;
            ClipSource = source;
        }
    }

    /// <summary>
    /// The palette of profiles planned for one scene. Identified by <see cref="SetId"/>, which the
    /// server derives from the scene and the prompts, so a headset that has this set on disk can
    /// skip synthesis entirely on its next visit to the same room.
    /// </summary>
    public sealed class HapticProfileSet
    {
        public readonly string SetId;
        public readonly string SceneKey;
        /// <summary>How the plan was made: "gemini-live", "gemini-http", "offline", "cache:...".</summary>
        public readonly string Source;
        public readonly string Notes;
        public readonly IReadOnlyList<HapticProfile> Profiles;

        readonly Dictionary<string, HapticProfile> _byId = new Dictionary<string, HapticProfile>(StringComparer.Ordinal);
        readonly Dictionary<string, HapticProfile> _byRole = new Dictionary<string, HapticProfile>(StringComparer.Ordinal);

        public HapticProfileSet(string setId, string sceneKey, string source, string notes, IList<HapticProfile> profiles)
        {
            SetId = setId ?? string.Empty;
            SceneKey = sceneKey ?? string.Empty;
            Source = source ?? "unknown";
            Notes = notes ?? string.Empty;
            var list = new List<HapticProfile>(profiles?.Count ?? 0);
            if (profiles != null)
            {
                foreach (var p in profiles)
                {
                    if (p == null) continue;
                    list.Add(p);
                    _byId[p.Id] = p;
                    // First profile for a role wins; the server sends exactly one per role anyway.
                    if (!_byRole.ContainsKey(p.Role)) _byRole[p.Role] = p;
                }
            }
            Profiles = list;
        }

        public HapticProfile ById(string id) => id != null && _byId.TryGetValue(id, out var p) ? p : null;
        public HapticProfile ByRole(string role) => role != null && _byRole.TryGetValue(role, out var p) ? p : null;

        public int ClipCount
        {
            get
            {
                int n = 0;
                foreach (var p in Profiles) if (p.HasClip) n++;
                return n;
            }
        }

        public bool Complete => ClipCount == Profiles.Count && Profiles.Count > 0;

        public List<string> MissingClipIds()
        {
            var ids = new List<string>();
            foreach (var p in Profiles) if (!p.HasClip) ids.Add(p.Id);
            return ids;
        }
    }
}
