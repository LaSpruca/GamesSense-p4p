using System.Collections.Generic;
using System.Text;
using HapticAI.Core;

namespace HapticAI.Cache
{
    /// <summary>
    /// Caches generated clips keyed by event type plus coarse-bucketed cues, so slightly
    /// different events (trigger at 0.71 vs 0.73) reuse the same clip and play instantly. The
    /// bucket granularity is the knob that trades variety against cache hit-rate.
    /// </summary>
    public sealed class HapticClipCache
    {
        readonly Dictionary<string, HapticClipData> _clips = new Dictionary<string, HapticClipData>();
        readonly int _buckets;

        public HapticClipCache(int buckets = 4) => _buckets = buckets < 1 ? 1 : buckets;

        public int Count => _clips.Count;

        /// <summary>Stable key: event name + each cue snapped to a bucket. Order-independent.</summary>
        public string KeyFor(HapticEvent evt)
        {
            var sb = new StringBuilder(evt.Name);

            var numberKeys = new List<string>(evt.Numbers.Keys);
            numberKeys.Sort(System.StringComparer.Ordinal);
            foreach (var k in numberKeys)
            {
                int bucket = (int)(evt.Numbers[k] * _buckets);
                sb.Append('|').Append(k).Append('=').Append(bucket);
            }

            var tagKeys = new List<string>(evt.Tags.Keys);
            tagKeys.Sort(System.StringComparer.Ordinal);
            foreach (var k in tagKeys)
                sb.Append('|').Append(k).Append(':').Append(evt.Tags[k]);

            return sb.ToString();
        }

        public bool TryGet(string key, out HapticClipData clip) => _clips.TryGetValue(key, out clip);

        public void Put(HapticClipData clip)
        {
            if (clip != null && !clip.IsFallback) _clips[clip.Key] = clip;
        }

        public void Clear() => _clips.Clear();
    }
}
