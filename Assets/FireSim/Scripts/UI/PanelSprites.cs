using UnityEngine;

namespace FireSim.UI
{
    /// <summary>
    /// Runtime-generated UI sprites, so the panel can have rounded corners, soft shadows and
    /// outlines without any imported art. Everything is built from a signed-distance function of
    /// a rounded rectangle and sliced (9-slice) so a small texture stretches to any size with
    /// crisp corners. Each sprite is made once and shared.
    /// </summary>
    public static class PanelSprites
    {
        static Sprite _panel, _card, _pill, _keyCap, _bar, _outline, _keyOutline, _shadow, _sheen;

        /// <summary>Large-radius rounded rectangle for the panel body.</summary>
        public static Sprite Panel => _panel != null ? _panel : (_panel = Rounded(28));
        /// <summary>Medium-radius rounded rectangle for rows and cards.</summary>
        public static Sprite Card => _card != null ? _card : (_card = Rounded(14));
        /// <summary>Fully rounded ends, for accents and progress bars.</summary>
        public static Sprite Pill => _pill != null ? _pill : (_pill = Rounded(6));
        /// <summary>Key cap background.</summary>
        public static Sprite KeyCap => _keyCap != null ? _keyCap : (_keyCap = Rounded(10));
        public static Sprite Bar => Pill;
        /// <summary>Thin border matching <see cref="Panel"/>.</summary>
        public static Sprite Outline => _outline != null ? _outline : (_outline = RoundedOutline(28, 2f));
        /// <summary>Thin border matching <see cref="KeyCap"/>.</summary>
        public static Sprite KeyOutline => _keyOutline != null ? _keyOutline : (_keyOutline = RoundedOutline(10, 2f));
        /// <summary>Blurred rounded rectangle to sit behind the panel.</summary>
        public static Sprite Shadow => _shadow != null ? _shadow : (_shadow = SoftShadow(28, 34));
        /// <summary>Rounded rectangle whose alpha fades from the top down: a light sheen over the panel.</summary>
        public static Sprite Sheen => _sheen != null ? _sheen : (_sheen = TopSheen(28));

        /// <summary>Signed distance to a rounded rectangle centred on the origin (negative inside).</summary>
        static float RoundedRectSdf(float x, float y, float halfW, float halfH, float radius)
        {
            float qx = Mathf.Abs(x) - (halfW - radius);
            float qy = Mathf.Abs(y) - (halfH - radius);
            float outside = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) + Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f));
            float inside = Mathf.Min(Mathf.Max(qx, qy), 0f);
            return outside + inside - radius;
        }

        static Sprite Rounded(int radius)
        {
            int size = radius * 2 + 8;
            var tex = NewTexture(size, size, "PanelSprite_Rounded" + radius);
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = RoundedRectSdf(x + 0.5f - half, y + 0.5f - half, half, half, radius);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d)));
            }
            tex.Apply(false, true);
            return Slice(tex, radius + 3);
        }

        static Sprite RoundedOutline(int radius, float thickness)
        {
            int size = radius * 2 + 8;
            var tex = NewTexture(size, size, "PanelSprite_Outline" + radius);
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = RoundedRectSdf(x + 0.5f - half, y + 0.5f - half, half, half, radius);
                // Band between the edge (d = 0) and d = -thickness, anti-aliased both sides.
                float a = Mathf.Clamp01(0.5f - d) * Mathf.Clamp01(0.5f + d + thickness);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
            tex.Apply(false, true);
            return Slice(tex, radius + 3);
        }

        static Sprite SoftShadow(int radius, int blur)
        {
            int size = (radius + blur) * 2 + 8;
            var tex = NewTexture(size, size, "PanelSprite_Shadow");
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = RoundedRectSdf(x + 0.5f - half, y + 0.5f - half, half - blur, half - blur, radius);
                float a = Mathf.Clamp01(1f - (d + blur) / (2f * blur));
                a = a * a * (3f - 2f * a);   // smoothstep for a softer falloff
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
            tex.Apply(false, true);
            return Slice(tex, radius + blur + 3);
        }

        static Sprite TopSheen(int radius)
        {
            int w = radius * 2 + 8, h = 256;
            var tex = NewTexture(w, h, "PanelSprite_Sheen");
            float halfW = w * 0.5f, halfH = h * 0.5f;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float d = RoundedRectSdf(x + 0.5f - halfW, y + 0.5f - halfH, halfW, halfH, radius);
                float mask = Mathf.Clamp01(0.5f - d);
                float fromTop = 1f - (y + 0.5f) / h;                   // 0 at the top row
                float fade = Mathf.Pow(1f - Mathf.Clamp01(fromTop / 0.55f), 2f);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, mask * fade));
            }
            tex.Apply(false, true);
            return Slice(tex, radius + 3);
        }

        static Texture2D NewTexture(int w, int h, string name)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
        }

        static Sprite Slice(Texture2D tex, int border)
        {
            var sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f),
                100f, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
            sprite.name = tex.name;
            return sprite;
        }
    }
}
