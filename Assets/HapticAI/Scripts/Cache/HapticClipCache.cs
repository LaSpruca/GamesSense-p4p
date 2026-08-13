using System.Collections.Generic;
using System.Text;
using HapticAI.Core;

namespace HapticAI.Cache
{
    /// <summary>
    /// Caches generated clips keyed by event type plus coarse-bucketed cues, so slightly
    /// different events (trigger at 0.71 vs 0.73) reuse the same clip and play instantly. The
    /// bucket granularity is the knob that trades variety against cache hit-rate.
    ///
    /// Pass a <see cref="HapticClipStore"/> to make the cache survive the session. Generation costs
    /// a 17-34 s backend round trip, so an in-memory-only cache pays that on every single run;
    /// with a store, only the very first run ever hits the backend.
    /// </summary>
    public sealed class HapticClipCache
    {
        readonly Dictionary<string, HapticClipData> _clips = new Dictionary<string, HapticClipData>();
        readonly int _buckets;
        readonly HapticClipStore _store;   // null = in-memory only (the unit tests use this)

        public HapticClipCache(int buckets = 4, HapticClipStore store = null)
        {
            _buckets = buckets < 1 ? 1 : buckets;
            _store = store;
            if (_store == null) return;

            foreach (var clip in _store.LoadAll())
                _clips[clip.Key] = clip;
        }

        public int Count => _clips.Count;

        /// <summary>Stable key: event name + each cue snapped to a bucket. Order-independent.</summary>
        public string KeyFor(HapticEvent evt)
        {
            var sb = new StringBuilder(evt.Name);

            var numberKeys = new List<string>(evt.Numbers.Keys);
            numberKeys.Sort(System.StringComparer.Ordinal);
            foreach (var k in numberKeys)
            {
                sb.Append('|').Append(k).Append('=').Append(Bucket(k, evt.Numbers[k]));
            }

            var tagKeys = new List<string>(evt.Tags.Keys);
            tagKeys.Sort(System.StringComparer.Ordinal);
            foreach (var k in tagKeys)
                sb.Append('|').Append(k).Append(':').Append(evt.Tags[k]);

            return sb.ToString();
        }

        /// <summary>
        /// Snap one cue to a bucket. The uniform <c>value * buckets</c> rule assumes a cue is
        /// normalised 0..1, which holds for intensity and triggerDepth.
        ///
        /// <c>distance</c> is the exception and it silently wrecked the hit-rate: it is in metres
        /// and unbounded, so uniform bucketing minted a fresh key every 0.25 m — a fire 4 m away and
        /// the same fire 4.3 m away were different cache entries. Yet the backend collapses distance
        /// into exactly three descriptors, so all of those keys generate an identical prompt and an
        /// identical clip. Matching those thresholds here means events that would produce the same
        /// clip now share one. Keep in sync with build_brief() in server/haptic_prompt.py.
        /// </summary>
        int Bucket(string cue, float value)
        {
            if (cue == "distance")
                return value < 0.5f ? 0 : value < 1.5f ? 1 : 2;
            return (int)(value * _buckets);
        }

        public bool TryGet(string key, out HapticClipData clip) => _clips.TryGetValue(key, out clip);

        public void Put(HapticClipData clip)
        {
            // Fallback clips are the cheap procedural stand-in played while the real one generates;
            // caching them would permanently pin the placeholder in place of the real clip.
            if (clip == null || clip.IsFallback) return;
            _clips[clip.Key] = clip;
            _store?.Save(clip);
        }

        public void Clear()
        {
            _clips.Clear();
            _store?.Clear();
        }
    }
}
