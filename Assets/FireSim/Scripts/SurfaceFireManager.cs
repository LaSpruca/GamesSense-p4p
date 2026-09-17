using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

namespace FireSim
{
    /// <summary>
    /// Owns the fire in the scene. MRUK hands it the room via <see cref="OnSceneReady"/>; the
    /// session then calls <see cref="Begin"/> to light the first fire, which spreads to nearby
    /// points on detected surfaces on a timer. Crucially it NEVER ignites a fresh, independent
    /// fire: every new patch grows out of an existing burning patch, so once the user extinguishes
    /// every patch the run is over and <see cref="AllExtinguished"/> fires.
    ///
    /// Ignition is deliberately NOT automatic on room load. The room can take several seconds to
    /// arrive and the haptics cache several more, and a fire that starts during that window burns
    /// unattended while the user is still reading the briefing. The session decides when to start.
    /// </summary>
    public class SurfaceFireManager : MonoBehaviour
    {
        [Header("Fire patch")]
        [Tooltip("Visual/collider radius of each burning patch, in metres.")]
        public float spotRadius = 0.14f;
        [Tooltip("How far the flame column rises off the surface. Foam damage is graded over this " +
                 "height, so it is also the size of the 'aim at the base' target.")]
        public float flameHeight = 0.5f;
        public float spotHealth = 100f;
        [Tooltip("Health removed by one foam particle striking the BASE of the flames. A well-aimed " +
                 "spray lands 100-180 particles/s on a patch, so this sets the kill time directly.")]
        public float foamDamagePerParticle = 0.45f;
        [Tooltip("Damage multiplier for foam hitting the top of the flames instead of the base.")]
        [Range(0f, 1f)] public float topOfFlameDamageScale = 0.15f;

        [Header("Recovery")]
        [Tooltip("Seconds without foam before a knocked-down patch starts climbing back.")]
        public float recoveryDelay = 1.5f;
        [Tooltip("Health per second regained once recovery starts. Must stay well below the damage " +
                 "rate of an accurate spray, or a patch becomes unkillable.")]
        public float recoveryRate = 14f;

        [Header("Spreading")]
        [Tooltip("Seconds of grace after ignition before fire can spread at all.")]
        public float firstSpreadDelay = 4f;
        [Tooltip("Seconds between spread attempts.")]
        public float spreadInterval = 1.75f;
        [Tooltip("Chance (0-1) that a given eligible patch spreads on each tick.")]
        [Range(0f, 1f)] public float spreadChance = 0.55f;
        [Tooltip("Hard cap on new patches per tick, so a large fire cannot snowball out of reach.")]
        public int maxSpreadsPerTick = 2;
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
        [Tooltip("Upper bound on simultaneous burning patches.")]
        public int maxSpots = 24;

        [Header("What can burn")]
        [Tooltip("Surfaces fire is allowed to live on. The initial fire prefers tables/storage, then the floor.")]
        public MRUKAnchor.SceneLabels flammableLabels =
            MRUKAnchor.SceneLabels.TABLE | MRUKAnchor.SceneLabels.FLOOR | MRUKAnchor.SceneLabels.COUCH |
            MRUKAnchor.SceneLabels.STORAGE | MRUKAnchor.SceneLabels.BED | MRUKAnchor.SceneLabels.OTHER |
            MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.SCREEN | MRUKAnchor.SceneLabels.WALL_ART;

        readonly List<FireSpot> _spots = new List<FireSpot>();
        readonly List<MRUKAnchor> _flammableAnchors = new List<MRUKAnchor>();
        MRUKRoom _room;
        Transform _fireRoot;
        Coroutine _spreadLoop;

        public int BurningCount => _spots.Count;
        /// <summary>True once MRUK has handed over a room with at least one flammable surface.</summary>
        public bool RoomReady { get; private set; }
        /// <summary>True between <see cref="Begin"/> and the last patch going out.</summary>
        public bool RunActive { get; private set; }
        /// <summary>Patches lit this run, including the first one.</summary>
        public int TotalIgnited { get; private set; }
        /// <summary>Patches the user put out this run.</summary>
        public int TotalExtinguished { get; private set; }
        /// <summary>The most patches burning at once this run - a rough "how close did it get" score.</summary>
        public int PeakBurning { get; private set; }

        /// <summary>Raised once MRUK has produced a usable room. The session waits on this.</summary>
        public event System.Action RoomBecameReady;
        /// <summary>Raised for the very first fire of a run.</summary>
        public event System.Action<FireSpot> SpotIgnited;
        /// <summary>Raised each time fire spreads to a new patch.</summary>
        public event System.Action<FireSpot> SpotSpread;
        /// <summary>Raised as a patch is extinguished.</summary>
        public event System.Action<FireSpot> SpotExtinguished;
        /// <summary>Raised when the last burning patch goes out - i.e. the user has finished.</summary>
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
            RoomReady = true;
            RoomBecameReady?.Invoke();
        }

        /// <summary>
        /// Light the first fire and start the spread loop. Safe to call again after a run finishes;
        /// that is what Restart does.
        /// </summary>
        public bool Begin()
        {
            if (!RoomReady)
            {
                Debug.LogWarning("[FireSim] Begin() called before the room was ready.");
                return false;
            }
            if (RunActive) return true;

            ClearAllSpots();
            TotalIgnited = 0;
            TotalExtinguished = 0;
            PeakBurning = 0;

            if (!IgniteInitialFire())
            {
                Debug.LogWarning("[FireSim] Could not find a flammable surface to ignite.");
                return false;
            }

            RunActive = true;
            _spreadLoop = StartCoroutine(SpreadLoop());
            return true;
        }

        /// <summary>Stop the run and remove every burning patch without raising AllExtinguished.</summary>
        public void Abort()
        {
            RunActive = false;
            if (_spreadLoop != null) { StopCoroutine(_spreadLoop); _spreadLoop = null; }
            ClearAllSpots();
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
            foreach (var anchor in _room.Anchors)
            {
                if (anchor == null) continue;
                if (!anchor.HasAnyLabel(flammableLabels)) continue;
                if (!anchor.PlaneRect.HasValue && !anchor.VolumeBounds.HasValue) continue;
                _flammableAnchors.Add(anchor);
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

        IEnumerator SpreadLoop()
        {
            yield return new WaitForSeconds(firstSpreadDelay);

            var wait = new WaitForSeconds(spreadInterval);
            while (RunActive)
            {
                yield return wait;
                if (_spots.Count == 0 || _spots.Count >= maxSpots) continue;

                // Snapshot the count so we don't iterate over patches added by this same tick.
                int existing = _spots.Count;
                int spreadsThisTick = 0;
                for (int i = 0; i < existing && _spots.Count < maxSpots; i++)
                {
                    if (spreadsThisTick >= maxSpreadsPerTick) break;

                    var spot = _spots[i];
                    if (spot == null || !spot.IsBurning) continue;
                    // A patch you have knocked down is too weak to throw off new fire. This is the
                    // main reason committing to one patch at a time beats spraying everything.
                    if (spot.HealthFraction < spreadHealthThreshold) continue;
                    if (Random.value > spreadChance) continue;

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
            if (IsTooCloseToExistingSpot(surfacePos)) return false;

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

        bool IsTooCloseToExistingSpot(Vector3 point)
        {
            foreach (var s in _spots)
            {
                if (s == null) continue;
                if (Vector3.Distance(s.Position, point) < minSpotSpacing) return true;
            }
            return false;
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
                TopOfFlameDamageScale = topOfFlameDamageScale,
                RecoveryDelay = recoveryDelay,
                RecoveryRate = recoveryRate,
            });
            _spots.Add(spot);
            TotalIgnited++;
            if (_spots.Count > PeakBurning) PeakBurning = _spots.Count;
            return spot;
        }

        public void OnSpotExtinguished(FireSpot spot)
        {
            SpotExtinguished?.Invoke(spot);
            _spots.Remove(spot);
            TotalExtinguished++;

            if (_spots.Count != 0 || !RunActive) return;

            RunActive = false;
            if (_spreadLoop != null) { StopCoroutine(_spreadLoop); _spreadLoop = null; }
            Debug.Log($"[FireSim] All fire extinguished after {TotalExtinguished} patch(es).");
            AllExtinguished?.Invoke();
        }
    }
}
