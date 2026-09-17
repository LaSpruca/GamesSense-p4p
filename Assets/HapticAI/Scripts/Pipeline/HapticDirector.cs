using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HapticAI.Cache;
using HapticAI.Core;
using HapticAI.Playback;

namespace HapticAI.Pipeline
{
    /// <summary>
    /// Runtime coordinator implementing the hybrid timing model. It never plays anything itself
    /// (playback is the OVR glue's job); instead it decides WHICH clip should play and hands it to
    /// the per-request callback:
    ///
    ///   • cache hit           → deliver the cached clip immediately.
    ///   • first-time (miss)   → deliver a procedural fallback now, generate the real clip in the
    ///                           background, and cache it so the next identical event is instant.
    ///   • <see cref="WarmAsync"/> pre-generates a canonical set at scene load.
    ///
    /// The callback is per-request rather than a single broadcast event because callers need to
    /// know what a clip is FOR: a spray clip loops, a fire cue plays once. Callbacks fire on the
    /// main thread because the pipeline's awaits resume there.
    /// </summary>
    public sealed class HapticDirector
    {
        readonly HapticPipeline _pipeline;
        readonly HapticClipCache _cache;
        readonly int _sampleRateHz;
        readonly HashSet<string> _inFlight = new HashSet<string>();

        /// <summary>Raised (best-effort) when a background generation throws, for logging.</summary>
        public event Action<string, Exception> GenerationFailed;

        public HapticDirector(HapticPipeline pipeline, HapticClipCache cache, int sampleRateHz)
        {
            _pipeline = pipeline;
            _cache = cache;
            _sampleRateHz = sampleRateHz;
        }

        /// <summary>
        /// Request haptics for an event. Returns immediately; <paramref name="onClip"/> is invoked
        /// straight away with either the cached clip or a procedural stand-in.
        ///
        /// <paramref name="upgradeWhenReady"/> makes it fire a SECOND time with the real clip once
        /// generation finishes. That is right for a continuously looping effect, where swapping the
        /// waveform mid-loop is seamless, and wrong for a discrete cue, where it would land as a
        /// stray second buzz seconds after the thing that caused it.
        /// </summary>
        public void Request(HapticEvent evt, Action<HapticClipData> onClip,
            bool upgradeWhenReady = false, CancellationToken ct = default)
        {
            string key = _cache.KeyFor(evt);
            if (_cache.TryGet(key, out var cached))
            {
                onClip?.Invoke(cached);
                return;
            }

            // Immediate feedback while the real clip is generated.
            onClip?.Invoke(FallbackHaptics.Build(evt, _sampleRateHz, key));
            _ = GenerateAsync(evt, key, upgradeWhenReady ? onClip : null, ct);
        }

        /// <summary>
        /// Pre-generate clips for a set of canonical events so runtime hits are instant.
        /// Generations are issued concurrently: each one is a 17-34 s backend round trip that is
        /// almost entirely spent waiting on the network, so awaiting them one at a time made scene
        /// load take the SUM of the set (~2 min for six) instead of the slowest single request.
        /// Anything already cached — including clips restored from disk — is skipped, so a warm
        /// start issues no requests at all.
        /// </summary>
        public async Task WarmAsync(IEnumerable<HapticEvent> events, CancellationToken ct = default)
        {
            var pending = new List<Task>();
            foreach (var evt in events)
            {
                string key = _cache.KeyFor(evt);
                if (_cache.TryGet(key, out _)) continue;
                pending.Add(GenerateAsync(evt, key, null, ct));
            }

            if (pending.Count > 0) await Task.WhenAll(pending);
        }

        async Task GenerateAsync(HapticEvent evt, string key, Action<HapticClipData> onReady, CancellationToken ct)
        {
            if (!_inFlight.Add(key)) return; // already generating this key
            try
            {
                var clip = await _pipeline.GenerateAsync(evt, key, ct);
                _cache.Put(clip);
                onReady?.Invoke(clip);
            }
            catch (OperationCanceledException) { /* scene teardown / superseded */ }
            catch (Exception e)
            {
                GenerationFailed?.Invoke(key, e);
            }
            finally
            {
                _inFlight.Remove(key);
            }
        }
    }
}
