using System.Threading;
using FireSim;
using HapticAI.Core;
using HapticAI.Pipeline;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace HapticAI.Scene
{
    /// <summary>
    /// Bridges the fire simulation to the AI haptics pipeline. It listens to fire events
    /// (ignite/spread/extinguish/complete) and the foam sprayer, packages each as a
    /// <see cref="HapticEvent"/> carrying the environmental cues available at that instant, and
    /// hands it to the <see cref="HapticDirector"/>. This is the seam where "what happened in the
    /// world" becomes "what to generate".
    ///
    /// It also decides how each resulting clip is played, which the director deliberately does not
    /// know about: fire events are one-shot cues, while the spray is a sustained loop that lasts as
    /// long as the trigger is held. See <see cref="HapticPlayer"/>.
    /// </summary>
    public sealed class FireHapticRelay : MonoBehaviour
    {
        /// <summary>Name of the custom event raised when the user puts the last fire out.</summary>
        public const string CompleteEventName = "Complete";

        // Individual labels probed to name the surface a fire sits on (order = priority).
        static readonly MRUKAnchor.SceneLabels[] CandidateLabels =
        {
            MRUKAnchor.SceneLabels.TABLE, MRUKAnchor.SceneLabels.STORAGE, MRUKAnchor.SceneLabels.COUCH,
            MRUKAnchor.SceneLabels.BED, MRUKAnchor.SceneLabels.SCREEN, MRUKAnchor.SceneLabels.WALL_ART,
            MRUKAnchor.SceneLabels.WALL_FACE, MRUKAnchor.SceneLabels.FLOOR, MRUKAnchor.SceneLabels.CEILING,
            MRUKAnchor.SceneLabels.OTHER,
        };

        HapticDirector _director;
        HapticPlayer _player;
        Transform _head;
        SurfaceFireManager _fireManager;
        FoamSprayer _foamSprayer;
        CancellationTokenSource _cts;
        int _sprayBucket = -1;
        int _cacheBuckets = 4;
        bool _muted;

        /// <summary>Human-readable description of the last clip played, for the debug readout.</summary>
        public string LastClipDescription { get; private set; } = "(nothing played yet)";
        /// <summary>Count of clips that arrived as procedural fallbacks rather than generated ones.</summary>
        public int FallbackCount { get; private set; }
        public int GeneratedCount { get; private set; }

        /// <summary>
        /// Silence the AI path without tearing it down, so the session can flip between the static
        /// and AI conditions instantly. Events are still received and clips still generate and
        /// cache while muted - only playback stops - so switching back is immediate.
        /// </summary>
        public void SetMuted(bool muted)
        {
            if (_muted == muted) return;
            _muted = muted;
            if (!muted) return;
            _player?.StopAll();
            _sprayBucket = -1;
        }

        public void Initialise(HapticDirector director, HapticPlayer player, Transform head,
            SurfaceFireManager fireManager, FoamSprayer foamSprayer, int cacheBuckets)
        {
            _director = director;
            _player = player;
            _head = head;
            _fireManager = fireManager;
            _foamSprayer = foamSprayer;
            _cacheBuckets = Mathf.Max(1, cacheBuckets);
            _cts = new CancellationTokenSource();

            _director.GenerationFailed += OnGenerationFailed;
            _fireManager.SpotIgnited += OnIgnited;
            _fireManager.SpotSpread += OnSpread;
            _fireManager.SpotExtinguished += OnExtinguished;
            _fireManager.AllExtinguished += OnAllExtinguished;
            _foamSprayer.Spraying += OnSpraying;
            _foamSprayer.SprayStopped += OnSprayStopped;
        }

        void Update()
        {
            if (!_muted) _player?.Tick();
        }

        void OnDestroy()
        {
            if (_director != null) _director.GenerationFailed -= OnGenerationFailed;
            if (_fireManager != null)
            {
                _fireManager.SpotIgnited -= OnIgnited;
                _fireManager.SpotSpread -= OnSpread;
                _fireManager.SpotExtinguished -= OnExtinguished;
                _fireManager.AllExtinguished -= OnAllExtinguished;
            }
            if (_foamSprayer != null)
            {
                _foamSprayer.Spraying -= OnSpraying;
                _foamSprayer.SprayStopped -= OnSprayStopped;
            }
            _player?.StopAll();
            _cts?.Cancel();
            _cts?.Dispose();
        }

        // --- Discrete fire cues -------------------------------------------------------------

        void OnIgnited(FireSpot spot) => RequestFireCue(HapticEventType.Ignite, spot, 1f);
        void OnSpread(FireSpot spot) => RequestFireCue(HapticEventType.Spread, spot, 0.8f);
        void OnExtinguished(FireSpot spot) => RequestFireCue(HapticEventType.Extinguish, spot, 0.7f);

        void RequestFireCue(HapticEventType type, FireSpot spot, float intensity)
        {
            var evt = new HapticEvent(type).WithNumber("intensity", intensity);
            if (spot != null)
            {
                if (_head != null)
                    evt.WithNumber("distance", Vector3.Distance(_head.position, spot.Position));
                string surface = SurfaceLabel(spot.Anchor);
                if (surface != null) evt.WithTag("surface", surface);
            }
            _director.Request(evt, PlayCue, upgradeWhenReady: false, ct: _cts.Token);
        }

        void OnAllExtinguished()
        {
            // The spray loop must stop before the celebration cue, or the loop would immediately
            // preempt it on the next Tick.
            _player.StopSustained();
            _sprayBucket = -1;
            _director.Request(HapticEvent.Custom(CompleteEventName).WithNumber("intensity", 1f),
                PlayCue, upgradeWhenReady: false, ct: _cts.Token);
        }

        void PlayCue(HapticClipData clip)
        {
            if (_muted) return;
            _player.PlayCue(clip);
            Record(clip, "cue");
        }

        // --- Sustained spray ----------------------------------------------------------------

        /// <summary>
        /// Called every frame the trigger is held. Rather than firing a request per frame (or on a
        /// timer, which is what used to truncate every clip to 100 ms), it only asks for a new clip
        /// when the trigger has moved into a different cache bucket - i.e. when the request would
        /// actually produce a different waveform. In between, the existing clip just keeps looping.
        /// </summary>
        void OnSpraying(float trigger)
        {
            if (_muted) return;
            int bucket = (int)(Mathf.Clamp01(trigger) * _cacheBuckets);
            if (bucket == _sprayBucket) return;
            _sprayBucket = bucket;

            var evt = new HapticEvent(HapticEventType.SprayTick).WithNumber("triggerDepth", trigger);
            // upgradeWhenReady: a spray loop can swap waveform mid-flight without a seam, so the
            // very first spray of a cold session starts on the fallback and silently becomes the
            // generated clip the moment the backend answers.
            _director.Request(evt, PlaySustained, upgradeWhenReady: true, ct: _cts.Token);
        }

        void PlaySustained(HapticClipData clip)
        {
            // A late upgrade can land after the trigger was released, or after a switch to the
            // static condition; either way, don't restart the buzz.
            if (_muted || !_foamSprayer.IsSpraying) return;
            _player.SetSustained(clip);
            Record(clip, "spray");
        }

        void OnSprayStopped()
        {
            _player.StopSustained();
            _sprayBucket = -1;
        }

        // --- Diagnostics ---------------------------------------------------------------------

        void Record(HapticClipData clip, string role)
        {
            if (clip == null) return;
            if (clip.IsFallback) FallbackCount++; else GeneratedCount++;

            string source = clip.IsFallback ? "procedural fallback" : clip.IsDegraded ? "on-device (backend failed)" : "generated";
            LastClipDescription = $"{role}: {clip.Key} [{source}] {clip.Amplitudes.Length} samples";
            Debug.Log($"[HapticAI] play {LastClipDescription} :: {clip.Prompt}");
        }

        void OnGenerationFailed(string key, System.Exception e) =>
            Debug.LogWarning($"[HapticAI] generation failed for {key}: {e.Message}");

        static string SurfaceLabel(MRUKAnchor anchor)
        {
            if (anchor == null) return null;
            foreach (var label in CandidateLabels)
                if (anchor.HasAnyLabel(label)) return label.ToString();
            return null;
        }
    }
}
