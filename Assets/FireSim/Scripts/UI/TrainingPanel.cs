using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

namespace FireSim.UI
{
    /// <summary>One line of a <see cref="TrainingPanel.ShowMenu"/> screen.</summary>
    public readonly struct MenuRow
    {
        public readonly string Label;
        public readonly string Value;
        public readonly bool Selected;
        /// <summary>Greyed out: shown, but not something this screen will act on right now.</summary>
        public readonly bool Dimmed;

        public MenuRow(string label, string value, bool selected, bool dimmed = false)
        {
            Label = label;
            Value = value;
            Selected = selected;
            Dimmed = dimmed;
        }
    }

    /// <summary>
    /// The one piece of UI in the experience: a world-space panel used for the briefing before a
    /// run, the result summary after it, the pause menu, and the setup screen the session is
    /// configured from. Built entirely in code, like the rest of the scene, so nothing needs
    /// hand-wiring and there are no imported assets to keep in sync.
    ///
    /// It uses legacy <see cref="Text"/> rather than TextMeshPro on purpose: TMP needs its Essential
    /// Resources imported into the project before a single label will render, and this project has
    /// none. <c>LegacyRuntime.ttf</c> is a built-in resource that is always there. The look comes
    /// from <see cref="PanelSprites"/> instead: a rounded, softly shadowed card with a light sheen
    /// and hairline border, a title/subtitle header with an accent, rows drawn as cards with
    /// progress bars for percentages, a pill highlight for the selected menu row, and the footer's
    /// controller buttons drawn as key caps rather than spelled out in a run of text.
    ///
    /// Titles of the form "Heading - detail" are split at the first " - " into a heading and a
    /// dimmer subtitle line, so callers keep passing one string. Footers of the form
    /// "A  start     B  switch     Y  setup" (hints separated by three or more spaces, each
    /// starting with a button name) become key caps; anything else is drawn as plain text.
    ///
    /// Positioning is "lazy follow": the panel is planted in front of the user and then stays put,
    /// so reading it is comfortable, but if the user walks away or turns past
    /// <see cref="recentreAngle"/> it glides back in front of them rather than being lost behind
    /// their shoulder. Screens fade and settle in rather than popping.
    /// </summary>
    public sealed class TrainingPanel : MonoBehaviour
    {
        const float PanelWidthPx = 1240f;
        const float PanelHeightPx = 860f;
        const float PixelsToMetres = 0.001f;
        const float Margin = 64f;
        /// <summary>Distance from the top edge to the header divider.</summary>
        const float HeaderBottom = 176f;
        const float ContentTop = HeaderBottom + 26f;
        const float FooterBottom = 38f;
        const float FooterHeight = 52f;
        const float ContentBottom = FooterBottom + FooterHeight + 30f;
        const float NoteHeightPx = 150f;
        const float MenuRowHeightPx = 62f;
        const float MenuRowGapPx = 6f;
        const float StatRowHeightPx = 66f;
        const float StatRowGapPx = 8f;
        const float FadeSeconds = 0.2f;

        // Palette. Warm neutrals on a deep slate card; the accent is the same orange as the fire.
        static readonly Color Ink = new Color(0.94f, 0.95f, 0.97f);
        static readonly Color InkSoft = new Color(0.8f, 0.82f, 0.87f);
        static readonly Color InkDim = new Color(0.62f, 0.65f, 0.72f);
        static readonly Color InkFaint = new Color(0.42f, 0.44f, 0.5f);
        static readonly Color Accent = new Color(1f, 0.5f, 0.16f);
        static readonly Color AccentText = new Color(1f, 0.83f, 0.6f);
        static readonly Color CardTint = new Color(1f, 1f, 1f, 0.05f);
        static readonly Color CardTintAlt = new Color(1f, 1f, 1f, 0.025f);

        /// <summary>Footer tokens drawn as key caps. Anything else stays as text.</summary>
        static readonly HashSet<string> KeyNames = new HashSet<string>
        {
            "A", "B", "X", "Y", "Menu", "Stick", "Grip", "Trigger",
        };
        static readonly Regex HintSplit = new Regex(@"\s{3,}", RegexOptions.Compiled);
        static readonly Regex PercentValue = new Regex(@"^\s*(\d{1,3})\s*%\s*$", RegexOptions.Compiled);

        [Tooltip("How far in front of the user the panel sits, in metres.")]
        public float distance = 1.5f;
        [Tooltip("Degrees off-centre before the panel re-centres itself in front of the user.")]
        public float recentreAngle = 38f;
        [Tooltip("Metres the user can move before the panel re-centres.")]
        public float recentreDistance = 0.8f;
        public float followLerpSpeed = 3.5f;

        Transform _head;
        Canvas _canvas;
        CanvasGroup _group;
        RectTransform _root;
        Text _title;
        Text _subtitle;
        Text _body;
        Text _note;
        RectTransform _footer;
        RectTransform _statsRoot;
        readonly List<GameObject> _statRows = new List<GameObject>();
        readonly List<GameObject> _footerItems = new List<GameObject>();
        Vector3 _targetPosition;
        Quaternion _targetRotation;
        bool _visible;
        Coroutine _fade;

        public bool IsVisible => _visible;

        public static TrainingPanel Create(Transform head)
        {
            var go = new GameObject("TrainingPanel");
            var panel = go.AddComponent<TrainingPanel>();
            panel.Build(head);
            return panel;
        }

        void Build(Transform head)
        {
            _head = head;

            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            gameObject.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 3f;
            _group = gameObject.AddComponent<CanvasGroup>();
            _group.interactable = false;
            _group.blocksRaycasts = false;

            _root = (RectTransform)transform;
            _root.sizeDelta = new Vector2(PanelWidthPx, PanelHeightPx);
            _root.localScale = Vector3.one * PixelsToMetres;

            BuildChrome();
            BuildHeader();
            BuildContent();
            BuildFooter();

            _group.alpha = 0f;
            _canvas.enabled = false;
            _visible = false;
        }

        /// <summary>Shadow, card, sheen and hairline border. Sibling order is paint order.</summary>
        void BuildChrome()
        {
            var shadow = MakeImage("Shadow", _root, PanelSprites.Shadow, new Color(0f, 0f, 0f, 0.55f));
            Stretch(shadow, new Vector4(-40f, -56f, -40f, -28f));
            shadow.anchoredPosition = new Vector2(0f, -16f);

            var background = MakeImage("Background", _root, PanelSprites.Panel, new Color(0.075f, 0.085f, 0.115f, 0.96f));
            Stretch(background, Vector4.zero);

            var sheen = MakeImage("Sheen", _root, PanelSprites.Sheen, new Color(1f, 1f, 1f, 0.07f));
            Stretch(sheen, Vector4.zero);

            var outline = MakeImage("Outline", _root, PanelSprites.Outline, new Color(1f, 1f, 1f, 0.1f));
            Stretch(outline, Vector4.zero);
        }

        void BuildHeader()
        {
            // Accent bar beside the title, in the fire's orange.
            var accent = MakeImage("Accent", _root, PanelSprites.Pill, Accent);
            accent.anchorMin = accent.anchorMax = new Vector2(0f, 1f);
            accent.pivot = new Vector2(0f, 1f);
            accent.sizeDelta = new Vector2(8f, 58f);
            accent.anchoredPosition = new Vector2(Margin, -(Margin + 2f));

            _title = MakeText("Title", _root, 54, FontStyle.Bold, TextAnchor.UpperLeft, Ink);
            _title.rectTransform.anchorMin = new Vector2(0f, 1f);
            _title.rectTransform.anchorMax = new Vector2(1f, 1f);
            _title.rectTransform.pivot = new Vector2(0.5f, 1f);
            _title.rectTransform.offsetMin = new Vector2(Margin + 28f, -(Margin + 66f));
            _title.rectTransform.offsetMax = new Vector2(-Margin, -Margin);
            _title.horizontalOverflow = HorizontalWrapMode.Overflow;

            _subtitle = MakeText("Subtitle", _root, 32, FontStyle.Normal, TextAnchor.UpperLeft, InkDim);
            _subtitle.rectTransform.anchorMin = new Vector2(0f, 1f);
            _subtitle.rectTransform.anchorMax = new Vector2(1f, 1f);
            _subtitle.rectTransform.pivot = new Vector2(0.5f, 1f);
            _subtitle.rectTransform.offsetMin = new Vector2(Margin + 28f, -(Margin + 112f));
            _subtitle.rectTransform.offsetMax = new Vector2(-Margin, -(Margin + 68f));
            _subtitle.horizontalOverflow = HorizontalWrapMode.Overflow;

            var divider = MakeImage("Divider", _root, null, new Color(1f, 1f, 1f, 0.08f));
            divider.anchorMin = new Vector2(0f, 1f);
            divider.anchorMax = new Vector2(1f, 1f);
            divider.pivot = new Vector2(0.5f, 1f);
            divider.offsetMin = new Vector2(Margin, -(HeaderBottom + 2f));
            divider.offsetMax = new Vector2(-Margin, -HeaderBottom);
        }

        void BuildContent()
        {
            _body = MakeText("Body", _root, 36, FontStyle.Normal, TextAnchor.UpperLeft, InkSoft);
            Stretch(_body.rectTransform, new Vector4(Margin, ContentBottom, Margin, ContentTop));
            _body.lineSpacing = 1.22f;

            _statsRoot = MakeRect("Stats", _root);
            Stretch(_statsRoot, new Vector4(Margin, ContentBottom, Margin, ContentTop));

            // Help text under a menu: what the selected row does, and the last thing that happened.
            // Sits in the lower part of the content area, below where menu rows reach.
            _note = MakeText("Note", _root, 29, FontStyle.Normal, TextAnchor.LowerLeft, InkDim);
            _note.rectTransform.anchorMin = new Vector2(0f, 0f);
            _note.rectTransform.anchorMax = new Vector2(1f, 0f);
            _note.rectTransform.pivot = new Vector2(0.5f, 0f);
            _note.rectTransform.offsetMin = new Vector2(Margin, ContentBottom);
            _note.rectTransform.offsetMax = new Vector2(-Margin, ContentBottom + NoteHeightPx);
            _note.lineSpacing = 1.18f;
        }

        /// <summary>A left-aligned row of hints along the bottom, each a key cap plus a label.</summary>
        void BuildFooter()
        {
            _footer = MakeRect("Footer", _root);
            _footer.anchorMin = new Vector2(0f, 0f);
            _footer.anchorMax = new Vector2(1f, 0f);
            _footer.pivot = new Vector2(0f, 0f);
            _footer.offsetMin = new Vector2(Margin, FooterBottom);
            _footer.offsetMax = new Vector2(-Margin, FooterBottom + FooterHeight);

            var layout = _footer.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 40f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
        }

        // --- Content ------------------------------------------------------------------------

        /// <summary>Show a block of prose. Clears any stat rows from a previous screen.</summary>
        public void Show(string title, string body, string footer)
        {
            ClearStatRows();
            _body.gameObject.SetActive(true);
            _note.gameObject.SetActive(false);
            SetTitle(title);
            _body.text = body;
            SetFooter(footer);
            Recentre(snap: !_visible);
            SetVisible(true);
        }

        /// <summary>
        /// Show label/value rows instead of prose. Built as paired left- and right-aligned labels
        /// because the built-in font is proportional, so column alignment by padding does not work.
        /// A value that is a percentage also gets a progress bar.
        /// </summary>
        public void ShowStats(string title, IReadOnlyList<(string Label, string Value)> rows, string footer)
        {
            ClearStatRows();
            _body.gameObject.SetActive(false);
            _note.gameObject.SetActive(false);
            SetTitle(title);
            SetFooter(footer);

            for (int i = 0; i < rows.Count; i++)
            {
                var rt = MakeRow($"Stat{i}", i, StatRowHeightPx, StatRowGapPx);
                var card = MakeImage("Card", rt, PanelSprites.Card, i % 2 == 0 ? CardTint : CardTintAlt);
                Stretch(card, Vector4.zero);

                var label = MakeText("Label", rt, 36, FontStyle.Normal, TextAnchor.MiddleLeft, InkSoft);
                Stretch(label.rectTransform, new Vector4(24f, 0f, 24f, 0f));
                label.text = rows[i].Label;

                var value = MakeText("Value", rt, 38, FontStyle.Bold, TextAnchor.MiddleRight, Ink);
                Stretch(value.rectTransform, new Vector4(24f, 0f, 24f, 0f));
                value.text = rows[i].Value;

                var percent = PercentValue.Match(rows[i].Value ?? string.Empty);
                if (percent.Success && int.TryParse(percent.Groups[1].Value, out int pct))
                    MakeProgressBar(rt, Mathf.Clamp01(pct / 100f));
            }

            Recentre(snap: !_visible);
            SetVisible(true);
        }

        /// <summary>
        /// Show a list of settings, one highlighted as the current selection. Used by the setup
        /// screen and the pause menu; the panel itself holds no selection state, it just draws
        /// what it is given, so the screen stays the single owner of what is selected and what
        /// each row means.
        /// </summary>
        public void ShowMenu(string title, IReadOnlyList<MenuRow> rows, string note, string footer)
        {
            ClearStatRows();
            _body.gameObject.SetActive(false);
            _note.gameObject.SetActive(true);
            SetTitle(title);
            _note.text = note;
            SetFooter(footer);

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var rt = MakeRow($"Menu{i}", i, MenuRowHeightPx, MenuRowGapPx);

                if (row.Selected)
                {
                    // Drawn first so it sits behind the two labels: sibling order is paint order.
                    var highlight = MakeImage("Highlight", rt, PanelSprites.Card, new Color(Accent.r, Accent.g, Accent.b, 0.2f));
                    Stretch(highlight, Vector4.zero);

                    var marker = MakeImage("Marker", rt, PanelSprites.Pill, Accent);
                    marker.anchorMin = new Vector2(0f, 0.5f);
                    marker.anchorMax = new Vector2(0f, 0.5f);
                    marker.pivot = new Vector2(0f, 0.5f);
                    marker.sizeDelta = new Vector2(6f, MenuRowHeightPx - 20f);
                    marker.anchoredPosition = new Vector2(8f, 0f);
                }

                Color labelColor = row.Dimmed ? InkFaint : row.Selected ? AccentText : InkSoft;
                Color valueColor = row.Dimmed ? InkFaint : Ink;

                var label = MakeText("Label", rt, 35, row.Selected ? FontStyle.Bold : FontStyle.Normal,
                    TextAnchor.MiddleLeft, labelColor);
                Stretch(label.rectTransform, new Vector4(30f, 0f, 24f, 0f));
                label.text = row.Label;

                var value = MakeText("Value", rt, 35, FontStyle.Bold, TextAnchor.MiddleRight, valueColor);
                Stretch(value.rectTransform, new Vector4(30f, 0f, 24f, 0f));
                value.text = row.Value;
            }

            Recentre(snap: !_visible);
            SetVisible(true);
        }

        public void Hide() => SetVisible(false);

        /// <summary>"Heading - detail" becomes a heading and a subtitle; anything else is just the heading.</summary>
        void SetTitle(string title)
        {
            title = title ?? string.Empty;
            int split = title.IndexOf(" - ", System.StringComparison.Ordinal);
            if (split > 0)
            {
                _title.text = title.Substring(0, split).Trim();
                _subtitle.text = title.Substring(split + 3).Trim();
                _subtitle.gameObject.SetActive(true);
            }
            else
            {
                _title.text = title;
                _subtitle.text = string.Empty;
                _subtitle.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// Footer hints, each either "Key  label" drawn as a key cap plus text, or plain text.
        /// The whole footer falls back to one plain label when no hint starts with a button name.
        /// </summary>
        void SetFooter(string footer)
        {
            foreach (var item in _footerItems)
                if (item != null) Destroy(item);
            _footerItems.Clear();

            footer = (footer ?? string.Empty).Trim();
            if (footer.Length == 0) return;

            var hints = HintSplit.Split(footer);
            bool anyKey = false;
            foreach (var hint in hints)
                if (TrySplitHint(hint, out _, out _)) { anyKey = true; break; }

            if (!anyKey)
            {
                _footerItems.Add(MakeFooterLabel(footer, InkDim).gameObject);
                return;
            }

            foreach (var hint in hints)
            {
                if (string.IsNullOrWhiteSpace(hint)) continue;
                if (TrySplitHint(hint, out var key, out var label)) _footerItems.Add(MakeHint(key, label));
                else _footerItems.Add(MakeFooterLabel(hint.Trim(), InkDim).gameObject);
            }
        }

        static bool TrySplitHint(string hint, out string key, out string label)
        {
            key = label = null;
            hint = hint.Trim();
            int space = hint.IndexOf(' ');
            if (space <= 0) return false;
            string head = hint.Substring(0, space);
            if (!KeyNames.Contains(head)) return false;
            key = head;
            label = hint.Substring(space).Trim();
            return label.Length > 0;
        }

        GameObject MakeHint(string key, string label)
        {
            var item = MakeRect("Hint", _footer);
            var layout = item.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 14f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            var cap = MakeImage("Key", item, PanelSprites.KeyCap, new Color(1f, 1f, 1f, 0.12f));
            var capSize = cap.gameObject.AddComponent<LayoutElement>();
            capSize.preferredWidth = Mathf.Max(46f, 22f + 18f * key.Length);
            capSize.preferredHeight = 46f;
            var capOutline = MakeImage("Outline", cap, PanelSprites.KeyOutline, new Color(1f, 1f, 1f, 0.22f));
            Stretch(capOutline, Vector4.zero);
            var capText = MakeText("Text", cap, key.Length > 1 ? 22 : 26, FontStyle.Bold, TextAnchor.MiddleCenter, Ink);
            Stretch(capText.rectTransform, Vector4.zero);
            capText.text = key;

            MakeFooterLabel(label, InkDim, item);
            return item.gameObject;
        }

        Text MakeFooterLabel(string content, Color color, Transform parent = null)
        {
            var text = MakeText("Label", parent != null ? parent : _footer, 30, FontStyle.Normal, TextAnchor.MiddleLeft, color);
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.text = content;
            return text;
        }

        /// <summary>A slim track and fill between the label and the value of a stats row.</summary>
        void MakeProgressBar(RectTransform row, float fraction)
        {
            var track = MakeImage("Track", row, PanelSprites.Bar, new Color(1f, 1f, 1f, 0.1f));
            track.anchorMin = new Vector2(0.52f, 0.5f);
            track.anchorMax = new Vector2(1f, 0.5f);
            track.pivot = new Vector2(0f, 0.5f);
            track.offsetMin = new Vector2(0f, -4f);
            track.offsetMax = new Vector2(-150f, 4f);

            var fill = MakeImage("Fill", track, PanelSprites.Bar, Accent);
            fill.anchorMin = new Vector2(0f, 0f);
            fill.anchorMax = new Vector2(Mathf.Max(0.02f, fraction), 1f);
            fill.offsetMin = Vector2.zero;
            fill.offsetMax = Vector2.zero;
        }

        /// <summary>One top-anchored row of a stats or menu list, tracked for later cleanup.</summary>
        RectTransform MakeRow(string name, int index, float rowHeight, float gap)
        {
            var row = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)row.transform;
            rt.SetParent(_statsRoot, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, rowHeight);
            rt.anchoredPosition = new Vector2(0f, -index * (rowHeight + gap));
            _statRows.Add(row);
            return rt;
        }

        void ClearStatRows()
        {
            foreach (var row in _statRows)
                if (row != null) Destroy(row);
            _statRows.Clear();
        }

        // --- Visibility ---------------------------------------------------------------------

        /// <summary>
        /// Fades the whole canvas via its CanvasGroup rather than deactivating children: deactivating
        /// them would undo the per-screen setup (ShowStats switches the prose body off, and
        /// re-enabling every child would put it straight back on top of the stat rows). The Canvas
        /// itself is only disabled once fully faded out, so a hidden panel costs nothing to draw.
        /// </summary>
        void SetVisible(bool visible)
        {
            bool wasVisible = _visible;
            _visible = visible;
            if (visible == wasVisible && (_canvas.enabled == visible)) return;

            if (_fade != null) StopCoroutine(_fade);
            _fade = StartCoroutine(Fade(visible));
        }

        IEnumerator Fade(bool fadeIn)
        {
            _canvas.enabled = true;
            float from = _group.alpha;
            float to = fadeIn ? 1f : 0f;
            float t = 0f;
            while (t < FadeSeconds)
            {
                // Unscaled: the pause menu sets timeScale to 0 and must still appear.
                t += Time.unscaledDeltaTime;
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / FadeSeconds));
                _group.alpha = Mathf.Lerp(from, to, k);
                // Settle in from slightly small; shrink slightly on the way out.
                float scale = fadeIn ? Mathf.Lerp(0.965f, 1f, k) : Mathf.Lerp(1f, 0.985f, k);
                _root.localScale = Vector3.one * (PixelsToMetres * scale);
                yield return null;
            }
            _group.alpha = to;
            _root.localScale = Vector3.one * PixelsToMetres;
            _canvas.enabled = fadeIn;
            _fade = null;
        }

        // --- Placement ----------------------------------------------------------------------

        void LateUpdate()
        {
            if (!_visible || _head == null) return;

            Vector3 toPanel = transform.position - _head.position;
            Vector3 headForward = Flatten(_head.forward);
            bool lost = Vector3.Angle(headForward, Flatten(toPanel)) > recentreAngle ||
                        Vector3.Distance(_head.position, transform.position) > distance + recentreDistance;
            if (lost) Recentre(snap: false);

            // Unscaled: the pause menu sets timeScale to 0, and a panel that stopped
            // following the head while paused would be left behind by anyone who turned.
            float k = 1f - Mathf.Exp(-followLerpSpeed * Time.unscaledDeltaTime);
            transform.position = Vector3.Lerp(transform.position, _targetPosition, k);
            transform.rotation = Quaternion.Slerp(transform.rotation, _targetRotation, k);
        }

        void Recentre(bool snap)
        {
            if (_head == null) return;

            Vector3 forward = Flatten(_head.forward);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            forward.Normalize();

            _targetPosition = _head.position + forward * distance;
            // Keep the panel level with the eyes rather than tilted, which reads as more stable.
            _targetRotation = Quaternion.LookRotation(forward, Vector3.up);

            if (!snap) return;
            transform.position = _targetPosition;
            transform.rotation = _targetRotation;
        }

        static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        // --- Construction helpers -----------------------------------------------------------

        static Font _font;

        /// <summary>
        /// The built-in dynamic font. "LegacyRuntime.ttf" replaced "Arial.ttf" as the shipped
        /// built-in resource; both are probed, then an OS font, so this cannot end up null and
        /// silently render nothing.
        /// </summary>
        public static Font BuiltInFont
        {
            get
            {
                if (_font != null) return _font;
                // Deliberately not "a ?? b": UnityEngine.Object overloads ==, and ?? bypasses that
                // overload, so a destroyed-but-non-null reference would slip through.
                _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                if (_font == null)
                {
                    _font = Font.CreateDynamicFontFromOSFont("Arial", 32);
                    Debug.LogWarning("[FireSim] Falling back to an OS font for UI text.");
                }
                return _font;
            }
        }

        static RectTransform MakeRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        /// <summary>An Image; sliced when given a sprite, a flat fill when not.</summary>
        static RectTransform MakeImage(string name, Transform parent, Sprite sprite, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple;
            image.color = color;
            image.raycastTarget = false;
            return (RectTransform)go.transform;
        }

        static Text MakeText(string name, Transform parent, int size, FontStyle style,
            TextAnchor anchor, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.font = BuiltInFont;
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = anchor;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        /// <summary>Fill the parent, inset by (left, bottom, right, top).</summary>
        static void Stretch(RectTransform rt, Vector4 padding)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(padding.x, padding.y);
            rt.offsetMax = new Vector2(-padding.z, -padding.w);
        }
    }
}
