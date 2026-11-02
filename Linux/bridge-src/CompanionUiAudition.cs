using System;
using System.IO;
using UnityEngine;

namespace MateEngine.Desktop
{
    [Serializable]
    internal sealed class CompanionUiCommand { public int sequence; public string operation, value; }
    [Serializable]
    internal sealed class CompanionUiObservation
    {
        public int sequence, frame;
        public string operation, sessionId, runtimeEpoch, draft, submission, phase, error;
        public string radialGeometry, popup;
        public bool open, quiet, busy;
        public string[] controls;
        public bool ticketVisible;
        public Vector4 ticketBounds;
        public bool radialInputEnabled;
        public string[] radialIds;
        public Vector2[] radialPoints;
        public string[] radialCaptions;
        public bool[] radialAvailable;
        public string lastActionId, lastActionResult, panelError;
        public bool lastActionUnknown;
        public string selectedRadialId, releasedRadialId, pressedRadialId;
        public Vector2 radialMouse, releasedRadialMouse;
        public int radialReleaseFrame;
        public float scroll;
        public bool portraitActive, portraitAutomatic, portraitTransition, keyboardEligible, keyboardLease, keyboardAvailable;
        public bool sitting, seatTransition, surfacePresentable, confirmedHidden, auditionPresentable;
        public string portraitMonitor, framePolicy, seatSupport, seatContact, seatAnimation;
        public int requestedFps, effectiveFps, seatPose;
        public float seatOffset;
        public string seatEdge, framePose, context, contextCaption;
        public bool contextEnabled;
        public Vector3 leftFoot, rightFoot;
        public Vector2 viewport;
        public bool dragging, edgeAnchored;
        public Vector3 hips, head;
        public Vector2 pointer, seatDesktop;
        public Vector4 bodyBounds;
        public bool mouseHeld, focused;
        public bool animationDragging;
        public Vector2 eventMouse;
        public Vector2 unityMouse;
        public string pointerTarget;
        public string visionPhase, visionClip, visionAccent, visionError;
        public int visionProps;
        public bool visionApplied, visionObservation;
        public Vector3 visionLeftHand, visionRightHand;
    }
    internal sealed class CompanionUiAudition
    {
        readonly string commandPath, resultPath;
        int sequence;
        float next;
        internal CompanionUiAudition()
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home) || !File.Exists(Path.Combine(home, ".mateengine-profile"))) return;
            commandPath = Path.Combine(home, "companion-audition.json");
            resultPath = Path.Combine(home, "companion-audition-result.json");
        }
        internal void Tick(Func<CompanionUiCommand, CompanionUiObservation> execute)
        {
            if (commandPath == null || Time.unscaledTime < next || WindowManager.Instance == null ||
                !CompanionAudition.Allows(WindowManager.Instance.CompanionManager)) return;
            next = Time.unscaledTime + .1f;
            if (!File.Exists(commandPath)) return;
            var info = new FileInfo(commandPath);
            if (info.Length > 65536 || (info.Attributes & FileAttributes.ReparsePoint) != 0) return;
            CompanionUiCommand command;
            try { command = JsonUtility.FromJson<CompanionUiCommand>(File.ReadAllText(commandPath)); }
            catch (ArgumentException) { return; }
            if (command == null || command.sequence <= sequence) return;
            sequence = command.sequence;
            CompanionUiObservation result;
            try { result = execute(command); }
            catch (Exception error) { result = new CompanionUiObservation { error = error.Message }; }
            result.sequence = sequence; result.frame = Time.frameCount; result.operation = command.operation;
            var temporary = resultPath + ".new";
            File.WriteAllText(temporary, JsonUtility.ToJson(result));
            if (File.Exists(resultPath)) File.Replace(temporary, resultPath, null); else File.Move(temporary, resultPath);
        }
    }
    internal sealed class CompanionPointerInput : UnityEngine.EventSystems.BaseInput
    {
        public override Vector2 mousePosition => MateEngine.Shoji.ShojiBridge.GetUnityMousePosition();
        int sampledFrame = -1, held, down, up;
        bool initialized;
        void SampleButtons()
        {
            if (sampledFrame == Time.frameCount) return;
            sampledFrame = Time.frameCount;
            var window = WindowManager.Instance;
            // Unity can discard button edges when an Xwayland window is partly
            // offscreen. Query all physical levels once, then derive frame edges.
            int next = window != null && window.CompanionManager != null ?
                (Application.isFocused && window.CompanionManager.PointerEligible ? window.GetMouseButtons() : 0) :
                (Input.GetMouseButton(0) ? 1 : 0) | (Input.GetMouseButton(1) ? 2 : 0) |
                (Input.GetMouseButton(2) ? 4 : 0);
            down = initialized ? next & ~held : 0;
            up = initialized ? held & ~next : 0;
            held = next; initialized = true;
        }
        public override bool GetMouseButton(int button)
        {
            if (button < 0 || button > 2) return base.GetMouseButton(button);
            SampleButtons(); return (held & (1 << button)) != 0;
        }
        public override bool GetMouseButtonDown(int button)
        {
            if (button < 0 || button > 2) return base.GetMouseButtonDown(button);
            SampleButtons(); return (down & (1 << button)) != 0;
        }
        public override bool GetMouseButtonUp(int button)
        {
            if (button < 0 || button > 2) return base.GetMouseButtonUp(button);
            SampleButtons(); return (up & (1 << button)) != 0;
        }
    }
}

namespace MateEngine.Desktop
{
    public sealed class CompanionKeyboardIntent : MonoBehaviour, UnityEngine.EventSystems.IPointerDownHandler
    {
        public void OnPointerDown(UnityEngine.EventSystems.PointerEventData value)
        {
            if (value != null && value.button == UnityEngine.EventSystems.PointerEventData.InputButton.Left)
                WindowManager.Instance?.CompanionManager?.RequestKeyboard();
        }
    }
}
