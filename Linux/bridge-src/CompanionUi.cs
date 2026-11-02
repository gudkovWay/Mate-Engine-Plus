using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MateEngine.Desktop
{
    internal static class CompanionUi
    {
        internal static readonly Color Surface = new Color(.065f, .073f, .083f, .99f);
        internal static readonly Color Raised = new Color(.105f, .118f, .13f, 1);
        internal static readonly Color Ink = new Color(.94f, .92f, .87f, 1);
        internal static readonly Color Muted = new Color(.69f, .72f, .73f, 1);
        internal static readonly Color Bronze = new Color(.48f, .39f, .29f, 1);
        internal static readonly Color Accent = new Color(.73f, .64f, .84f, 1);
        static Font font, mono;
        static Sprite rounded;
        static Sprite Rounded
        {
            get
            {
                if (rounded != null) return rounded;
                const int edge = 24, radius = 6;
                var texture = new Texture2D(edge, edge, TextureFormat.RGBA32, false);
                var pixels = new Color32[edge * edge];
                for (int y = 0; y < edge; y++) for (int x = 0; x < edge; x++)
                {
                    float dx = Mathf.Max(radius - x - .5f, x + .5f - (edge - radius));
                    float dy = Mathf.Max(radius - y - .5f, y + .5f - (edge - radius));
                    float distance = Mathf.Sqrt(Mathf.Max(0, dx) * Mathf.Max(0, dx) + Mathf.Max(0, dy) * Mathf.Max(0, dy));
                    pixels[y * edge + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(255 * Mathf.Clamp01(radius + .5f - distance)));
                }
                texture.SetPixels32(pixels); texture.Apply(false, true); texture.wrapMode = TextureWrapMode.Clamp;
                rounded = Sprite.Create(texture, new Rect(0, 0, edge, edge), new Vector2(.5f, .5f), 100, 0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
                return rounded;
            }
        }
        internal static Font Font { get { return font ?? (font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")); } }
        internal static Font Mono { get { return mono ?? (mono = UnityEngine.Font.CreateDynamicFontFromOSFont("DejaVu Sans Mono", 14)); } }
        internal static RectTransform Rect(string name, Transform parent)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>(); rect.SetParent(parent, false); return rect;
        }
        internal static void Fill(RectTransform rect, float left = 0, float bottom = 0, float right = 0, float top = 0)
        {
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = new Vector2(left, bottom); rect.offsetMax = new Vector2(-right, -top);
        }
        internal static void Top(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1); rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height);
        }
        internal static Image Background(RectTransform rect, Color color, bool raycast = true)
        {
            var image = rect.gameObject.AddComponent<Image>(); image.sprite = Rounded; image.type = Image.Type.Sliced;
            image.color = color; image.raycastTarget = raycast; return image;
        }
        internal static RectTransform Panel(string name, Transform parent)
        {
            var rect = Rect(name, parent); Background(rect, Surface);
            var border = rect.gameObject.AddComponent<Outline>(); border.effectDistance = new Vector2(1, -1); border.effectColor = Bronze; return rect;
        }
        internal static LayoutElement Size(GameObject value, float height, float width = -1)
        {
            var layout = value.AddComponent<LayoutElement>(); layout.minHeight = layout.preferredHeight = height;
            if (width >= 0) layout.minWidth = layout.preferredWidth = width; return layout;
        }
        internal static Text Text(Transform parent, string value, int size = 15, Color? color = null)
        {
            var rect = Rect("Label", parent); var text = rect.gameObject.AddComponent<Text>(); text.font = Font; text.fontSize = size;
            text.color = color ?? Ink; text.supportRichText = false; text.raycastTarget = false; text.text = value;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate; return text;
        }
        internal static Button Button(Transform parent, string label, UnityAction action)
        {
            var rect = Rect(label, parent); Background(rect, Color.white); var button = rect.gameObject.AddComponent<Button>();
            var colors = button.colors; colors.normalColor = Raised; colors.highlightedColor = new Color(.2f, .19f, .22f); colors.selectedColor = new Color(.25f, .22f, .3f);
            colors.pressedColor = new Color(.31f, .27f, .36f); colors.disabledColor = new Color(.09f, .1f, .11f); colors.fadeDuration = .08f; button.colors = colors;
            button.onClick.AddListener(action); var text = Text(rect, label); text.alignment = TextAnchor.MiddleCenter; Fill(text.rectTransform, 9, 3, 9, 3);
            Size(rect.gameObject, 36); return button;
        }
        internal static InputField Input(Transform parent, string placeholder, bool readOnly = false)
        {
            var rect = Rect(readOnly ? "Selectable message" : "Reply composer", parent); var image = Background(rect, readOnly ? new Color(0, 0, 0, 0) : Raised);
            var text = Text(rect, "", 15); text.raycastTarget = true; text.verticalOverflow = VerticalWrapMode.Overflow; Fill(text.rectTransform, 10, 8, 10, 8);
            var field = rect.gameObject.AddComponent<InputField>(); field.targetGraphic = image; field.textComponent = text; field.readOnly = readOnly;
            if (!readOnly)
            {
                var border = rect.gameObject.AddComponent<Outline>(); border.effectColor = new Color(Bronze.r, Bronze.g, Bronze.b, .8f); border.effectDistance = new Vector2(1, -1);
            }
            field.lineType = InputField.LineType.MultiLineNewline; field.characterLimit = readOnly ? 0 : 32768; field.selectionColor = new Color(.46f, .37f, .59f, .7f);
            field.caretColor = Accent; field.customCaretColor = true;
            if (!readOnly) { var hint = Text(rect, placeholder, 15, Muted); Fill(hint.rectTransform, 10, 8, 10, 8); field.placeholder = hint; }
            return field;
        }
        internal static ScrollRect Scroll(Transform parent, out RectTransform content)
        {
            var root = Rect("Messages", parent); var scroll = root.gameObject.AddComponent<ScrollRect>(); scroll.horizontal = false;
            Background(root, new Color(.08f, .095f, .105f, .96f));
            var border = root.gameObject.AddComponent<Outline>(); border.effectColor = new Color(Bronze.r, Bronze.g, Bronze.b, .45f); border.effectDistance = new Vector2(1, -1);
            scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 30;
            var viewport = Rect("Viewport", root); Fill(viewport); Background(viewport, new Color(0, 0, 0, .001f)); viewport.gameObject.AddComponent<RectMask2D>();
            content = Rect("Content", viewport); content.anchorMin = new Vector2(0, 1); content.anchorMax = Vector2.one; content.pivot = new Vector2(.5f, 1); content.sizeDelta = Vector2.zero;
            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>(); layout.padding = new RectOffset(6, 6, 8, 8); layout.spacing = 9;
            layout.childControlHeight = layout.childControlWidth = true; layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = viewport; scroll.content = content; return scroll;
        }
        internal static void Clear(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--) { var item = parent.GetChild(i).gameObject; item.SetActive(false); UnityEngine.Object.Destroy(item); }
        }
        internal static string Short(string value, int limit)
        {
            if (string.IsNullOrEmpty(value)) return ""; return value.Length <= limit ? value : value.Substring(0, limit - 1) + "…";
        }
        internal static string SessionTitle(CompanionSession session)
        {
            if (!string.IsNullOrWhiteSpace(session.title)) return Short(session.title, 36);
            string path = (session.cwd ?? "").TrimEnd('/', '\\'); int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            return path.Length > 0 ? Short(path.Substring(slash + 1), 30) : "OMP · " + Short(session.sessionId, 8);
        }
    }
}
