using System;
using System.Collections.Generic;
using UnityEngine;

namespace MateEngine.Vision
{
    public enum VisionRoutine { None, Read, Study, Chat, Work, Podcast, Film, Entertainment }
    public enum VisionProp { None, Magnifier, Notebook, Headphones, Popcorn }
    public enum VisionPhase { Idle, Enter, Hold, Lower, Raise, Gesture, Exit }

    [Serializable]
    public sealed class VisionPacket
    {
        public int version, epoch, windowX, windowY, windowWidth, windowHeight;
        public long sequence;
        public string lease, observer, identity, routine, accent;
        public double observedUtc, expiresUtc;
        public bool permitted, providerReady, scrolling, readingCarry;
        public float confidence, targetX, targetY, targetWidth, targetHeight;
    }

    [Serializable]
    public sealed class VisionControlPacket
    {
        public int version = 1, epoch;
        public string lease, identity;
        public bool enabled;
        public double notBeforeUtc, expiresUtc;
    }

    public sealed class VisionLease
    {
        public readonly VisionControlPacket Control = new VisionControlPacket { lease = Guid.NewGuid().ToString("N") };
        string title;
        long revision = -1;
        public bool Update(string identity, string windowTitle, long foregroundRevision, bool enabled, double utc)
        {
            bool changed = Control.identity != identity || title != windowTitle || revision != foregroundRevision || Control.enabled != enabled;
            if (changed)
            {
                Control.epoch++;
                Control.notBeforeUtc = utc;
                Control.identity = identity; title = windowTitle; revision = foregroundRevision; Control.enabled = enabled;
            }
            Control.expiresUtc = utc + 1.5;
            return changed;
        }
        public bool Accepts(VisionPacket packet, double utc)
        {
            return Control.enabled && Control.expiresUtc > utc && packet != null && packet.lease == Control.lease &&
                packet.epoch == Control.epoch && packet.identity == Control.identity && packet.observedUtc >= Control.notBeforeUtc;
        }
    }

    public sealed class VisionChoreography
    {
        readonly Dictionary<string, float> durations = new Dictionary<string, float>();
        public VisionRoutine Active { get; private set; }
        public VisionRoutine Desired { get; private set; }
        public VisionProp Prop { get; private set; }
        public VisionPhase Phase { get; private set; }
        public string Clip { get; private set; }
        public float PhaseTime { get; private set; }
        public bool Lowered { get; private set; }
        public string Accent { get; private set; } = "none";
        public float AccentTime { get; private set; }
        public bool ActivePerformance => Phase != VisionPhase.Idle;
        public VisionPacket Observation { get; private set; }
        public bool CurrentObservation { get; private set; }
        public bool ReadingCarry { get; private set; }
        public bool PropVisible => Prop != VisionProp.None && Phase != VisionPhase.Idle &&
            (Phase != VisionPhase.Enter || PhaseTime >= .12f) &&
            (Phase != VisionPhase.Exit || exitLower || PhaseTime < Duration(Clip) * .8f);
        float candidateAge, holdAge, gestureAt = 12f, accentCooldown, lensPause;
        bool scrolling, exitLower;
        string observer, pendingAccent = "none";
        long sequence;
        double accentObservation = -1, pendingObservation;
        int gestures;

        public void SetDuration(string name, float seconds)
        {
            if (string.IsNullOrEmpty(name) || !Finite(seconds) || seconds <= 0) throw new ArgumentException("Invalid vision clip duration");
            durations[name] = seconds;
        }
        public static VisionProp PropFor(VisionRoutine value)
        {
            switch (value)
            {
                case VisionRoutine.Read: return VisionProp.Magnifier;
                case VisionRoutine.Study: case VisionRoutine.Work: return VisionProp.Notebook;
                case VisionRoutine.Podcast: return VisionProp.Headphones;
                case VisionRoutine.Film: return VisionProp.Popcorn;
                default: return VisionProp.None;
            }
        }
        public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public static bool Valid(VisionPacket value, string identity, double utc)
        {
            return value != null && value.version == 2 && value.permitted && value.providerReady &&
                !string.IsNullOrEmpty(identity) && value.identity == identity && value.sequence > 0 &&
                !string.IsNullOrEmpty(value.lease) && !string.IsNullOrEmpty(value.observer) && value.observer.Length <= 80 &&
                !double.IsNaN(value.observedUtc) && !double.IsInfinity(value.observedUtc) &&
                !double.IsNaN(value.expiresUtc) && !double.IsInfinity(value.expiresUtc) &&
                value.observedUtc <= utc + 2 && value.expiresUtc > utc && value.expiresUtc - value.observedUtc <= 46 &&
                value.windowWidth > 0 && value.windowHeight > 0 && Finite(value.confidence) && value.confidence >= .65f && value.confidence <= 1 &&
                Finite(value.targetX) && Finite(value.targetY) && Finite(value.targetWidth) && Finite(value.targetHeight) &&
                value.targetX >= 0 && value.targetY >= 0 && value.targetWidth > 0 && value.targetHeight > 0 &&
                value.targetX + value.targetWidth <= 1.00001f && value.targetY + value.targetHeight <= 1.00001f;
        }
        static VisionRoutine Routine(string name)
        {
            switch (name)
            {
                case "read": return VisionRoutine.Read;
                case "study": return VisionRoutine.Study;
                case "chat": return VisionRoutine.Chat;
                case "work": return VisionRoutine.Work;
                case "podcast": return VisionRoutine.Podcast;
                case "film": return VisionRoutine.Film;
                case "entertainment": return VisionRoutine.Entertainment;
                default: return VisionRoutine.None;
            }
        }
        static bool AllowedAccent(string name) => name == "none" || name == "smile" || name == "laugh" ||
            name == "surprise" || name == "think" || name == "concern";
        void Request(VisionRoutine routine)
        {
            if (Desired == routine) return;
            Desired = routine; candidateAge = 0;
            Accent = pendingAccent = "none";
        }
        public bool Observe(VisionPacket value, string identity, double utc)
        {
            bool valid = Valid(value, identity, utc) && Routine(value.routine) != VisionRoutine.None && AllowedAccent(value.accent);
            if (valid && observer == value.observer && value.sequence < sequence) valid = false;
            ReadingCarry = valid && value.readingCarry && value.routine == "read" &&
                (Active == VisionRoutine.Read || Desired == VisionRoutine.Read);
            CurrentObservation = valid && !value.readingCarry;
            if (!CurrentObservation && !ReadingCarry)
            {
                Observation = null;
                Request(VisionRoutine.None); Accent = pendingAccent = "none"; return false;
            }
            if (observer != value.observer) { observer = value.observer; sequence = 0; }
            Observation = ReadingCarry ? null : value;
            scrolling = value.scrolling || ReadingCarry;
            Request(Routine(value.routine));
            if (CurrentObservation && value.sequence != sequence && value.observedUtc != accentObservation && !scrolling)
            { pendingAccent = value.accent; pendingObservation = value.observedUtc; }
            sequence = value.sequence;
            if (scrolling) Accent = pendingAccent = "none";
            return true;
        }
        void Play(VisionPhase phase, string clip)
        {
            Phase = phase; Clip = clip; PhaseTime = 0;
        }
        static string Prefix(VisionProp prop)
        {
            switch (prop)
            {
                case VisionProp.Magnifier: return "Magnifier";
                case VisionProp.Notebook: return "Notebook";
                case VisionProp.Headphones: return "Headphones";
                case VisionProp.Popcorn: return "Popcorn";
                default: return "";
            }
        }
        string HoldClip()
        {
            if (Prop != VisionProp.None) return Prefix(Prop) + "Hold";
            return Active == VisionRoutine.Chat ? "ChatHold" : "WatchHold";
        }
        void Enter()
        {
            Active = Desired; Prop = PropFor(Active); Lowered = false;
            holdAge = 0; gestures = 0; gestureAt = Active == VisionRoutine.Podcast ? 30 : Active == VisionRoutine.Film ? 25 : 12;
            Play(VisionPhase.Enter, Prop == VisionProp.None ? HoldClip() : Prefix(Prop) + "Equip");
        }
        void Hold()
        {
            Play(VisionPhase.Hold, Lowered ? "MagnifierLower" : HoldClip());
        }
        void Exit()
        {
            Accent = pendingAccent = "none";
            exitLower = Prop == VisionProp.Magnifier && !Lowered;
            Play(VisionPhase.Exit, exitLower ? "MagnifierLower" : Prop == VisionProp.None ? HoldClip() : Prefix(Prop) + "Stow");
        }
        public void Suspend()
        {
            Active = VisionRoutine.None; Prop = VisionProp.None; Phase = VisionPhase.Idle; Clip = null;
            PhaseTime = candidateAge = holdAge = lensPause = 0; Lowered = false; Accent = pendingAccent = "none";
        }
        public void Clear()
        {
            Suspend(); Desired = VisionRoutine.None; Observation = null; CurrentObservation = ReadingCarry = false;
        }
        public void Tick(float delta, bool suspended)
        {
            if (!Finite(delta) || delta <= 0) return;
            if (suspended) { Suspend(); return; }
            candidateAge += delta; PhaseTime += delta;
            accentCooldown = Mathf.Max(0, accentCooldown - delta);
            lensPause = Mathf.Max(0, lensPause - delta);
            if (Accent != "none")
            {
                AccentTime += delta;
                if (AccentTime >= 1.8f) Accent = "none";
            }
            if (Phase == VisionPhase.Idle)
            {
                if (Desired != VisionRoutine.None && candidateAge >= 1.2f) Enter();
                return;
            }
            if (Phase != VisionPhase.Exit && Desired != Active && candidateAge >= 1.2f)
            {
                if (Desired != VisionRoutine.None && PropFor(Desired) == Prop)
                { Active = Desired; Hold(); }
                else Exit();
            }
            switch (Phase)
            {
                case VisionPhase.Enter:
                    if (PhaseTime >= (Prop == VisionProp.None ? .3f : Duration(Clip))) Hold();
                    break;
                case VisionPhase.Exit:
                    if (PhaseTime < (Prop == VisionProp.None ? .3f : Duration(Clip))) break;
                    if (exitLower)
                    {
                        exitLower = false; Lowered = true; Play(VisionPhase.Exit, "MagnifierStow");
                    }
                    else
                    {
                        Active = VisionRoutine.None; Prop = VisionProp.None; Phase = VisionPhase.Idle; Clip = null; PhaseTime = 0;
                        if (Desired != VisionRoutine.None && candidateAge >= 1.2f) Enter();
                    }
                    break;
                case VisionPhase.Lower:
                    if (PhaseTime >= Duration(Clip)) { Lowered = true; Hold(); }
                    break;
                case VisionPhase.Raise:
                    if (PhaseTime >= Duration(Clip)) { Lowered = false; Hold(); }
                    break;
                case VisionPhase.Gesture:
                    if (PhaseTime >= Duration(Clip)) Hold();
                    break;
                case VisionPhase.Hold:
                    holdAge += delta;
                    bool lensDown = scrolling || lensPause > 0;
                    if (Prop == VisionProp.Magnifier && lensDown != Lowered)
                    { Play(lensDown ? VisionPhase.Lower : VisionPhase.Raise, lensDown ? "MagnifierLower" : "MagnifierRaise"); break; }
                    if (CurrentObservation && !scrolling && pendingAccent != "none" && accentCooldown <= 0)
                    {
                        Accent = pendingAccent; AccentTime = 0; accentCooldown = 30;
                        accentObservation = pendingObservation; pendingAccent = "none";
                        if (Prop == VisionProp.None)
                            Play(VisionPhase.Gesture, char.ToUpperInvariant(Accent[0]) + Accent.Substring(1));
                        else if (Prop == VisionProp.Magnifier)
                        { lensPause = 1.8f; if (!Lowered) Play(VisionPhase.Lower, "MagnifierLower"); }
                        else if (Prop == VisionProp.Notebook && Accent == "think")
                            Play(VisionPhase.Gesture, "NotebookThink");
                        else if (Prop == VisionProp.Popcorn && (Accent == "surprise" || Accent == "concern"))
                            Play(VisionPhase.Gesture, "PopcornPause");
                        break;
                    }
                    if (holdAge < gestureAt || !CurrentObservation || Desired != Active || scrolling) break;
                    gestures++; holdAge = 0;
                    if (Prop == VisionProp.Notebook) Play(VisionPhase.Gesture,
                        Active == VisionRoutine.Study && gestures % 3 == 0 ? "NotebookThink" : "NotebookWrite");
                    else if (Prop == VisionProp.Headphones) Play(VisionPhase.Gesture, "HeadphonesAdjust");
                    else if (Prop == VisionProp.Popcorn) Play(VisionPhase.Gesture, "PopcornBite");
                    break;
            }
        }
        public float Duration(string clip)
        {
            if (clip != null && durations.TryGetValue(clip, out var seconds)) return seconds;
            switch (clip)
            {
                case "MagnifierLower": case "MagnifierRaise": return .55f;
                case "MagnifierEquip": return 1.8f;
                case "PopcornEquip": case "PopcornStow": return 1.2f;
                case "MagnifierStow": return 1;
                case "NotebookEquip": return 1.3f;
                case "NotebookStow": return 1.2f;
                case "HeadphonesEquip": case "HeadphonesStow": return 1.5f;
                case "HeadphonesAdjust": return 1.2f;
                case "NotebookWrite": case "NotebookThink": case "PopcornBite": case "PopcornPause": return 2;
                case "Smile": case "Surprise": return 1.2f;
                case "Laugh": return 1.6f;
                case "Think": case "Concern": return 1.5f;
                default: return 6;
            }
        }
    }
}
