using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

namespace FireSim
{
    /// <summary>
    /// Owns the fire in the scene. When MRUK finishes loading the room it ignites a single
    /// initial fire on a detected surface, then on a timer spreads existing fire to nearby
    /// points on detected surfaces. Crucially it NEVER ignites a fresh, independent fire:
    /// every new patch grows out of an existing burning patch, so once the user extinguishes
    /// every patch the simulation is over.
    /// </summary>
    public class SurfaceFireManager : MonoBehaviour
    {
        [Header("Fire patch")]
        [Tooltip("Visual/collider radius of each burning patch, in metres.")]
        public float spotRadius = 0.14f;
        public float spotHealth = 100f;
        [Tooltip("Health removed per foam particle that strikes a patch.")]
        public float foamDamagePerParticle = 1.6f;

        [Header("Spreading")]
        [Tooltip("Seconds between spread attempts.")]
        public float spreadInterval = 2.5f;
        [Tooltip("Chance (0-1) that a given burning patch spreads on each tick.")]
        [Range(0f, 1f)] public float spreadChance = 0.5f;
        [Tooltip("How far a new patch appears from its parent, in metres.")]
        public float spreadDistanceMin = 0.22f;
        public float spreadDistanceMax = 0.45f;
        [Tooltip("A spread candidate must be within this distance of a detected surface to take hold.")]
        public float surfaceSnapDistance = 0.25f;
        [Tooltip("Minimum spacing between patches so they don't stack up.")]
        public float minSpotSpacing = 0.28f;
        [Tooltip("Upper bound on simultaneous burning patches.")]
        public int maxSpots = 40;

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

        public int BurningCount => _spots.Count;

        /// <summary>Raised for the very first fire of the scene.</summary>
        public event System.Action<FireSpot> SpotIgnited;
        /// <summary>Raised each time fire spreads to a new patch.</summary>
        public event System.Action<FireSpot> SpotSpread;
        /// <summary>Raised as a patch is extinguished. The AI haptics scene listens to these.</summary>
        public event System.Action<FireSpot> SpotExtinguished;

        void Awake()
        {
            _fireRoot = new GameObject("Fire").transform;
            _fireRoot.SetParent(transform, false);
        }

        /// <summary>Wired to MRUK's SceneLoadedEvent by the bootstrap.</summary>
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
            if (!IgniteInitialFire())
            {
                Debug.LogWarning("[FireSim] Could not find a flammable surface to ignite.");
                return;
            }

            StartCoroutine(SpreadLoop());
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
            var wait = new WaitForSeconds(spreadInterval);
            while (true)
            {
                yield return wait;
                if (_spots.Count == 0 || _spots.Count >= maxSpots) continue;

                // Snapshot so we don't iterate while adding.
                int existing = _spots.Count;
                for (int i = 0; i < existing && _spots.Count < maxSpots; i++)
                {
                    var spot = _spots[i];
                    if (spot == null || !spot.IsBurning) continue;
                    if (Random.value > spreadChance) continue;
                    TrySpreadFrom(spot);
                }
            }
        }

        void TrySpreadFrom(FireSpot spot)
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
                return;
            if (IsTooCloseToExistingSpot(surfacePos)) return;

            SpotSpread?.Invoke(SpawnSpot(surfacePos, surfaceNormal, anchor));
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
            spot.Initialise(this, position, normal, anchor, spotRadius, spotHealth, foamDamagePerParticle);
            _spots.Add(spot);
            return spot;
        }

        public void OnSpotExtinguished(FireSpot spot)
        {
            SpotExtinguished?.Invoke(spot);
            _spots.Remove(spot);
            if (_spots.Count == 0)
                Debug.Log("[FireSim] All fire extinguished. Well done!");
        }
    }
}
