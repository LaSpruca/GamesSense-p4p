using System.Collections.Generic;
using UnityEngine;

namespace FireSim
{
    /// <summary>
    /// Lazily-built, shared textures, materials and meshes for every FireSim visual, generated at
    /// runtime so the simulation still needs no imported art. The shaders live under
    /// Resources/Shaders so Shader.Find can reach them in a player build; if one is missing the
    /// matching material falls back to a plain vertex-coloured sprite so nothing renders pink.
    ///
    /// The particle shaders read four floats from TEXCOORD0 (uv, age, per-particle random), which
    /// is why every particle renderer that uses them must go through <see cref="ApplyParticleMaterial"/>.
    /// </summary>
    public static class FireSimVisuals
    {
        /// <summary>Vertex streams the FireSim particle shaders expect. Packed by Unity into TEXCOORD0.xyzw.</summary>
        public static readonly List<ParticleSystemVertexStream> ParticleStreams = new List<ParticleSystemVertexStream>
        {
            ParticleSystemVertexStream.Position,
            ParticleSystemVertexStream.Color,
            ParticleSystemVertexStream.UV,
            ParticleSystemVertexStream.AgePercent,
            ParticleSystemVertexStream.StableRandomX,
        };

        const int NoiseSize = 128;
        const int RampSize = 256;

        static Texture2D _noise;
        static Texture2D _fireRamp;
        static Texture2D _softDot;
        static Material _flame, _smoke, _foam, _glow, _scorch, _roomLight;
        static Material _legacyAdditive, _legacyAlpha;
        static Mesh _quad;
        static readonly HashSet<string> _warned = new HashSet<string>();

        // --- Textures -----------------------------------------------------------------------

        /// <summary>
        /// Tileable multi-octave value noise, two independent channels (R and G), linear data.
        /// One texture serves the flame erosion, smoke break-up, foam rims and scorch soot.
        /// </summary>
        public static Texture2D Noise
        {
            get
            {
                if (_noise != null) return _noise;
                _noise = new Texture2D(NoiseSize, NoiseSize, TextureFormat.RGBA32, true, true)
                {
                    name = "FireSim_Noise",
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Trilinear,
                };

                var r = new float[NoiseSize * NoiseSize];
                var g = new float[NoiseSize * NoiseSize];
                FillNoise(r, seed: 11);
                FillNoise(g, seed: 47);

                var pixels = new Color32[NoiseSize * NoiseSize];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color32((byte)(r[i] * 255f), (byte)(g[i] * 255f), 128, 255);
                _noise.SetPixels32(pixels);
                _noise.Apply(true, true);
                return _noise;
            }
        }

        /// <summary>Periodic fBm value noise, stretched to span the full 0..1 range.</summary>
        static void FillNoise(float[] dst, int seed)
        {
            float min = float.MaxValue, max = float.MinValue;
            for (int y = 0; y < NoiseSize; y++)
            for (int x = 0; x < NoiseSize; x++)
            {
                float u = (float)x / NoiseSize, v = (float)y / NoiseSize;
                float sum = 0f, amp = 0.5f, total = 0f;
                int period = 4;
                for (int o = 0; o < 4; o++)
                {
                    sum += PeriodicValueNoise(u * period, v * period, period, seed + o * 101) * amp;
                    total += amp;
                    amp *= 0.5f;
                    period *= 2;
                }
                float n = sum / total;
                dst[y * NoiseSize + x] = n;
                if (n < min) min = n;
                if (n > max) max = n;
            }

            float range = Mathf.Max(1e-4f, max - min);
            for (int i = 0; i < dst.Length; i++) dst[i] = (dst[i] - min) / range;
        }

        static float PeriodicValueNoise(float x, float y, int period, int seed)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            int x1 = (x0 + 1) % period, y1 = (y0 + 1) % period;
            x0 %= period; y0 %= period;
            float v00 = Hash(x0, y0, seed), v10 = Hash(x1, y0, seed);
            float v01 = Hash(x0, y1, seed), v11 = Hash(x1, y1, seed);
            return Mathf.Lerp(Mathf.Lerp(v00, v10, fx), Mathf.Lerp(v01, v11, fx), fy);
        }

        static float Hash(int x, int y, int seed)
        {
            uint h = (uint)(x * 374761393) + (uint)(y * 668265263) + (uint)(seed * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777215f;
        }

        /// <summary>Black-body style flame ramp: deep red at 0 through orange and yellow to near white at 1.</summary>
        public static Texture2D FireRamp
        {
            get
            {
                if (_fireRamp != null) return _fireRamp;
                _fireRamp = new Texture2D(RampSize, 4, TextureFormat.RGBA32, false, false)
                {
                    name = "FireSim_FireRamp",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                var gradient = new Gradient();
                gradient.SetKeys(
                    new[]
                    {
                        new GradientColorKey(new Color(0.35f, 0.02f, 0.00f), 0.00f),
                        new GradientColorKey(new Color(0.85f, 0.12f, 0.02f), 0.18f),
                        new GradientColorKey(new Color(1.00f, 0.42f, 0.05f), 0.40f),
                        new GradientColorKey(new Color(1.00f, 0.72f, 0.18f), 0.62f),
                        new GradientColorKey(new Color(1.00f, 0.92f, 0.55f), 0.82f),
                        new GradientColorKey(new Color(1.00f, 0.99f, 0.90f), 1.00f),
                    },
                    new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });

                var pixels = new Color[RampSize * 4];
                for (int x = 0; x < RampSize; x++)
                {
                    var c = gradient.Evaluate(x / (float)(RampSize - 1));
                    for (int y = 0; y < 4; y++) pixels[y * RampSize + x] = c;
                }
                _fireRamp.SetPixels(pixels);
                _fireRamp.Apply(false, true);
                return _fireRamp;
            }
        }

        /// <summary>Soft round dot, used only by the fallback materials when a shader is missing.</summary>
        public static Texture2D SoftDot
        {
            get
            {
                if (_softDot != null) return _softDot;
                const int size = 64;
                _softDot = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "FireSim_SoftDot" };
                Vector2 c = new Vector2(size * 0.5f, size * 0.5f);
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), c) / (size * 0.5f);
                    float a = Mathf.Clamp01(1f - d);
                    a *= a;
                    _softDot.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
                _softDot.Apply();
                _softDot.wrapMode = TextureWrapMode.Clamp;
                return _softDot;
            }
        }

        // --- Materials ----------------------------------------------------------------------

        public static Material FlameMaterial
        {
            get
            {
                if (_flame != null) return _flame;
                _flame = Make("FireSim/Flame", "FireSim_Flame", additiveFallback: true);
                if (_flame.HasProperty("_Ramp"))
                {
                    _flame.SetTexture("_MainTex", Noise);
                    _flame.SetTexture("_Ramp", FireRamp);
                }
                return _flame;
            }
        }

        public static Material SmokeMaterial
        {
            get
            {
                if (_smoke != null) return _smoke;
                _smoke = Make("FireSim/Smoke", "FireSim_Smoke", additiveFallback: false);
                if (_smoke.HasProperty("_Erode")) _smoke.SetTexture("_MainTex", Noise);
                return _smoke;
            }
        }

        public static Material FoamMaterial
        {
            get
            {
                if (_foam != null) return _foam;
                _foam = Make("FireSim/Foam", "FireSim_Foam", additiveFallback: false);
                if (_foam.HasProperty("_Roughness")) _foam.SetTexture("_MainTex", Noise);
                return _foam;
            }
        }

        public static Material GlowMaterial
        {
            get
            {
                if (_glow != null) return _glow;
                _glow = Make("FireSim/Glow", "FireSim_Glow", additiveFallback: true);
                return _glow;
            }
        }

        /// <summary>Shared scorch material; per-patch state goes through a MaterialPropertyBlock. Null if the shader is missing.</summary>
        public static Material ScorchMaterial
        {
            get
            {
                if (_scorch != null) return _scorch;
                var shader = FindShader("FireSim/Scorch");
                if (shader == null) return null;
                _scorch = new Material(shader) { name = "FireSim_Scorch" };
                _scorch.SetTexture("_MainTex", Noise);
                // Before the particles: the mark lies on the surface under the flames.
                _scorch.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent - 20;
                return _scorch;
            }
        }

        /// <summary>
        /// Material for the MRUK effect meshes: invisible except for the fire light it adds to the
        /// real room. Null if the shader is missing, which leaves the meshes unrendered as before.
        /// </summary>
        public static Material RoomLightMaterial
        {
            get
            {
                if (_roomLight != null) return _roomLight;
                var shader = FindShader("FireSim/RoomLight");
                if (shader == null) return null;
                _roomLight = new Material(shader) { name = "FireSim_RoomLight" };
                // First of the transparents, so every other effect draws over the lit room.
                _roomLight.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent - 50;
                return _roomLight;
            }
        }

        /// <summary>Kept for anything that still wants a plain additive sprite material.</summary>
        public static Material AdditiveParticleMaterial => _legacyAdditive != null ? _legacyAdditive : (_legacyAdditive = LegacySprite("FireSim_LegacyAdditive"));

        /// <summary>Kept for anything that still wants a plain alpha-blended sprite material.</summary>
        public static Material AlphaParticleMaterial => _legacyAlpha != null ? _legacyAlpha : (_legacyAlpha = LegacySprite("FireSim_LegacyAlpha"));

        /// <summary>Assign a FireSim particle material and the vertex streams its shader reads.</summary>
        public static void ApplyParticleMaterial(ParticleSystemRenderer renderer, Material material)
        {
            renderer.sharedMaterial = material;
            renderer.SetActiveVertexStreams(ParticleStreams);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        static Material Make(string shaderName, string materialName, bool additiveFallback)
        {
            var shader = FindShader(shaderName);
            if (shader == null) return LegacySprite(materialName + "_Fallback");
            return new Material(shader) { name = materialName };
        }

        static Shader FindShader(string name)
        {
            var shader = Shader.Find(name);
            if (shader == null && _warned.Add(name))
                Debug.LogWarning($"[FireSim] Shader '{name}' not found - falling back to a plain sprite material. " +
                                 "Check that Assets/FireSim/Resources/Shaders is in the build.");
            return shader;
        }

        static Material LegacySprite(string name)
        {
            var m = new Material(Shader.Find("Sprites/Default")) { name = name, mainTexture = SoftDot };
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            return m;
        }

        // --- Meshes -------------------------------------------------------------------------

        /// <summary>Unit quad in the local XY plane facing +Z, for surface decals.</summary>
        public static Mesh Quad
        {
            get
            {
                if (_quad != null) return _quad;
                _quad = new Mesh { name = "FireSim_Quad" };
                _quad.SetVertices(new[]
                {
                    new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                    new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f),
                });
                _quad.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) });
                _quad.SetNormals(new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward });
                _quad.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
                _quad.RecalculateBounds();
                _quad.UploadMeshData(true);
                return _quad;
            }
        }
    }
}
