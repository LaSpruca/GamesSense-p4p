using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

namespace FireSim
{
    /// <summary>
    /// Per-frame picture of what the spray is doing, for the haptics pipeline to stream to its
    /// server. Everything here is derived from foam hits the patches report and the nozzle's
    /// motion; nothing is sampled from the particle system directly.
    /// </summary>
    public struct SprayTelemetry
    {
        /// <summary>From the sprayer: trigger held past its threshold.</summary>
        public bool Spraying;
        /// <summary>From the sprayer: trigger pull 0..1 (0 when not spraying).</summary>
        public float Trigger;
        /// <summary>Foam particles/second currently landing on burning OR smouldering patches (smoothed over ~0.25 s).</summary>
        public float HitRate;
        /// <summary>Mean quality of those hits, 0..1 (1 = base), smoothed; 0 when none.</summary>
        public float HitQuality;
        /// <summary>Most recent hits were on a smouldering patch.</summary>
        public bool Soaking;
        /// <summary>
        /// Nozzle lateral speed > 0.35 m/s AND HitRate > 15 AND (Coverage of the targeted spot
        /// >= 0.66 when the stage has more than one sweep sector).
        /// </summary>
        public bool Sweeping;
        /// <summary>Nozzle lateral speed, m/s (component of nozzle velocity perpendicular to its forward axis), smoothed.</summary>
        public float SweepSpeed;
        /// <summary>Coverage of the spot most recently hit (0..1).</summary>
        public float Coverage;
        /// <summary>Distance from nozzle to nearest burning/smouldering spot; 99 if none.</summary>
        public float NearestFireMetres;
        /// <summary>Angle between nozzle forward and the direction to the nearest burning/smouldering spot; 0 if none.</summary>
        public float AimErrorDegrees;
        public int FiresBurning;
        public int FiresSmouldering;
        public int StageIndex;
    }

    /// <summary>
    /// Owns the fire in the scene. MRUK hands it the room via <see cref="OnSceneReady"/>; the
    /// session then calls <see cref="Begin(TrainingStage)"/> with the stage to run, which lights the
    /// stage's initial fire(s) and, if the stage allows it, spreads to nearby points on detected
    /// surfaces on a timer. Crucially it NEVER ignites a fresh, independent fire after that: every
    /// new patch grows out of an existing burning patch, so once the user has put out every patch
    /// (and none is left smouldering) the run is over and <see cref="AllExtinguished"/> fires.
    ///
    /// Ignition is deliberately NOT automatic on room load. The room can take several seconds to
    /// arrive and the haptics cache several more, and a fire that starts during that window burns
    /// unattended while the user is still reading the briefing. The session decides when to start.
    ///
    /// Everything that makes a stage easier or harder (spread, aim tolerance, sweep sectors,
    /// re-ignition, recovery) lives on the <see cref="TrainingStage"/>, not in the inspector, so
    /// the five stages are the single source of truth for difficulty.
    /// </summary>
    public class SurfaceFireManager : MonoBehaviour
    {
        /// <summary>Foam hits at or above this quality count as "base" hits for scoring.</summary>
        const float BaseHitQuality = 0.6f;
        /// <summary>Time constant for the exponentially smoothed telemetry rates.</summary>
        const float TelemetrySmoothing = 0.25f;
        /// <summary>Minimum separation between the stage's initial fires.</summary>
        const float InitialFireSpacing = 0.6f;
        const float NoFireDistance = 99f;

        [Header("Fire patch")]
        [Tooltip("Visual radius of each burning patch, in metres. The stage scales the collider on top of this.")]
        public float spotRadius = 0.14f;
        [Tooltip("How far the flame column rises off the surface. Foam damage is graded over this " +
                 "height, so it is also the size of the 'aim at the base' target.")]
        public float flameHeight = 0.5f;
        public float spotHealth = 100f;
        [Tooltip("Health removed by one foam particle striking the BASE of the flames. A well-aimed " +
                 "spray lands 100-180 particles/s on a patch, so this sets the kill time directly.")]
        public float foamDamagePerParticle = 0.45f;

        [Header("Spreading (geometry - timing and chance come from the stage)")]
        [Tooltip("A patch below this health fraction is too weak to spread. Knocking a patch down " +
                 "therefore slows the fire even before it goes out.")]
        [Range(0f, 1f)] public float spreadHealthThreshold = 0.5f;
        [Tooltip("How far a new patch appears from its parent, in metres.")]
        public float spreadDistanceMin = 0.22f;
        public float spreadDistanceMax = 0.45f;
        [Tooltip("A spread candidate must be within this distance of a detected surface to take hold.")]
        public float surfaceSnapDistance = 0.25f;
        [Tooltip("Minimum spacing between patches so they don't stack up.")]
        public float minSpotSpacing = 0.28f;

        [Header("What can burn")]
        [Tooltip("Surfaces fire is allowed to live on. The initial fire prefers tables/storage, then the floor.")]
        public MRUKAnchor.SceneLabels flammableLabels =
            MRUKAnchor.SceneLabels.TABLE | MRUKAnchor.SceneLabels.FLOOR | MRUKAnchor.SceneLabels.COUCH |
            MRUKAnchor.SceneLabels.STORAGE | MRUKAnchor.SceneLabels.BED | MRUKAnchor.SceneLabels.OTHER |
            MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.SCREEN | MRUKAnchor.SceneLabels.WALL_ART;

        readonly List<FireSpot> _spots = new List<FireSpot>();
        readonly List<MRUKAnchor> _flammableAnchors = new List<MRUKAnchor>();
        readonly List<string> _roomSurfaceLabels = new List<string>();
        MRUKRoom _room;
        Transform _fireRoot;
        Coroutine _spreadLoop;

        // Telemetry inputs, accumulated between LateUpdates.
        FoamSprayer _sprayer;
        float _trigger;
        int _frameHits;
        float _frameQualitySum;
        FireSpot _lastHitSpot;
        bool _lastHitSmouldering;
        float _hitRate;
        float _hitQuality;
        bool _soaking;
        float _sweepSpeed;
        Vector3 _prevNozzlePos;
        bool _havePrevNozzlePos;

        /// <summary>The stage the current (or most recent) run was started with. Null before the first Begin.</summary>
        public TrainingStage Stage { get; private set; }

        /// <summary>Patches with flames up right now.</summary>
        public int BurningCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _spots.Count; i++)
                    if (_spots[i] != null && _spots[i].IsBurning) n++;
                return n;
            }
        }
        /// <summary>Patches knocked down but not yet out - they can still flare back.</summary>
        public int SmoulderingCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _spots.Count; i++)
                    if (_spots[i] != null && _spots[i].IsSmouldering) n++;
                return n;
            }
        }
        /// <summary>True once MRUK has handed over a room with at least one flammable surface.</summary>
        public bool RoomReady { get; private set; }
        /// <summary>True between <see cref="Begin(TrainingStage)"/> and AllExtinguished/Abort.</summary>
        public bool RunActive { get; private set; }
        /// <summary>Patches lit this run, including the initial one(s). Re-ignitions are not counted here.</summary>
        public int TotalIgnited { get; private set; }
        /// <summary>Flames-out events this run. A patch that re-ignites and is knocked down again counts twice.</summary>
        public int TotalExtinguished { get; private set; }
        /// <summary>Smouldering patches that flared back this run - the "walked away too soon" score.</summary>
        public int TotalReignited { get; private set; }
        /// <summary>The most patches burning at once this run - a rough "how close did it get" score.</summary>
        public int PeakBurning { get; private set; }
        /// <summary>Foam particles that hit a burning (not smouldering) patch this run.</summary>
        public int TotalFoamHits { get; private set; }
        /// <summary>Of <see cref="TotalFoamHits"/>, those with hit quality >= 0.6, i.e. on or near the base.</summary>
        public int BaseHits { get; private set; }
        /// <summary>BaseHits / TotalFoamHits; 0 when nothing has hit yet.</summary>
        public float BaseHitFraction => TotalFoamHits > 0 ? (float)BaseHits / TotalFoamHits : 0f;

        /// <summary>Distinct MRUK label names of the flammable anchors, e.g. "TABLE", "FLOOR".</summary>
        public IReadOnlyList<string> RoomSurfaceLabels => _roomSurfaceLabels;
        /// <summary>Total anchors MRUK found in the room (flammable or not).</summary>
        public int RoomAnchorCount { get; private set; }
        /// <summary>Size of the room's bounding box; Vector3.zero before the room is ready.</summary>
        public Vector3 RoomSize { get; private set; }

        /// <summary>Recomputed in LateUpdate every frame, whether or not a run is active.</summary>
        public SprayTelemetry Telemetry { get; private set; }

        /// <summary>Raised once MRUK has produced a usable room. The session waits on this.</summary>
        public event System.Action RoomBecameReady;
        /// <summary>Raised for each initial fire of a run (one per stage.InitialFires).</summary>
        public event System.Action<FireSpot> SpotIgnited;
        /// <summary>Raised each time fire spreads to a new patch.</summary>
        public event System.Action<FireSpot> SpotSpread;
        /// <summary>Raised the moment a patch's flames go out (it may still smoulder afterwards).</summary>
        public event System.Action<FireSpot> SpotExtinguished;
        /// <summary>Raised when a knocked-down patch enters its smouldering state.</summary>
        public event System.Action<FireSpot> SpotSmouldering;
        /// <summary>Raised when a smouldering patch flares back into a burning one.</summary>
        public event System.Action<FireSpot> SpotReignited;
        /// <summary>Raised when a patch is out for good and about to be removed.</summary>
        public event System.Action<FireSpot> SpotOut;
        /// <summary>
        /// Raised when no patch is burning OR smouldering - i.e. the stage is complete. Never fires
        /// while an ember could still flare back.
        /// </summary>
        public event System.Action AllExtinguished;

        void Awake()
        {
            _fireRoot = new GameObject("Fire").transform;
            _fireRoot.SetParent(transform, false);
        }

        /// <summary>Wired to MRUK's SceneLoadedEvent by the bootstrap. Does not start the fire.</summary>
        public void OnSceneReady()
        {
            _room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
            if (_room == null)
            {
                Debug.LogWarning("[FireSim] No MRUK room available - cannot start the fire. " +
                                 "Make sure Space Setup has been run on the headset.");
                return;
            }

            CollectFlammableAnchors();
            RoomSize = _room.GetRoomBounds().size;
            RoomReady = true;
            RoomBecameReady?.Invoke();
        }

        /// <summary>
        /// The sprayer whose nozzle and trigger feed <see cref="Telemetry"/>. The bootstrap calls
        /// this once both objects exist; the manager does not go looking for a sprayer itself.
        /// </summary>
        public void AttachSprayer(FoamSprayer sprayer)
        {
            if (_sprayer != null) _sprayer.Spraying -= OnSprayerSpraying;
            _sprayer = sprayer;
            _havePrevNozzlePos = false;
            _sweepSpeed = 0f;
            if (_sprayer != null) _sprayer.Spraying += OnSprayerSpraying;
        }

        void OnDestroy()
        {
            if (_sprayer != null) _sprayer.Spraying -= OnSprayerSpraying;
        }

        void OnSprayerSpraying(float trigger) => _trigger = trigger;

        /// <summary>Convenience overload: runs the first stage. Prefer <see cref="Begin(TrainingStage)"/>.</summary>
        public bool Begin() => Begin(TrainingStages.Get(1));

        /// <summary>
        /// Apply the stage, light its initial fire(s) and start the spread loop if the stage has
        /// one. Safe to call again after a run finishes; that is what Restart / Next stage do.
        /// </summary>
        public bool Begin(TrainingStage stage)
        {
            if (!RoomReady)
            {
                Debug.LogWarning("[FireSim] Begin() called before the room was ready.");
                return false;
            }
            if (RunActive) return true;
            if (stage == null)
            {
                Debug.LogWarning("[FireSim] Begin() called with no stage - using stage 1.");
                stage = TrainingStages.Get(1);
            }

            ClearAllSpots();
            ResetRunState();
            Stage = stage;

            if (!IgniteInitialFire())
            {
                Debug.LogWarning("[FireSim] Could not find a flammable surface to ignite.");
                return false;
            }
            for (int i = 1; i < stage.InitialFires; i++)
            {
                if (!IgniteExtraFire())
                    Debug.LogWarning($"[FireSim] Could not place initial fire {i + 1} of {stage.InitialFires}.");
            }

            RunActive = true;
            if (stage.SpreadEnabled) _spreadLoop = StartCoroutine(SpreadLoop());
            return true;
        }

        /// <summary>Stop the run and remove every patch without raising AllExtinguished.</summary>
        public void Abort()
        {
            RunActive = false;
            if (_spreadLoop != null) { StopCoroutine(_spreadLoop); _spreadLoop = null; }
            ClearAllSpots();
            _lastHitSpot = null;
            _frameHits = 0;
            _frameQualitySum = 0f;
        }

        void ResetRunState()
        {
            TotalIgnited = 0;
            TotalExtinguished = 0;
            TotalReignited = 0;
            PeakBurning = 0;
            TotalFoamHits = 0;
            BaseHits = 0;
            _frameHits = 0;
            _frameQualitySum = 0f;
            _lastHitSpot = null;
            _lastHitSmouldering = false;
            _hitRate = 0f;
            _hitQuality = 0f;
            _soaking = false;
        }

        void ClearAllSpots()
        {
            foreach (var spot in _spots)
                if (spot != null) Destroy(spot.gameObject);
            _spots.Clear();
        }

        void CollectFlammableAnchors()
        {
            _flammableAnchors.Clear();
            _roomSurfaceLabels.Clear();
            RoomAnchorCount = 0;
            foreach (var anchor in _room.Anchors)
            {
                if (anchor == null) continue;
                RoomAnchorCount++;
                if (!anchor.HasAnyLabel(flammableLabels)) continue;
                if (!anchor.PlaneRect.HasValue && !anchor.VolumeBounds.HasValue) continue;
                _flammableAnchors.Add(anchor);

                string label = anchor.Label.ToString();
                if (!_roomSurfaceLabels.Contains(label)) _roomSurfaceLabels.Add(label);
            }
        }

        bool IgniteInitialFire()
        {
            // Prefer an upward-facing surface a person would set something alight on (table/desk/etc).
            var preferred = MRUKAnchor.SceneLabels.TABLE | MRUKAnchor.SceneLabels.STORAGE |
                            MRUKAnchor.SceneLabels.COUCH | MRUKAnchor.SceneLabels.BED;

            if (_room.GenerateRandomPositionOnSurface(MRUK.SurfaceType.FACING_UP, spotRadius,
                    new LabelFilter(preferred), out var pos, out var normal) ||
                _room.GenerateRandomPositionOnSurface(MRUK.SurfaceType.FACING_UP, spotRadius,
                    new LabelFilter(MRUKAnchor.SceneLabels.FLOOR), out pos, out normal))
            {
                SpotIgnited?.Invoke(SpawnSpot(pos, normal, FindAnchorAt(pos)));
                return true;
            }

            // Fall back to the centre of any flammable anchor we found.
            if (_flammableAnchors.Count > 0)
            {
                var anchor = _flammableAnchors[0];
                Vector3 center = anchor.GetAnchorCenter();
                anchor.GetClosestSurfacePosition(center, out var surfacePos, out var surfaceNormal);
                SpotIgnited?.Invoke(SpawnSpot(surfacePos, surfaceNormal, anchor));
                return true;
            }

            return false;
        }

        /// <summary>
        /// A second/third starting fire on any flammable surface, ideally well away from the ones
        /// already lit so the user has to choose which to fight first. Falls back to whatever it
        /// can find rather than silently running the stage with fewer fires than briefed.
        /// </summary>
        bool IgniteExtraFire()
        {
            const int attempts = 12;
            bool haveFallback = false;
            Vector3 fallbackPos = Vector3.zero, fallbackNormal = Vector3.up;

            for (int i = 0; i < attempts; i++)
            {
                if (!_room.GenerateRandomPositionOnSurface(MRUK.SurfaceType.FACING_UP, spotRadius,
                        new LabelFilter(flammableLabels), out var pos, out var normal))
                    break;
                if (!haveFallback) { haveFallback = true; fallbackPos = pos; fallbackNormal = normal; }
                if (DistanceToNearestSpot(pos) < InitialFireSpacing) continue;

                SpotIgnited?.Invoke(SpawnSpot(pos, normal, FindAnchorAt(pos)));
                return true;
            }

            // No random candidate was far enough away; try anchor centres, then take anything.
            foreach (var anchor in _flammableAnchors)
            {
                if (anchor == null) continue;
                anchor.GetClosestSurfacePosition(anchor.GetAnchorCenter(), out var p, out var n);
                if (DistanceToNearestSpot(p) < InitialFireSpacing) continue;
                SpotIgnited?.Invoke(SpawnSpot(p, n, anchor));
                return true;
            }

            if (haveFallback)
            {
                SpotIgnited?.Invoke(SpawnSpot(fallbackPos, fallbackNormal, FindAnchorAt(fallbackPos)));
                return true;
            }
            return false;
        }

        IEnumerator SpreadLoop()
        {
            yield return new WaitForSeconds(Stage.FirstSpreadDelay);

            var wait = new WaitForSeconds(Stage.SpreadInterval);
            while (RunActive)
            {
                yield return wait;
                if (_spots.Count == 0 || _spots.Count >= Stage.MaxSpots) continue;

                // Snapshot the count so we don't iterate over patches added by this same tick.
                int existing = _spots.Count;
                int spreadsThisTick = 0;
                for (int i = 0; i < existing && _spots.Count < Stage.MaxSpots; i++)
                {
                    if (spreadsThisTick >= Stage.MaxSpreadsPerTick) break;

                    var spot = _spots[i];
                    // Smouldering patches don't spread - only live flames throw off new fire.
                    if (spot == null || !spot.IsBurning) continue;
                    // A patch you have knocked down is too weak to throw off new fire. This is the
                    // main reason committing to one patch at a time beats spraying everything.
                    if (spot.HealthFraction < spreadHealthThreshold) continue;
                    if (Random.value > Stage.SpreadChance) continue;

                    if (TrySpreadFrom(spot)) spreadsThisTick++;
                }
            }
        }

        bool TrySpreadFrom(FireSpot spot)
        {
            // Pick a random direction lying in the spot's surface plane.
            Vector3 normal = spot.SurfaceNormal;
            Vector3 tangent = Vector3.Cross(normal, Vector3.up);
            if (tangent.sqrMagnitude < 1e-4f) tangent = Vector3.Cross(normal, Vector3.right);
            tangent.Normalize();
            Vector3 bitangent = Vector3.Cross(normal, tangent);

            float angle = Random.value * Mathf.PI * 2f;
            Vector3 dir = (tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle)).normalized;
            float dist = Random.Range(spreadDistanceMin, spreadDistanceMax);
            Vector3 candidate = spot.Position + dir * dist;

            // Snap the candidate onto the nearest detected surface; reject if nothing is close.
            if (!FindNearestSurfacePoint(candidate, out var surfacePos, out var surfaceNormal, out var anchor))
                return false;
            if (DistanceToNearestSpot(surfacePos) < minSpotSpacing) return false;

            SpotSpread?.Invoke(SpawnSpot(surfacePos, surfaceNormal, anchor));
            return true;
        }

        bool FindNearestSurfacePoint(Vector3 worldPoint, out Vector3 closest, out Vector3 normal, out MRUKAnchor anchor)
        {
            closest = Vector3.zero;
            normal = Vector3.up;
            anchor = null;
            float best = surfaceSnapDistance;

            foreach (var a in _flammableAnchors)
            {
                if (a == null) continue;
                float d = a.GetClosestSurfacePosition(worldPoint, out var p, out var n);
                if (d < best)
                {
                    best = d;
                    closest = p;
                    normal = n;
                    anchor = a;
                }
            }
            return anchor != null;
        }

        MRUKAnchor FindAnchorAt(Vector3 worldPoint)
        {
            FindNearestSurfacePoint(worldPoint, out _, out _, out var anchor);
            return anchor;
        }

        float DistanceToNearestSpot(Vector3 point)
        {
            float best = float.MaxValue;
            foreach (var s in _spots)
            {
                if (s == null) continue;
                float d = Vector3.Distance(s.Position, point);
                if (d < best) best = d;
            }
            return best;
        }

        FireSpot SpawnSpot(Vector3 position, Vector3 normal, MRUKAnchor anchor)
        {
            var go = new GameObject("FireSpot");
            go.transform.SetParent(_fireRoot, false);
            var spot = go.AddComponent<FireSpot>();
            spot.Initialise(this, position, normal, anchor, new FireSpotSettings
            {
                Radius = spotRadius,
                FlameHeight = flameHeight,
                Health = spotHealth,
                FoamDamagePerParticle = foamDamagePerParticle,
                TopOfFlameDamageScale = Stage.TopOfFlameDamageScale,
                RecoveryDelay = Stage.RecoveryDelay,
                RecoveryRate = Stage.RecoveryRate,
                HitRadiusScale = Stage.HitRadiusScale,
                BaseBandFraction = Stage.BaseBandFraction,
                SweepSectors = Stage.SweepSectors,
                ReigniteEnabled = Stage.ReigniteEnabled,
                SoakSeconds = Stage.SoakSeconds,
                ReigniteDelay = Stage.ReigniteDelay,
                ReigniteChance = Stage.ReigniteChance,
                ReigniteHealthFraction = Stage.ReigniteHealthFraction,
            });
            _spots.Add(spot);
            TotalIgnited++;
            TrackPeak();
            return spot;
        }

        void TrackPeak()
        {
            int burning = BurningCount;
            if (burning > PeakBurning) PeakBurning = burning;
        }

        // ---- Callbacks from FireSpot ------------------------------------------------------------

        /// <summary>
        /// Called by a patch every frame foam lands on it (from OnParticleCollision, which Unity
        /// runs before LateUpdate). Accumulated here and folded into <see cref="Telemetry"/> once
        /// per frame. Scoring counters only count hits on burning patches: foam on embers is
        /// soaking, not aim.
        /// </summary>
        public void ReportFoamHit(FireSpot spot, int particles, float meanQuality, bool smouldering)
        {
            if (particles <= 0) return;
            _frameHits += particles;
            _frameQualitySum += meanQuality * particles;
            _lastHitSpot = spot;
            _lastHitSmouldering = smouldering;

            if (smouldering) return;
            TotalFoamHits += particles;
            if (meanQuality >= BaseHitQuality) BaseHits += particles;
        }

        /// <summary>The flames-out moment. The patch may go on to smoulder; it is not removed here.</summary>
        public void OnSpotExtinguished(FireSpot spot)
        {
            TotalExtinguished++;
            SpotExtinguished?.Invoke(spot);
        }

        public void OnSpotSmouldering(FireSpot spot)
        {
            SpotSmouldering?.Invoke(spot);
        }

        public void OnSpotReignited(FireSpot spot)
        {
            TotalReignited++;
            TrackPeak();
            SpotReignited?.Invoke(spot);
        }

        /// <summary>The patch is gone for good. This is the only place a run can complete.</summary>
        public void OnSpotOut(FireSpot spot)
        {
            SpotOut?.Invoke(spot);
            _spots.Remove(spot);
            if (_lastHitSpot == spot) _lastHitSpot = null;

            if (!RunActive) return;
            for (int i = 0; i < _spots.Count; i++)
            {
                var s = _spots[i];
                if (s != null && (s.IsBurning || s.IsSmouldering)) return;
            }

            RunActive = false;
            if (_spreadLoop != null) { StopCoroutine(_spreadLoop); _spreadLoop = null; }
            Debug.Log($"[FireSim] Stage {(Stage != null ? Stage.Index : 0)} complete: " +
                      $"{TotalExtinguished} knock-down(s), {TotalReignited} re-ignition(s).");
            AllExtinguished?.Invoke();
        }

        // ---- Telemetry ---------------------------------------------------------------------------

        /// <summary>
        /// Folds this frame's foam hits and nozzle motion into the smoothed telemetry. Runs after
        /// every OnParticleCollision for the frame, so the per-frame accumulators are complete.
        /// </summary>
        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            // Exponential smoothing with a fixed time constant, so the result is frame-rate independent.
            float alpha = 1f - Mathf.Exp(-dt / TelemetrySmoothing);

            float instantRate = _frameHits / dt;
            _hitRate = Mathf.Lerp(_hitRate, instantRate, alpha);
            if (_frameHits > 0)
            {
                _hitQuality = Mathf.Lerp(_hitQuality, _frameQualitySum / _frameHits, alpha);
                _soaking = _lastHitSmouldering;
            }
            else
            {
                _hitQuality = Mathf.Lerp(_hitQuality, 0f, alpha);
                if (_hitRate < 1f) { _hitRate = 0f; _hitQuality = 0f; _soaking = false; }
            }
            _frameHits = 0;
            _frameQualitySum = 0f;

            bool spraying = _sprayer != null && _sprayer.IsSpraying;
            if (!spraying) _trigger = 0f;

            float nearest = NoFireDistance;
            float aimError = 0f;
            if (_sprayer != null)
            {
                Transform nozzle = _sprayer.Nozzle;
                Vector3 nozzlePos = nozzle.position;
                if (_havePrevNozzlePos)
                {
                    Vector3 velocity = (nozzlePos - _prevNozzlePos) / dt;
                    Vector3 forward = nozzle.forward;
                    Vector3 lateral = velocity - forward * Vector3.Dot(velocity, forward);
                    _sweepSpeed = Mathf.Lerp(_sweepSpeed, lateral.magnitude, alpha);
                }
                _prevNozzlePos = nozzlePos;
                _havePrevNozzlePos = true;

                FireSpot target = NearestLiveSpot(nozzlePos, out nearest);
                if (target != null)
                    aimError = Vector3.Angle(nozzle.forward, target.Position - nozzlePos);
                else
                    nearest = NoFireDistance;
            }

            float coverage = 0f;
            if (_lastHitSpot != null && (_lastHitSpot.IsBurning || _lastHitSpot.IsSmouldering))
                coverage = _lastHitSpot.Coverage;

            bool needsCoverage = Stage != null && Stage.SweepSectors > 1;
            bool sweeping = _sweepSpeed > 0.35f && _hitRate > 15f && (!needsCoverage || coverage >= 0.66f);

            Telemetry = new SprayTelemetry
            {
                Spraying = spraying,
                Trigger = _trigger,
                HitRate = _hitRate,
                HitQuality = _hitQuality,
                Soaking = _soaking,
                Sweeping = sweeping,
                SweepSpeed = _sweepSpeed,
                Coverage = coverage,
                NearestFireMetres = nearest,
                AimErrorDegrees = aimError,
                FiresBurning = BurningCount,
                FiresSmouldering = SmoulderingCount,
                StageIndex = Stage != null ? Stage.Index : 0,
            };
        }

        /// <summary>Nearest patch that is still burning or smouldering, or null with distance 99.</summary>
        FireSpot NearestLiveSpot(Vector3 from, out float distance)
        {
            FireSpot best = null;
            distance = NoFireDistance;
            for (int i = 0; i < _spots.Count; i++)
            {
                var s = _spots[i];
                if (s == null || (!s.IsBurning && !s.IsSmouldering)) continue;
                float d = Vector3.Distance(from, s.Position);
                if (d < distance) { distance = d; best = s; }
            }
            return best;
        }
    }
}
