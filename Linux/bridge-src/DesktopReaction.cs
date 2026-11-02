using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using APIs.Hyprland;

namespace MateEngine.Desktop
{
    internal static class CompanionAudition
    {
        static readonly string workspace = CompanionPresencePolicy.OwnedAuditionWorkspace;

        public static bool Allows(HyprlandManager manager)
        {
            if (workspace == null || manager == null) return false;
            var window = manager.CompanionWindow;
            return window != null && window.workspace != null && window.workspace.name == workspace &&
                manager.SurfacePresentableForOwnedWorkspace(workspace);
        }
    }

    [DefaultExecutionOrder(10000)]
    internal sealed class DesktopReaction : MonoBehaviour
    {
        public bool Enabled = true;
        public bool Presentable { get; private set; }
        public string Caption { get; private set; }
        public string PinnedMonitor { get { return pin; } }
        internal CompanionMotionPhase CurrentPhase { get { return motion == null ? CompanionMotionPhase.Idle : motion.Journey.Phase; } }
        AttentionView view;
        bool fresh, interacting, dragging, wasDragging, initialized;
        float nextLookup, candidateSince, pausedUntil;
        string homeMonitor, pin, candidate, stableMonitor, activeToken, output;
        Vector2 homeAnchor;
        RectInt outputArea;
        Vector2Int windowSize;
        Animator animator;
        CompanionLocomotion motion;
        readonly HashSet<string> handled = new HashSet<string>();
        readonly Queue<string> handledOrder = new Queue<string>();
        AvatarBigScreenHandler portrait;
        bool saveHomeAfterPortrait;
        readonly CompanionPortraitEpisodes portraits = new CompanionPortraitEpisodes();
        DesktopVision vision;
        CustomDancePlayer.AvatarDanceHandler[] manualDances;
        public bool ContextEnabled { get; private set; }
        public string ContextCaption { get { return vision == null ? "" : vision.Error ?? ""; } }
        internal string ContextState => vision == null ? "None" : vision.Choreography.Active.ToString();
        internal DesktopVision Vision => vision;

        public void ToggleContext()
        {
            ContextEnabled = !ContextEnabled;
            PlayerPrefs.SetInt("Companion.ContextEnabled", ContextEnabled ? 1 : 0);
            PlayerPrefs.Save();
            Cancel();
        }


        internal bool PortraitActive => portrait != null && portrait.IsAutomaticActive;
        internal string PortraitMonitor => portrait == null ? "" : portrait.AutomaticMonitor;
        internal AvatarWindowHandler Seating => animator == null ? null : animator.GetComponent<AvatarWindowHandler>();
        internal AvatarBigScreenHandler Portrait => portrait;

        bool BeginPortrait(HyprlandManager manager, HyprlandMonitor selected)
        {
            if (portrait == null || portrait.IsBigScreenActive || portrait.IsTransitioning || selected == null) return false;
            if (!portraits.CanClaim(view.token, view.phase == "bigscreen")) return false;
            motion.Cancel();
            if (animator != null)
            {
                var seat = animator.GetComponent<AvatarWindowHandler>();
                if (seat != null) seat.ForceExitWindowSitting();
            }
            manager.ReleaseMotion(this);
            if (!portrait.TryBeginAutomatic(selected.name)) return false;
            portraits.TryClaim(view.token, view.phase == "bigscreen");
            Remember(view.token);
            activeToken = view.token;
            return true;
        }

        void Awake()
        {
            motion = gameObject.AddComponent<CompanionLocomotion>();
            ContextEnabled = PlayerPrefs.GetInt("Companion.ContextEnabled", 1) != 0;
            vision = new DesktopVision();
        }

        public void SetAttention(AttentionView value, bool connected, bool held, bool menu)
        {
            view = value;
            fresh = connected;
            dragging = held || WindowManager.Instance?.CompanionManager?.IsDragging == true;
            interacting = dragging || menu || TutorialMenu.IsActive;
        }

        HyprlandMonitor Monitor(HyprlandManager manager, string name)
        {
            if (manager.Monitors == null) return null;
            HyprlandMonitor fallback = null;
            foreach (var item in manager.Monitors)
            {
                if (item.disabled || item.width <= 0 || item.height <= 0) continue;
                if (item.name == name) return item;
                if (fallback == null || item.id == manager.CompanionWindow.monitor) fallback = item;
            }
            return fallback;
        }

        void Remember(string token)
        {
            if (string.IsNullOrEmpty(token) || !handled.Add(token)) return;
            handledOrder.Enqueue(token);
            if (handledOrder.Count > 64) handled.Remove(handledOrder.Dequeue());
        }

        void SaveHome(HyprlandManager manager)
        {
            var monitor = Monitor(manager, output ?? homeMonitor);
            Vector2 anchor;
            if (monitor == null || !motion.TryGetFootAnchor(animator, Camera.main, manager.CompanionWindow.sizeVector, out anchor)) return;
            var area = monitor.workArea;
            anchor += (Vector2)manager.GetWindowPosition();
            homeMonitor = monitor.name;
            homeAnchor = new Vector2(Mathf.Clamp01((anchor.x - area.x) / area.width), Mathf.Clamp01((anchor.y - area.y) / area.height));
            PlayerPrefs.SetString("Companion.HomeMonitor", homeMonitor);
            PlayerPrefs.SetFloat("Companion.HomeX", homeAnchor.x);
            PlayerPrefs.SetFloat("Companion.HomeY", homeAnchor.y);
            PlayerPrefs.Save();
        }

        public void Home()
        {
            pausedUntil = Time.unscaledTime + 30f;
            Cancel();
            if (PortraitActive) return;
            // Home is an explicit stationary reset, never a reverse attention shuttle.
            var wm = WindowManager.Instance;
            var manager = wm == null ? null : wm.CompanionManager;
            if (manager == null || manager.CompanionWindow == null) return;
            var monitor = Monitor(manager, homeMonitor);
            Vector2 anchor;
            if (monitor == null || monitor.id != manager.CompanionWindow.monitor ||
                !motion.TryGetFootAnchor(animator, Camera.main, manager.CompanionWindow.sizeVector, out anchor)) return;
            var area = monitor.workArea;
            manager.SetCompanionPosition(Vector2Int.RoundToInt(new Vector2(area.x + area.width * homeAnchor.x,
                area.y + area.height * homeAnchor.y) - anchor));
        }

        public void ToggleMonitorPin()
        {
            var wm = WindowManager.Instance;
            var manager = wm == null ? null : wm.CompanionManager;
            if (manager == null || manager.CompanionWindow == null) return;
            var monitor = Monitor(manager, stableMonitor ?? homeMonitor);
            pin = string.IsNullOrEmpty(pin) && monitor != null ? monitor.name : "";
            PlayerPrefs.SetString("Companion.AttentionMonitor", pin);
            PlayerPrefs.Save();
        }

        void Update()
        {
            if (Time.unscaledTime < nextLookup && animator != null && animator.isActiveAndEnabled) return;
            nextLookup = Time.unscaledTime + .5f;
            var receiver = Object.FindFirstObjectByType<AvatarAnimatorReceiver>();
            var next = receiver == null ? null : receiver.avatarAnimator;
            if (next != animator) Cancel();
            animator = next;
            manualDances = Object.FindObjectsByType<CustomDancePlayer.AvatarDanceHandler>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            portrait = null;
            foreach (var handler in AvatarBigScreenHandler.ActiveHandlers)
                if (handler != null && handler.avatarAnimator == animator) { portrait = handler; break; }
        }

        void LateUpdate()
        {
            var wm = WindowManager.Instance;
            var manager = wm == null ? null : wm.CompanionManager;
            bool audition = CompanionAudition.Allows(manager);
            Presentable = manager != null && (manager.SurfacePresentable || audition) && animator != null && animator.isHuman &&
                motion.Ready && !TutorialMenu.IsActive;
            Caption = fresh ? "" : "Источник статусов недоступен";
            if (manager == null || manager.CompanionWindow == null || manager.Monitors == null) { Cancel(); return; }
            if (saveHomeAfterPortrait && !PortraitActive && (portrait == null || !portrait.IsTransitioning))
            { saveHomeAfterPortrait = false; SaveHome(manager); }
            if (!initialized)
            {
                initialized = true;
                pin = PlayerPrefs.GetString("Companion.AttentionMonitor", "");
                homeMonitor = PlayerPrefs.GetString("Companion.HomeMonitor", "");
                homeAnchor = new Vector2(Mathf.Clamp01(PlayerPrefs.GetFloat("Companion.HomeX", .85f)),
                    Mathf.Clamp01(PlayerPrefs.GetFloat("Companion.HomeY", .86f)));
                if (string.IsNullOrEmpty(homeMonitor)) SaveHome(manager);
            }
            var foreground = manager.ForegroundWindow;
            bool observedForeground = false;
            if (foreground != null)
                foreach (var monitor in manager.Monitors)
                {
                    if (monitor.id != foreground.monitor || monitor.disabled || !monitor.dpmsStatus) continue;
                    observedForeground = true;
                    if (candidate != monitor.name) { candidate = monitor.name; candidateSince = Time.unscaledTime; }
                    else if (Time.unscaledTime - candidateSince >= 2f) stableMonitor = candidate;
                    break;
                }
            if (!observedForeground) candidate = stableMonitor = null;
            if (wasDragging && !dragging)
            {
                if (PortraitActive) saveHomeAfterPortrait = true; else SaveHome(manager);
                pausedUntil = Time.unscaledTime + 30f;
            }
            wasDragging = dragging;
            bool manualDance = false;
            if (manualDances != null)
                foreach (var dance in manualDances) if (dance != null && dance.IsPlaying) { manualDance = true; break; }
            bool attention = fresh && view != null && !view.quiet && view.items != null && view.items.Length > 0;
            bool visionEnabled = Enabled && ContextEnabled && Presentable && (view == null || !view.quiet) &&
                Time.unscaledTime >= pausedUntil;
            bool visionSuspended = interacting || manualDance || attention || motion.Leased || manager.CompanionMotion ||
                manager.SeatTransition || portrait != null && (portrait.IsBigScreenActive || portrait.IsTransitioning);
            if (vision != null && vision.Tick(animator, manager, visionEnabled, visionSuspended, audition)) return;
            if (!Enabled || !fresh || !Presentable || interacting || view != null && view.quiet || Time.unscaledTime < pausedUntil)
            {
                if (view != null && interacting) Remember(view.token);
                Cancel();
                return;
            }
            var selected = Monitor(manager, audition ? null : string.IsNullOrEmpty(pin) ? stableMonitor ?? homeMonitor : pin);
            bool pendingPortrait = view != null && !string.IsNullOrEmpty(view.token) &&
                CompanionPresencePolicy.PortraitPending(view.phase);
            if (PortraitActive)
            {
                if (!pendingPortrait || view.quiet || view.token != activeToken) Cancel();
                else Caption = view.caption;
                return;
            }
            // Timed escalation is independent of the initial walk and releases its controller first.
            if (pendingPortrait && !view.quiet && selected != null &&
                (view.phase == "bigscreen" || selected.id != manager.CompanionWindow.monitor))
            {
                if (BeginPortrait(manager, selected)) { Caption = view.caption; return; }
            }
            if (portrait != null && (portrait.IsBigScreenActive || portrait.IsTransitioning))
            {
                motion.Cancel();
                manager.ReleaseMotion(this);
                return;
            }
            if (motion.Leased)
            {
                var original = Monitor(manager, output);
                if (original == null || original.name != output || original.workArea != outputArea ||
                    manager.CompanionWindow.monitor != original.id || manager.CompanionWindow.sizeVector != windowSize)
                { Cancel(); return; }
                bool pending = view != null && (view.phase == "hold" || CompanionPresencePolicy.PortraitPending(view.phase));
                if (pending && view != null) Remember(view.token);
                if (!pending) motion.Viewed();
                motion.Tick(Time.unscaledDeltaTime);
                if (motion.Leased)
                {
                    manager.CompanionAnimationActive = motion.Journey.Phase != CompanionMotionPhase.Wait;
                    wm.SetPassiveInput(false);
                    var position = Vector2Int.RoundToInt(motion.Journey.Position);
                    if (manager.GetWindowPosition() != position) manager.SetCompanionPosition(position);
                    Caption = view == null ? "" : view.caption;
                    if (audition) motion.RecordAudition(manager, Camera.main);
                }
                else Cancel();
                return;
            }
            if (view == null || string.IsNullOrEmpty(view.token) || handled.Contains(view.token) ||
                (view.phase != "approach" && view.phase != "waiting" && view.phase != "strong") || selected == null) return;
            // Crossing a physical output gap is not a walk. Keep the current output until explicitly moved.
            if (selected.id != manager.CompanionWindow.monitor) return;
            Vector2 foot;
            if (!motion.TryGetFootAnchor(animator, Camera.main, manager.CompanionWindow.sizeVector, out foot)) return;
            var current = manager.GetWindowPosition();
            var body = manager.BodyBounds;
            var area = selected.workArea;
            float desiredX = area.x + area.width * (view.phase == "strong" ? .62f : .80f) - foot.x;
            float minX = area.xMin + 24f - body.xMin, maxX = area.xMax - 24f - body.xMax;
            if (minX > maxX) return;
            var destination = new Vector2(Mathf.Clamp(desiredX, minX, maxX), current.y);
            float stride = motion.StridePixels(animator, Camera.main, manager.CompanionWindow.sizeVector);
            var seatHandler = animator.GetComponent<AvatarWindowHandler>();
            if (seatHandler != null) seatHandler.ForceExitWindowSitting();
            if (stride <= 0) return;
            destination.x = current.x + Mathf.Clamp(destination.x - current.x, -2f * stride, 2f * stride);
            if (!manager.TryOwnMotion(this)) return;
            if (!motion.Begin(animator, current, destination, stride, audition)) { manager.ReleaseMotion(this); return; }
            activeToken = view.token;
            Remember(activeToken);
            output = selected.name;
            outputArea = area;
            windowSize = manager.CompanionWindow.sizeVector;
            manager.CompanionAnimationActive = true;
            wm.SetPassiveInput(false);
            if (audition) motion.RecordAudition(manager, Camera.main);
        }

        public void Cancel()
        {
            if (vision != null) vision.Suspend();
            if (portrait != null) portrait.CancelAutomatic();
            if (motion != null) motion.Cancel();
            activeToken = null;
            var wm = WindowManager.Instance;
            if (wm == null) return;
            wm.SetPassiveInput(false);
            if (wm.CompanionManager != null) wm.CompanionManager.ReleaseMotion(this);
        }
        void OnDisable() { Cancel(); }
        void OnApplicationQuit() { Cancel(); }
        void OnDestroy() { Cancel(); if (vision != null) vision.Dispose(); }
    }
}
