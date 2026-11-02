using System;
using System.Collections.Generic;
using UnityEngine;

namespace APIs.Hyprland
{
    public enum CompanionFramePose { Stock, Dangle, Lie, Watch }

    public static class CompanionFramePolicy
    {
        public static int EdgeY(RectInt rect, bool bottom) => bottom ? rect.yMax : rect.yMin;
        public static bool SupportFits(RectInt rect, RectInt area, float width, float height, bool bottom) =>
            CompanionPresencePolicy.SupportFits(new RectInt(rect.x, EdgeY(rect, bottom), rect.width, rect.height), area, width, height);

        public static bool TryDrop(RectInt rect, Vector2 contact, Vector2 grip, float radius, out bool bottom, out float x)
        {
            float upper = Mathf.Min(Mathf.Abs(contact.y - rect.yMin), Mathf.Abs(grip.y - rect.yMin));
            float lower = Mathf.Min(Mathf.Abs(contact.y - rect.yMax), Mathf.Abs(grip.y - rect.yMax));
            bottom = lower < upper;
            float edge = bottom ? rect.yMax : rect.yMin;
            var selected = Mathf.Abs(grip.y - edge) <= Mathf.Abs(contact.y - edge) ? grip : contact;
            x = Mathf.Clamp(selected.x, rect.xMin, rect.xMax);
            return rect.width > 0 && rect.height > 0 && selected.x >= rect.xMin - radius &&
                selected.x <= rect.xMax + radius && Mathf.Min(upper, lower) <= radius;
        }
    }

    public static class CompanionContextPolicy
    {
        static readonly HashSet<string> Browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "zen", "zen-browser", "firefox", "org.mozilla.firefox", "chromium", "chromium-browser", "google-chrome", "brave-browser"
        };
        static readonly HashSet<string> Telegram = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "org.telegram.desktop", "telegramdesktop", "telegram-desktop", "ayugram", "ayugramdesktop", "com.ayugram.desktop"
        };

        public static bool IsBrowser(string app) => app != null && Browsers.Contains(app);
        public static bool IsTelegram(string app) => app != null && Telegram.Contains(app);

    }
}
