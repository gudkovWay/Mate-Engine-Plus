using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace MateEngine.Desktop
{
    internal sealed class CompanionRadialSkin : MonoBehaviour
    {
        Xamin.CircleSelector selector;
        Transform buttons;
        Image background, cursor;
        Text caption;
        RectTransform captionPlate;
        GameObject pressed;
        readonly List<Image> discs = new List<Image>();
        static Sprite discSprite, ringSprite;
        internal string ReleasedId;
        internal Vector2 ReleasedMouse;
        internal int ReleaseFrame;
        internal string PressedId { get { return pressed == null ? "" : pressed.GetComponent<Xamin.Button>().id; } }
        internal void Initialize(Xamin.CircleSelector value)
        {
            selector = value; buttons = transform.Find("Buttons");
            background = transform.Find("Background").GetComponent<Image>();
            cursor = transform.Find("Cursor").GetComponent<Image>();
            if (discSprite == null) discSprite = Circle(false);
            if (ringSprite == null) ringSprite = Circle(true);
            background.gameObject.SetActive(false);
            cursor.gameObject.SetActive(true); cursor.raycastTarget = false; cursor.color = Color.clear;
            transform.Find("Separators").gameObject.SetActive(false);
            var ring = CompanionUi.Rect("Companion ring", transform); ring.SetAsFirstSibling();
            ring.anchorMin = ring.anchorMax = ring.pivot = new Vector2(.5f, .5f);
            ring.anchoredPosition = Vector2.zero; ring.sizeDelta = new Vector2(240, 240);
            background = ring.gameObject.AddComponent<Image>(); background.sprite = ringSprite; background.raycastTarget = false;
            captionPlate = CompanionUi.Panel("Companion caption", transform);
            captionPlate.GetComponent<Image>().raycastTarget = false;
            captionPlate.anchorMin = captionPlate.anchorMax = captionPlate.pivot = new Vector2(.5f, .5f);
            captionPlate.anchoredPosition = new Vector2(0, -181); captionPlate.sizeDelta = new Vector2(320, 44);
            caption = CompanionUi.Text(captionPlate, "", 14);
            caption.alignment = TextAnchor.MiddleCenter;
            CompanionUi.Fill(caption.rectTransform, 10, 8, 10, 8);
        }
        void LateUpdate()
        {
            if (selector == null || transform.localScale.sqrMagnitude < .0001f) return;
            if (Input.GetMouseButtonUp(0))
            {
                ReleasedId = selector.SelectedSegment == null ? "" : selector.SelectedSegment.GetComponent<Xamin.Button>().id;
                ReleasedMouse = Input.mousePosition; ReleaseFrame = Time.frameCount;
            }
            background.color = Color.white; cursor.color = Color.clear;
            transform.Find("Separators").gameObject.SetActive(false);
            for (int i = 0; i < buttons.childCount; i++)
            {
                var item = buttons.GetChild(i);
                if (item.Find("Companion disc") != null) continue;
                var rect = CompanionUi.Rect("Companion disc", item); rect.SetAsFirstSibling();
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
                rect.anchoredPosition = Vector2.zero; rect.sizeDelta = new Vector2(70, 70);
                var image = rect.gameObject.AddComponent<Image>(); image.sprite = discSprite; image.raycastTarget = true;
                discs.Add(image);
                var original = item.GetComponent<Image>();
                var glyph = CompanionUi.Rect("Companion glyph", item);
                glyph.anchorMin = glyph.anchorMax = glyph.pivot = new Vector2(.5f, .5f);
                glyph.anchoredPosition = Vector2.zero; glyph.sizeDelta = new Vector2(40, 40);
                var ink = glyph.gameObject.AddComponent<Image>();
                ink.sprite = item.GetComponent<Xamin.Button>().image; ink.color = CompanionUi.Ink; ink.raycastTarget = false;
                original.enabled = false;
            }
            for (int i = discs.Count - 1; i >= 0; i--)
            {
                var image = discs[i];
                if (image == null) { discs.RemoveAt(i); continue; }
                var button = image.transform.parent.GetComponent<Xamin.Button>();
                bool available = button != null && button.unlocked;
                image.color = !available ? new Color(.48f, .48f, .48f, 1) : selector.SelectedSegment == image.transform.parent.gameObject ? CompanionUi.Accent : Color.white;
                var glyph = image.transform.parent.Find("Companion glyph");
                if (glyph != null) glyph.GetComponent<Image>().color = available ? CompanionUi.Ink : CompanionUi.Muted;
            }
            string label = "";
            var selected = selector.SelectedSegment == null ? null : selector.SelectedSegment.GetComponent<Xamin.Button>();
            if (selected != null)
            {
                var entry = selected.GetComponent<DesktopMenuAction>();
                if (entry != null && !string.IsNullOrEmpty(entry.caption)) label = entry.caption;
                else
                switch (selected.id)
                {
                    case "omp.inbox": label = "OMP · агенты и ответы"; break;
                    case "companion.avatar": label = "Персонаж"; break;
                    case "settings": label = "Настройки MateEngine"; break;
                    case "companion.home": label = "Домой"; break;
                    case "companion.actions": label = "Быстрые действия"; break;
                    case "companion.behaviour": label = "Поведение и тишина"; break;
                    case "companion.back": label = "Назад к ежедневным действиям"; break;
                    default: label = selected.id; break;
                }
            }
            if (caption.text != label)
            {
                caption.text = label;
                captionPlate.sizeDelta = new Vector2(320, Mathf.Max(36, caption.preferredHeight + 16));
                captionPlate.gameObject.SetActive(!string.IsNullOrEmpty(label));
            }
            captionPlate.localRotation = Quaternion.Inverse(transform.localRotation);
        }
        internal bool AllowInput()
        {
            if (selector.controlType != Xamin.CircleSelector.ControlType.mouseAndTouch) return true;
            var system = UnityEngine.EventSystems.EventSystem.current;
            var input = system == null || system.currentInputModule == null ? null :
                system.currentInputModule.inputOverride as CompanionPointerInput;
            if (input != null && WindowManager.Instance?.CompanionManager != null)
            {
                var hit = HitButton();
                if (hit != null) selector.SelectedSegment = hit;
                if (input.GetMouseButtonDown(0)) pressed = hit;
                if (input.GetMouseButtonUp(0))
                {
                    var button = hit == null || hit != pressed ? null : hit.GetComponent<Xamin.Button>();
                    pressed = null;
                    if (button != null && button.unlocked)
                    {
                        button.ExecuteAction();
                        var audio = UnityEngine.Object.FindFirstObjectByType<MenuAudioHandler>();
                        if (audio != null) audio.PlayButtonSound();
                        selector.Close();
                    }
                }
                return false;
            }
            if (Input.GetMouseButtonDown(0)) pressed = HitButton();
            if (!Input.GetMouseButtonUp(0)) return true;
            var released = HitButton();
            bool accepted = pressed != null && pressed == released;
            pressed = null;
            return accepted;
        }
        GameObject HitButton()
        {
            var native = WindowManager.Instance?.CompanionManager != null;
            var pointer = native ? MateEngine.Shoji.ShojiBridge.GetUnityMousePosition() : Input.mousePosition;
            var canvas = selector.GetComponentInParent<Canvas>();
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            for (int i = 0; i < (native ? buttons.childCount : 1); i++)
            {
                var selected = native ? buttons.GetChild(i).gameObject : selector.SelectedSegment;
                if (selected == null) continue;
                var rect = selected.GetComponent<RectTransform>();
                Vector2 local;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, pointer, camera, out local) &&
                    (local - rect.rect.center).sqrMagnitude <= 35 * 35) return selected;
            }
            return null;
        }
        static Sprite Circle(bool ring)
        {
            const int size = 256;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                float distance = Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(size / 2f, size / 2f));
                float edge = Mathf.Clamp01(size / 2f - 1 - distance);
                Color color = CompanionUi.Bronze;
                if (ring) color.a = edge * Mathf.Clamp01(distance - (size / 2f - 3));
                else
                {
                    color = Color.Lerp(CompanionUi.Surface, CompanionUi.Bronze, Mathf.Clamp01(distance - (size / 2f - 5)));
                    color.a = edge;
                }
                pixels[y * size + x] = color;
            }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.SetPixels32(pixels); texture.Apply(false, true); texture.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(.5f, .5f), size);
        }
    }
}

namespace MateEngine.Shoji
{
    public static partial class ShojiBridge
    {
        public static bool AllowRadialInput(Xamin.CircleSelector selector)
        {
            var skin = selector.GetComponent<MateEngine.Desktop.CompanionRadialSkin>();
            return skin == null || skin.AllowInput();
        }
    }
}
