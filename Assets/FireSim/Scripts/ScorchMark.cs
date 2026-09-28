using System.Collections;
using UnityEngine;

namespace FireSim
{
    /// <summary>
    /// The mark a patch leaves on the surface it burns: a sooty disc that darkens the real surface
    /// and glowing ember veins in its middle. It grounds the flames (they visibly come FROM the
    /// table rather than hovering over it), shows how long a patch has burned, and while the patch
    /// smoulders it is the thing that says "not finished yet". When the patch is out the mark is
    /// handed to the fire root and fades over a while, so a room that has just been fought reads
    /// as one.
    /// </summary>
    public sealed class ScorchMark : MonoBehaviour
    {
        static readonly int CharId = Shader.PropertyToID("_Char");
        static readonly int GlowId = Shader.PropertyToID("_Glow");
        static readonly int FadeId = Shader.PropertyToID("_Fade");

        MeshRenderer _renderer;
        MaterialPropertyBlock _block;
        float _char;
        float _glow;
        float _fade = 1f;

        /// <summary>
        /// Build a mark lying in the parent's local XY plane (the surface plane of a FireSpot,
        /// whose +Z is the surface normal). Returns null when the scorch shader is unavailable.
        /// </summary>
        public static ScorchMark Create(Transform parent, float radius)
        {
            var material = FireSimVisuals.ScorchMaterial;
            if (material == null) return null;

            var go = new GameObject("Scorch");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, 0.004f);
            go.transform.localScale = Vector3.one * (radius * 2.9f);

            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = FireSimVisuals.Quad;

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            var mark = go.AddComponent<ScorchMark>();
            mark._renderer = renderer;
            mark._block = new MaterialPropertyBlock();
            mark.Apply();
            return mark;
        }

        /// <param name="charAmount">0 = clean surface, 1 = fully sooted.</param>
        /// <param name="glow">Ember brightness; 0 when nothing is burning under the soot.</param>
        public void SetState(float charAmount, float glow)
        {
            _char = Mathf.Clamp01(charAmount);
            _glow = Mathf.Max(0f, glow);
            Apply();
        }

        /// <summary>
        /// Detach from the (about to be destroyed) patch and fade away under <paramref name="newParent"/>.
        /// The embers die first, then the soot lifts.
        /// </summary>
        public void Release(Transform newParent, float fadeSeconds)
        {
            transform.SetParent(newParent, true);
            StartCoroutine(FadeOut(fadeSeconds));
        }

        IEnumerator FadeOut(float seconds)
        {
            float t = 0f;
            while (t < seconds)
            {
                t += Time.deltaTime;
                _glow = Mathf.MoveTowards(_glow, 0f, Time.deltaTime * 1.5f);
                _fade = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / seconds));
                Apply();
                yield return null;
            }
            Destroy(gameObject);
        }

        void Apply()
        {
            if (_renderer == null) return;
            _block.SetFloat(CharId, _char);
            _block.SetFloat(GlowId, _glow);
            _block.SetFloat(FadeId, _fade);
            _renderer.SetPropertyBlock(_block);
        }
    }
}
