using System;
using System.IO;
using System.Runtime.InteropServices;
using APIs.Hyprland;
using MateEngine.Vision;
using UnityEngine;

namespace MateEngine.Desktop
{
    internal sealed class DesktopVision : IDisposable
    {
        readonly VisionChoreography policy = new VisionChoreography();
        readonly VisionPerformance performance;
        readonly string statePath, controlPath, auditionAddress;
        readonly VisionLease lease = new VisionLease();
        VisionPacket packet;
        DateTime modified;
        float nextRead, nextControl;
        bool controlWritten;
        string lastIdentity;
        [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
        static extern int Chmod(string path, uint mode);
        [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
        static extern int Rename(string source, string destination);
        static double Utc => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        Animator audioOwner;
        AvatarAnimatorController audioController;
        public string Error { get; private set; }
        public VisionChoreography Choreography { get { return policy; } }
        public VisionPerformance Performance { get { return performance; } }
        public bool Configured { get { return statePath != null; } }

        public DesktopVision()
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home) || !File.Exists(Path.Combine(home, ".mateengine-profile"))) return;
            var configured = Environment.GetEnvironmentVariable("MATEENGINE_VISION_STATE");
            if (string.IsNullOrEmpty(configured) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MATEENGINE_VISION_CONFIG"))) return;
            var expected = Path.GetFullPath(Path.Combine(home, "player-data", "vision-state.json"));
            if (!string.IsNullOrEmpty(configured) && Path.GetFullPath(configured) != expected)
                throw new InvalidOperationException("Vision state must remain in the selected mascot profile");
            statePath = expected;
            controlPath = Path.Combine(home, "player-data", "vision-control.json");
            auditionAddress = Environment.GetEnvironmentVariable("MATEENGINE_VISION_WINDOW");
            performance = new VisionPerformance(policy);
            Error = performance.Error;
        }
        bool WriteControl(bool changed)
        {
            if (controlPath == null) return false;
            if (!changed && Time.unscaledTime < nextControl) return controlWritten;
            nextControl = Time.unscaledTime + .25f;
            var temporary = controlPath + ".new";
            bool created = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(controlPath));
                if (File.Exists(temporary) || (File.Exists(controlPath) &&
                    (File.GetAttributes(controlPath) & FileAttributes.ReparsePoint) != 0)) return controlWritten = false;
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    if (Chmod(temporary, 384) != 0) throw new IOException("Cannot protect vision control");
                    using (var writer = new StreamWriter(file)) writer.Write(JsonUtility.ToJson(lease.Control));
                }
                if (Rename(temporary, controlPath) != 0) throw new IOException("Cannot publish vision control");
                return controlWritten = true;
            }
            catch (IOException) { Error = "Vision permission lease unavailable"; return controlWritten = false; }
            catch (UnauthorizedAccessException) { Error = "Vision permission lease unavailable"; return controlWritten = false; }
            finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
        }
        void Read()
        {
            if (statePath == null || Time.unscaledTime < nextRead) return;
            nextRead = Time.unscaledTime + .1f;
            try
            {
                var info = new FileInfo(statePath);
                if (!info.Exists || info.Length > 8192 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                { packet = null; return; }
                if (modified == info.LastWriteTimeUtc && packet != null) return;
                packet = JsonUtility.FromJson<VisionPacket>(File.ReadAllText(statePath));
                modified = info.LastWriteTimeUtc;
            }
            catch (IOException) { packet = null; }
            catch (UnauthorizedAccessException) { packet = null; }
            catch (ArgumentException) { packet = null; }
        }
        HyprlandClient Foreground(HyprlandManager manager, bool audition)
        {
            if (!audition) return manager.ForegroundWindow;
            if (string.IsNullOrEmpty(auditionAddress)) return null;
            var handle = HyprlandUtils.AddressToIntPtr(auditionAddress);
            var selected = manager.FindClientByWindowId(handle);
            if (selected == null || selected.workspace == null ||
                selected.workspace.name != CompanionPresencePolicy.OwnedAuditionWorkspace ||
                !manager.IsWindowVisible(handle)) return null;
            return selected;
        }
        public bool Tick(Animator avatar, HyprlandManager manager, bool enabled, bool suspended, bool audition)
        {
            if (!Configured || manager == null || avatar == null || performance == null || !performance.Ready)
            { Suspend(); return false; }
            var foreground = Foreground(manager, audition);
            lastIdentity = foreground == null ? "" : foreground.address + ":" + foreground.pid;
            bool safe = enabled && !suspended && manager.ObservationFresh && foreground != null &&
                (audition || manager.SurfacePresentable);
            double utc = Utc;
            bool changed = lease.Update(lastIdentity, foreground == null ? "" : foreground.title,
                audition ? 0 : manager.ForegroundRevision, safe, utc);
            if (changed) policy.Clear();
            if (!WriteControl(changed)) safe = false;
            Read();
            policy.Observe(safe && lease.Accepts(packet, utc) ? packet : null, safe ? lastIdentity : "", utc);
            if (audioOwner != avatar)
            {
                if (audioOwner != null) VisionAudioPolicy.Release(audioOwner);
                audioOwner = avatar;
                audioController = avatar.GetComponent<AvatarAnimatorController>();
            }
            bool semanticAudio = safe && foreground != null &&
                (CompanionContextPolicy.IsBrowser(foreground.initialClass) ||
                 CompanionContextPolicy.IsTelegram(foreground.initialClass) || policy.CurrentObservation);
            if (semanticAudio)
            {
                VisionAudioPolicy.Claim(avatar);
                if (audioController != null) audioController.SuppressAutomaticDanceForVision();
            }
            else VisionAudioPolicy.Release(avatar);
            bool stop = !safe || !policy.CurrentObservation && !policy.ReadingCarry;
            policy.Tick(Time.unscaledDeltaTime, stop);
            if (stop) { performance.Hide(); return safe; }
            if (!policy.ActivePerformance) { performance.Hide(); return safe; }
            Vector2 look = new Vector2(Screen.width * .5f, Screen.height * .5f);
            var observation = policy.Observation;
            if (observation != null && manager.CompanionWindow != null)
            {
                var window = manager.CompanionWindow;
                float x = observation.windowX + (observation.targetX + observation.targetWidth * .5f) * observation.windowWidth;
                float y = observation.windowY + (observation.targetY + observation.targetHeight * .5f) * observation.windowHeight;
                var origin = manager.GetWindowPosition();
                look = new Vector2((x - origin.x) * Screen.width / Mathf.Max(1, window.sizeVector.x),
                    Screen.height - (y - origin.y) * Screen.height / Mathf.Max(1, window.sizeVector.y));
            }
            try { performance.Apply(avatar, policy, look, Time.unscaledDeltaTime); Error = performance.Error; }
            catch (Exception error)
            {
                Error = error.Message; policy.Clear(); performance.Hide();
                Debug.LogError("[CompanionVision] " + error.GetType().Name + ": " + error.Message);
                return false;
            }
            return safe;
        }
        public void Suspend()
        {
            if (controlPath != null)
            {
                var control = lease.Control;
                bool changed = control.enabled;
                if (changed) { control.enabled = false; control.epoch++; control.notBeforeUtc = Utc; }
                WriteControl(changed);
            }
            policy.Clear();
            if (performance != null) performance.Hide();
            if (audioOwner != null) VisionAudioPolicy.Release(audioOwner);
        }
        public void Dispose()
        {
            Suspend(); if (performance != null) performance.Dispose();
        }
    }
}
