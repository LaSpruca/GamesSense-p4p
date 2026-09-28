using System.Collections.Generic;
using UnityEngine;

namespace FireSim
{
    /// <summary>
    /// Publishes every live fire patch as a light source to the FireSim/RoomLight shader through
    /// global shader arrays, once per frame. That shader runs on the MRUK effect meshes and adds
    /// the flames' warm light to the passthrough view of the real floor, table and walls.
    ///
    /// Done this way rather than through URP's own point lights because URP forward on Quest caps
    /// additional lights per object (a floor lit by five patches would drop some of them), and
    /// because nothing else in this passthrough scene is lit anyway - the room is a camera image,
    /// not geometry. Patches register themselves; the field creates itself on first use and is
    /// destroyed with the scene like everything else.
    /// </summary>
    public sealed class FireLightField : MonoBehaviour
    {
        public const int MaxLights = 16;

        static readonly int PosId = Shader.PropertyToID("_FireSimLightPos");
        static readonly int ColorId = Shader.PropertyToID("_FireSimLightColor");
        static readonly int CountId = Shader.PropertyToID("_FireSimLightCount");

        static FireLightField _instance;
        static readonly List<FireSpot> _sources = new List<FireSpot>();

        readonly Vector4[] _positions = new Vector4[MaxLights];
        readonly Vector4[] _colors = new Vector4[MaxLights];

        public static void Register(FireSpot spot)
        {
            if (spot == null) return;
            Ensure();
            if (!_sources.Contains(spot)) _sources.Add(spot);
        }

        public static void Unregister(FireSpot spot)
        {
            _sources.Remove(spot);
        }

        static void Ensure()
        {
            if (_instance != null) return;
            var go = new GameObject("FireLightField");
            _instance = go.AddComponent<FireLightField>();
        }

        void LateUpdate()
        {
            int n = 0;
            for (int i = _sources.Count - 1; i >= 0; i--)
            {
                var spot = _sources[i];
                if (spot == null) { _sources.RemoveAt(i); continue; }
                if (n >= MaxLights) break;
                if (!spot.TryGetLight(out var position, out var range, out var color)) continue;
                _positions[n] = new Vector4(position.x, position.y, position.z, range);
                _colors[n] = new Vector4(color.r, color.g, color.b, 0f);
                n++;
            }

            Shader.SetGlobalVectorArray(PosId, _positions);
            Shader.SetGlobalVectorArray(ColorId, _colors);
            Shader.SetGlobalFloat(CountId, n);
        }

        void OnDestroy()
        {
            if (_instance != this) return;
            _instance = null;
            _sources.Clear();
            Shader.SetGlobalFloat(CountId, 0f);
        }
    }
}
