using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

namespace APIs.Hyprland
{
    public sealed class CompanionInputMask
    {
        readonly byte[] pixels;
        readonly int width, height;
        public CompanionInputMask(byte[] pixels, int width, int height)
        {
            this.pixels = pixels; this.width = width; this.height = height;
        }
        public bool Contains(Vector2 local, Vector2Int windowSize)
        {
            if (pixels == null || width <= 0 || height <= 0 || windowSize.x <= 0 || windowSize.y <= 0 ||
                local.x < 0 || local.y < 0 || local.x >= windowSize.x || local.y >= windowSize.y) return false;
            int x = Mathf.FloorToInt(local.x * width / windowSize.x);
            int y = Mathf.FloorToInt(local.y * height / windowSize.y);
            return pixels[(y * width + x) * 4 + 3] > 10;
        }
    }

    public sealed class CompanionMotionLease
    {
        object owner;
        public bool Owned => owner != null;
        public bool IsOwner(object value) => ReferenceEquals(owner, value) && value != null;
        public bool TryAcquire(object value)
        {
            if (value == null || owner != null && !ReferenceEquals(owner, value)) return false;
            owner = value;
            return true;
        }
        public bool Release(object value)
        {
            if (!IsOwner(value)) return false;
            owner = null;
            return true;
        }
    }

    public sealed class CompanionKeyboardOwnership
    {
        public bool Owned { get; private set; }
        public bool Pending { get; private set; }
        public string PreviousWindow { get; private set; }
        public int PreviousPid { get; private set; }

        public void Begin(string previousWindow, int previousPid)
        {
            Owned = Pending = true;
            PreviousWindow = previousWindow;
            PreviousPid = previousPid;
        }

        public void ObserveExternal()
        {
            // Event and command sockets are unordered. Only a fresh observation can discard the restore target.
            if (Owned) Pending = true;
        }

        public void Reconcile(bool companionActive)
        {
            Pending = false;
            if (!companionActive) Clear();
        }

        public void ObservationFailed() { if (Owned) Pending = true; }

        public void Clear()
        {
            Owned = Pending = false;
            PreviousWindow = null;
            PreviousPid = 0;
        }
    }

    public static class CompanionPresencePolicy
    {
        public static readonly string OwnedAuditionWorkspace = AuthorizeAuditionWorkspace();
        static string AuthorizeAuditionWorkspace()
        {
            if (Environment.GetEnvironmentVariable("MATEENGINE_COMPANION_AUDITION") != "1") return null;
            var key = Environment.GetEnvironmentVariable("OMP_GUI_KEY");
            if (string.IsNullOrWhiteSpace(key)) return null;
            try
            {
                foreach (var line in File.ReadAllLines("/proc/self/cgroup"))
                    if (line.StartsWith("0::", StringComparison.Ordinal))
                        foreach (var part in line.Substring(3).Split('/'))
                        {
                            const string prefix = "omp-heavy-job-", suffix = ".scope";
                            if (part.Length != prefix.Length + 32 + suffix.Length ||
                                !part.StartsWith(prefix, StringComparison.Ordinal) || !part.EndsWith(suffix, StringComparison.Ordinal)) continue;
                            bool valid = true;
                            for (int i = prefix.Length; i < prefix.Length + 32; i++)
                                if (!(part[i] >= '0' && part[i] <= '9') && !(part[i] >= 'a' && part[i] <= 'f')) { valid = false; break; }
                            if (valid) return "special:omp-" + key;
                        }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        public static int EffectiveFrames(int requested, bool governed, bool active, bool hidden, float now, float lastActive)
        {
            requested = Mathf.Clamp(requested, 15, 165);
            if (!governed || active || now - lastActive < 2f) return requested;
            return Mathf.Min(requested, hidden ? 5 : 60);
        }

        public static float PortraitViewportScale(Vector2Int windowSize, RectInt output) =>
            Mathf.Max(1f, Mathf.Max(windowSize.x / (float)Mathf.Max(1, output.width),
                windowSize.y / (float)Mathf.Max(1, output.height)));

        public static bool PointerEligible(bool presentable, bool passive, bool specialWorkspace) =>
            presentable && !passive && !specialWorkspace;

        public static bool KeyboardEligible(bool deliberate, bool presentable, bool specialWorkspace) =>
            deliberate && presentable && !specialWorkspace;

        public static bool KeyboardRequestCurrent(bool requested, long requestedSerial, long observedSerial) =>
            requested && requestedSerial == observedSerial;

        public static Vector2Int RestorePosition(Vector2Int origin, RectInt oldArea, RectInt area, Rect body)
        {
            var position = origin;
            if (area != oldArea)
            {
                var fraction = new Vector2((origin.x + body.center.x - oldArea.x) / Mathf.Max(1, oldArea.width),
                    (origin.y + body.center.y - oldArea.y) / Mathf.Max(1, oldArea.height));
                position = Vector2Int.RoundToInt(new Vector2(area.x + Mathf.Clamp01(fraction.x) * area.width,
                    area.y + Mathf.Clamp01(fraction.y) * area.height) - body.center);
            }
            float minX = area.xMin + 12 - body.xMin, maxX = area.xMax - 12 - body.xMax;
            float minY = area.yMin + 12 - body.yMin, maxY = area.yMax - 12 - body.yMax;
            position.x = Mathf.RoundToInt(minX <= maxX ? Mathf.Clamp(position.x, minX, maxX) : (minX + maxX) * .5f);
            position.y = Mathf.RoundToInt(minY <= maxY ? Mathf.Clamp(position.y, minY, maxY) : (minY + maxY) * .5f);
            return position;
        }

        public static bool SupportFits(RectInt support, RectInt workArea, float bodyWidth, float headroom) =>
            bodyWidth > 0 && headroom > 0 && support.width >= bodyWidth + 24 && support.height > 0 &&
            support.xMax > workArea.xMin + bodyWidth && support.xMin < workArea.xMax - bodyWidth &&
            support.yMin >= workArea.yMin + headroom + 12 && support.yMin < workArea.yMax - 24;

        public static bool BodyFits(RectInt area, Vector2Int position, Rect body) =>
            body.width > 0 && body.height > 0 && position.x + body.xMin >= area.xMin + 11 &&
            position.x + body.xMax <= area.xMax - 11 && position.y + body.yMin >= area.yMin + 11 &&
            position.y + body.yMax <= area.yMax - 11;

        public static bool SeatClientEligible(HyprlandClient support, HyprlandClient companion, HyprlandMonitor monitor,
            bool fresh, string ownedWorkspace)
        {
            if (!fresh || support == null || companion == null || monitor == null || !support.mapped || support.hidden ||
                support.address == companion.address || support.pid == companion.pid || support.fullscreen != 0 ||
                support.monitor != companion.monitor || support.monitor != monitor.id || monitor.disabled || !monitor.dpmsStatus ||
                support.workspace == null) return false;
            bool owned = ownedWorkspace != null && support.workspace.name == ownedWorkspace &&
                companion.workspace?.name == ownedWorkspace;
            if (support.workspace.IsSpecial || companion.workspace?.IsSpecial == true) return owned;
            return support.pinned || support.workspace.Matches(monitor.activeWorkspace);
        }

        public static bool PortraitPending(string phase) =>
            phase == "approach" || phase == "waiting" || phase == "strong" || phase == "bigscreen";
    }

    public sealed class CompanionPortraitEpisodes
    {
        readonly HashSet<string> immediate = new HashSet<string>(), timed = new HashSet<string>();
        readonly Queue<string> order = new Queue<string>();
        public bool CanClaim(string token, bool escalation) =>
            !string.IsNullOrEmpty(token) && !(escalation ? timed : immediate).Contains(token);
        public bool TryClaim(string token, bool escalation)
        {
            if (!CanClaim(token, escalation)) return false;
            if (!immediate.Contains(token) && !timed.Contains(token))
            {
                order.Enqueue(token);
                if (order.Count > 64) { var old = order.Dequeue(); immediate.Remove(old); timed.Remove(old); }
            }
            if (escalation)
            {
                bool claimed = timed.Add(token);
                immediate.Add(token);
                return claimed;
            }
            return immediate.Add(token);
        }
    }
}
