using System.Collections.Generic;
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
    /// run, the result summary after it, and the setup screen the session is configured from.
    /// Built entirely in code, like the rest of the scene, so nothing needs hand-wiring and there
    /// are no imported assets to keep in sync.
    ///
    /// It uses legacy <see cref="Text"/> rather than TextMeshPro on purpose: TMP needs its Essential
    /// Resources imported into the project before a single label will render, and this project has
    /// none. <c>LegacyRuntime.ttf</c> is a built-in resource that is always there.
    ///
    /// Positioning is "lazy follow": the panel is planted in front of the user and then stays put,
    /// so reading it is comfortable, but if the user walks away or turns past
    /// <see cref="recentreAngle"/> it glides back in front of them rather than being lost behind
    /// their shoulder.
    /// </summary>
    public sealed class TrainingPanel : MonoBehaviour
    {
        const float PanelWidthPx = 1200f;
        const float PanelHeightPx = 820f;
        const float PixelsToMetres = 0.001f;
        const float MenuRowHeightPx = 58f;
        const float MenuNoteHeightPx = 132f;

        [Tooltip("How far in front of the user the panel sits, in metres.")]
        public float distance = 1.5f;
        [Tooltip("Degrees off-centre before the panel re-centres itself in front of the user.")]
        public float recentreAngle = 38f;
        [Tooltip("Metres the user can move before the panel re-centres.")]
        public float recentreDistance = 0.8f;
        public float followLerpSpeed = 3.5f;

        Transform _head;
        Canvas _canvas;
        RectTransform _root;
        Text _title;
        Text _body;
        Text _footer;
        Text _note;
        RectTransform _statsRoot;
        readonly List<GameObject> _statRows = new List<GameObject>();
        Vector3 _targetPosition;
        Quaternion _targetRotation;
        bool _visible;

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

            _root = (RectTransform)transform;
            _root.sizeDelta = new Vector2(PanelWidthPx, PanelHeightPx);
            _root.localScale = Vector3.one * PixelsToMetres;

            // Dark, mostly-opaque backing so text stays readable against an arbitrary real room.
            var background = MakeImage("Background", _root, new Color(0.04f, 0.04f, 0.06f, 0.88f));
            Stretch(background, Vector4.zero);

            var accent = MakeImage("Accent", _root, new Color(1f, 0.45f, 0.12f, 1f));
            accent.anchorMin = new Vector2(0f, 1f);
            accent.anchorMax = new Vector2(1f, 1f);
            accent.pivot = new Vector2(0.5f, 1f);
            accent.sizeDelta = new Vector2(0f, 8f);
            accent.anchoredPosition = Vector2.zero;

            _title = MakeText("Title", _root, 64, FontStyle.Bold, TextAnchor.UpperLeft,
                new Color(1f, 0.72f, 0.36f));
            _title.rectTransform.anchorMin = new Vector2(0f, 1f);
            _title.rectTransform.anchorMax = new Vector2(1f, 1f);
            _title.rectTransform.pivot = new Vector2(0.5f, 1f);
            _title.rectTransform.sizeDelta = new Vector2(-112f, 84f);
            _title.rectTransform.anchoredPosition = new Vector2(0f, -40f);

            _body = MakeText("Body", _root, 38, FontStyle.Normal, TextAnchor.UpperLeft, Color.white);
            _body.rectTransform.anchorMin = new Vector2(0f, 0f);
            _body.rectTransform.anchorMax = new Vector2(1f, 1f);
            _body.rectTransform.offsetMin = new Vector2(56f, 110f);
            _body.rectTransform.offsetMax = new Vector2(-56f, -140f);
            _body.lineSpacing = 1.18f;

            _statsRoot = MakeRect("Stats", _root);
            _statsRoot.anchorMin = new Vector2(0f, 0f);
            _statsRoot.anchorMax = new Vector2(1f, 1f);
            _statsRoot.offsetMin = new Vector2(56f, 110f);
            _statsRoot.offsetMax = new Vector2(-56f, -140f);

            // Help text under a menu: what the selected row does, and the last thing that happened.
            // Sits in the lower part of the body area, below where menu rows reach.
            _note = MakeText("Note", _root, 30, FontStyle.Normal, TextAnchor.LowerLeft,
                new Color(0.72f, 0.74f, 0.8f));
            _note.rectTransform.anchorMin = new Vector2(0f, 0f);
            _note.rectTransform.anchorMax = new Vector2(1f, 0f);
            _note.rectTransform.pivot = new Vector2(0.5f, 0f);
            _note.rectTransform.sizeDelta = new Vector2(-112f, MenuNoteHeightPx);
            _note.rectTransform.anchoredPosition = new Vector2(0f, 118f);
            _note.lineSpacing = 1.15f;

            _footer = MakeText("Footer", _root, 34, FontStyle.Bold, TextAnchor.LowerLeft,
                new Color(0.65f, 0.85f, 1f));
            _footer.rectTransform.anchorMin = new Vector2(0f, 0f);
            _footer.rectTransform.anchorMax = new Vector2(1f, 0f);
            _footer.rectTransform.pivot = new Vector2(0.5f, 0f);
            _footer.rectTransform.sizeDelta = new Vector2(-112f, 90f);
            _footer.rectTransform.anchoredPosition = new Vector2(0f, 28f);

            SetVisible(false);
        }

        // --- Content ------------------------------------------------------------------------

        /// <summary>Show a block of prose. Clears any stat rows from a previous screen.</summary>
        public void Show(string title, string body, string footer)
        {
            ClearStatRows();
            _body.gameObject.SetActive(true);
            _note.gameObject.SetActive(false);
            _title.text = title;
            _body.text = body;
            _footer.text = footer;
            Recentre(snap: !_visible);
            SetVisible(true);
        }

        /// <summary>
        /// Show label/value rows instead of prose. Built as paired left- and right-aligned labels
        /// because the built-in font is proportional, so column alignment by padding does not work.
        /// </summary>
        public void ShowStats(string title, IReadOnlyList<(string Label, string Value)> rows, string footer)
        {
            ClearStatRows();
            _body.gameObject.SetActive(false);
            _note.gameObject.SetActive(false);
            _title.text = title;
            _footer.text = footer;

            const float rowHeight = 62f;
            for (int i = 0; i < rows.Count; i++)
            {
                var rt = MakeRow($"Stat{i}", i, rowHeight);

                var label = MakeText("Label", rt, 38, FontStyle.Normal, TextAnchor.MiddleLeft,
                    new Color(0.78f, 0.78f, 0.82f));
                Stretch(label.rectTransform, Vector4.zero);
                label.text = rows[i].Label;

                var value = MakeText("Value", rt, 40, FontStyle.Bold, TextAnchor.MiddleRight, Color.white);
                Stretch(value.rectTransform, Vector4.zero);
                value.text = rows[i].Value;
            }

            Recentre(snap: !_visible);
            SetVisible(true);
        }

        /// <summary>
        /// Show a list of settings, one highlighted as the current selection. Used by the setup
        /// screen; the panel itself holds no selection state, it just draws what it is given, so
        /// the screen stays the single owner of what is selected and what each row means.
        /// </summary>
        public void ShowMenu(string title, IReadOnlyList<MenuRow> rows, string note, string footer)
        {
            ClearStatRows();
            _body.gameObject.SetActive(false);
            _note.gameObject.SetActive(true);
            _title.text = title;
            _note.text = note;
            _footer.text = footer;

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var rt = MakeRow($"Menu{i}", i, MenuRowHeightPx);

                if (row.Selected)
                {
                    // Drawn first so it sits behind the two labels: sibling order is paint order.
                    var highlight = MakeImage("Highlight", rt, new Color(1f, 0.45f, 0.12f, 0.22f));
                    Stretch(highlight, new Vector4(-14f, 2f, -14f, 2f));
                }

                Color labelColor = row.Dimmed
                    ? new Color(0.45f, 0.45f, 0.5f)
                    : row.Selected ? new Color(1f, 0.82f, 0.55f) : new Color(0.78f, 0.78f, 0.82f);
                Color valueColor = row.Dimmed ? new Color(0.5f, 0.5f, 0.55f) : Color.white;

                var label = MakeText("Label", rt, 36, row.Selected ? FontStyle.Bold : FontStyle.Normal,
                    TextAnchor.MiddleLeft, labelColor);
                Stretch(label.rectTransform, Vector4.zero);
                // ">" rather than a nicer arrow glyph: the built-in font is whatever the platform
                // ships, and a missing glyph renders as nothing at all.
                label.text = (row.Selected ? "> " : "   ") + row.Label;

                var value = MakeText("Value", rt, 36, FontStyle.Bold, TextAnchor.MiddleRight, valueColor);
                Stretch(value.rectTransform, Vector4.zero);
                value.text = row.Value;
            }

            Recentre(snap: !_visible);
            SetVisible(true);
        }

        /// <summary>One top-anchored row of a stats or menu list, tracked for later cleanup.</summary>
        RectTransform MakeRow(string name, int index, float rowHeight)
        {
            var row = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)row.transform;
            rt.SetParent(_statsRoot, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, rowHeight);
            rt.anchoredPosition = new Vector2(0f, -index * rowHeight);
            _statRows.Add(row);
            return rt;
        }

        public void Hide() => SetVisible(false);

        void ClearStatRows()
        {
            foreach (var row in _statRows)
                if (row != null) Destroy(row);
            _statRows.Clear();
        }

        /// <summary>
        /// Hides by disabling the Canvas rather than deactivating children. Deactivating them would
        /// undo the per-screen setup - ShowStats switches the prose body off, and re-enabling every
        /// child would put it straight back on top of the stat rows.
        /// </summary>
        void SetVisible(bool visible)
        {
            _visible = visible;
            _canvas.enabled = visible;
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

        static RectTransform MakeImage(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
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
