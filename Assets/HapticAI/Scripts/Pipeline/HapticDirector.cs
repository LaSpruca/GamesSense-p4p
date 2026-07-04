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
    /// (playback is the OVR glue's job); instead it decides WHICH clip should play and raises
    /// <see cref="ClipReady"/> for the glue to consume:
    ///
    ///   • cache hit           → raise the cached clip immediately.
    ///   • first-time (miss)   → raise a procedural fallback now, generate the real clip in the
    ///                           background, and cache it so the next identical event is instant.
    ///   • <see cref="WarmAsync"/> pre-generates a canonical set at scene load.
    ///
    /// <see cref="ClipReady"/> fires on the main thread because the pipeline's awaits resume there.
    /// </summary>
    public sealed class HapticDirector
    {
        readonly HapticPipeline _pipeline;
        readonly HapticClipCache _cache;
        readonly int _sampleRateHz;
        readonly HashSet<string> _inFlight = new HashSet<string>();

        /// <summary>Raised whenever a clip should play. Subscribe from the glue to actually play it.</summary>
        public event Action<HapticClipData> ClipReady;
        /// <summary>Raised (best-effort) when a background generation throws, for logging.</summary>
        public event Action<string, Exception> GenerationFailed;

        public HapticDirector(HapticPipeline pipeline, HapticClipCache cache, int sampleRateHz)
        {
            _pipeline = pipeline;
            _cache = cache;
            _sampleRateHz = sampleRateHz;
        }

        /// <summary>Request haptics for an event. Returns immediately; ClipReady may fire now and/or later.</summary>
        public void Request(HapticEvent evt, CancellationToken ct = default)
        {
            string key = _cache.KeyFor(evt);
            if (_cache.TryGet(key, out var cached))
            {
                ClipReady?.Invoke(cached);
                return;
            }

            // Immediate feedback while the real clip is generated.
            ClipReady?.Invoke(FallbackHaptics.Build(evt, _sampleRateHz, key));
            _ = GenerateAsync(evt, key, playWhenReady: false, ct);
        }

        /// <summary>Pre-generate clips for a set of canonical events so runtime hits are instant.</summary>
        public async Task WarmAsync(IEnumerable<HapticEvent> events, CancellationToken ct = default)
        {
            foreach (var evt in events)
            {
                string key = _cache.KeyFor(evt);
                if (_cache.TryGet(key, out _)) continue;
                await GenerateAsync(evt, key, playWhenReady: false, ct);
            }
        }

        async Task GenerateAsync(HapticEvent evt, string key, bool playWhenReady, CancellationToken ct)
        {
            if (!_inFlight.Add(key)) return; // already generating this key
            try
            {
                var clip = await _pipeline.GenerateAsync(evt, key, ct);
                _cache.Put(clip);
                if (playWhenReady) ClipReady?.Invoke(clip);
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
