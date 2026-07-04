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
    /// (ignite/spread/extinguish) and the foam sprayer, packages each as a <see cref="HapticEvent"/>
    /// carrying the environmental cues available at that instant, and hands it to the
    /// <see cref="HapticDirector"/>. When the director produces a clip it is played on the
    /// controller. This is the seam where "what happened in the world" becomes "what to generate".
    /// </summary>
    public sealed class FireHapticRelay : MonoBehaviour
    {
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
        float _spraySampleInterval;
        float _nextSprayTime;
        CancellationTokenSource _cts;

        public void Initialise(HapticDirector director, HapticPlayer player, Transform head,
            SurfaceFireManager fireManager, FoamSprayer foamSprayer, float spraySampleInterval = 0.1f)
        {
            _director = director;
            _player = player;
            _head = head;
            _fireManager = fireManager;
            _foamSprayer = foamSprayer;
            _spraySampleInterval = spraySampleInterval;
            _cts = new CancellationTokenSource();

            _director.ClipReady += OnClipReady;
            _director.GenerationFailed += OnGenerationFailed;
            _fireManager.SpotIgnited += OnIgnited;
            _fireManager.SpotSpread += OnSpread;
            _fireManager.SpotExtinguished += OnExtinguished;
            _foamSprayer.Spraying += OnSpraying;
        }

        void OnDestroy()
        {
            if (_director != null)
            {
                _director.ClipReady -= OnClipReady;
                _director.GenerationFailed -= OnGenerationFailed;
            }
            if (_fireManager != null)
            {
                _fireManager.SpotIgnited -= OnIgnited;
                _fireManager.SpotSpread -= OnSpread;
                _fireManager.SpotExtinguished -= OnExtinguished;
            }
            if (_foamSprayer != null) _foamSprayer.Spraying -= OnSpraying;
            _cts?.Cancel();
            _cts?.Dispose();
        }

        void OnClipReady(HapticClipData clip)
        {
            _player.Play(clip);
            Debug.Log($"[HapticAI] play {clip.Key} ({clip.Amplitudes.Length} samples" +
                      $"{(clip.IsFallback ? ", fallback" : "")}) :: {clip.Prompt}");
        }

        void OnGenerationFailed(string key, System.Exception e) =>
            Debug.LogWarning($"[HapticAI] generation failed for {key}: {e.Message}");

        void OnIgnited(FireSpot spot) => RequestFire(HapticEventType.Ignite, spot, 1f);
        void OnSpread(FireSpot spot) => RequestFire(HapticEventType.Spread, spot, 0.8f);
        void OnExtinguished(FireSpot spot) => RequestFire(HapticEventType.Extinguish, spot, 0.7f);

        void RequestFire(HapticEventType type, FireSpot spot, float intensity)
        {
            var evt = new HapticEvent(type).WithNumber("intensity", intensity);
            if (spot != null)
            {
                if (_head != null)
                    evt.WithNumber("distance", Vector3.Distance(_head.position, spot.Position));
                string surface = SurfaceLabel(spot.Anchor);
                if (surface != null) evt.WithTag("surface", surface);
            }
            _director.Request(evt, _cts.Token);
        }

        void OnSpraying(float trigger)
        {
            if (Time.time < _nextSprayTime) return;
            _nextSprayTime = Time.time + _spraySampleInterval;
            var evt = new HapticEvent(HapticEventType.SprayTick).WithNumber("triggerDepth", trigger);
            _director.Request(evt, _cts.Token);
        }

        static string SurfaceLabel(MRUKAnchor anchor)
        {
            if (anchor == null) return null;
            foreach (var label in CandidateLabels)
                if (anchor.HasAnyLabel(label)) return label.ToString();
            return null;
        }
    }
}
