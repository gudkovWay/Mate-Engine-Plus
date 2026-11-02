using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace MateEngine.Desktop
{
    public sealed class DesktopPanel : MonoBehaviour
    {
        static DesktopPanel instance;
        static bool checkedEnvironment, pointerHeld;
        DesktopClient client;
        DesktopReaction reaction;
        CompanionInbox inbox;
        CompanionUiAudition audition;
        CompanionPointerInput pointerInput;
        RectTransform canvasRect, ticket, speech, popup, popupContent;
        Text ticketTitle, ticketReason, ticketCount, speechText, popupTitle, feedback;
        MenuActions menu;
        MenuEntry inboxEntry, popupEntry;
        Xamin.CircleSelector radial;
        List<GameObject> stockRadial, dailyRadial, avatarRadial, actionRadial;
        readonly List<GameObject> actionTemplates = new List<GameObject>();
        ControlAction[] actionInventory;
        ControlAction radialConfirmation;
        ControlResponse observedActionResponse;
        string actionKind = "quick", lastActionId, lastActionResult;
        int actionPage;
        bool lastActionUnknown, actionFresh, actionBusy;
        int reopenRadialAt = -1;
        bool radialWasVisible;
        bool renderedSitting, renderedSittingEnabled;
        readonly List<Rect> inputRegions = new List<Rect>(3);
        Rect[] previousRegions = new Rect[0];
        string popupKind, renderedRevision, ticketKey, ticketLabel;
        ControlAction confirmation;
        int controlSequence, resultRevision = -1, oldWidth, oldHeight;
        float ticketStarted;
        Vector2 ticketAnchor;
        int ticketItemCount = -1;
        bool ticketCompact;
        bool renderedQuiet, renderedBusy, renderedFresh;
        bool savedFollow, followFrozen;
        public static void EnsureCreated()
        {
            if (checkedEnvironment) return; checkedEnvironment = true;
            string path = Environment.GetEnvironmentVariable("MATEENGINE_CONTROL_SOCKET");
            if (string.IsNullOrEmpty(path)) return;
            var owner = new GameObject("Mascot companion"); Object.DontDestroyOnLoad(owner);
            instance = owner.AddComponent<DesktopPanel>(); instance.client = new DesktopClient(path);
        }
        public static void PointerDragChanged(bool value)
        {
            if (pointerHeld == value) return;
            pointerHeld = value;
            if (value && instance != null && instance.reaction != null) instance.reaction.Cancel();
        }
        void Start()
        {
            reaction = gameObject.AddComponent<DesktopReaction>();
            canvasRect = CompanionUi.Rect("Companion native canvas", transform);
            var canvas = canvasRect.gameObject.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 3000;
            canvasRect.gameObject.AddComponent<GraphicRaycaster>();
            inbox = new CompanionInbox(canvasRect, client, () => SetInbox(false));
            popup = CompanionUi.Panel("Companion actions", canvasRect); CompanionUi.Top(popup, 18, 72, 320, 300);
            popupTitle = CompanionUi.Text(popup, "Действия", 19); CompanionUi.Top(popupTitle.rectTransform, 14, 10, 210, 30);
            var behaviour = CompanionUi.Button(popup, "···", () => { if (popupKind == "behaviour") OpenActions("quick", 0); else OpenPopup("behaviour"); });
            behaviour.gameObject.name = "Поведение"; CompanionUi.Top(behaviour.GetComponent<RectTransform>(), 235, 10, 32, 28);
            var close = CompanionUi.Button(popup, "×", ClosePopup); CompanionUi.Top(close.GetComponent<RectTransform>(), 273, 10, 32, 28);
            var popupScroll = CompanionUi.Scroll(popup, out popupContent); CompanionUi.Top(popupScroll.GetComponent<RectTransform>(), 10, 48, 300, 218);
            feedback = CompanionUi.Text(popup, "", 13, CompanionUi.Muted); CompanionUi.Top(feedback.rectTransform, 14, 269, 292, 28);
            popup.gameObject.SetActive(false);
            ticket = CompanionUi.Panel("Unread invitation", canvasRect); CompanionUi.Top(ticket, 18, 32, 300, 90);
            ticketTitle = CompanionUi.Text(ticket, "", 14, CompanionUi.Muted); CompanionUi.Top(ticketTitle.rectTransform, 14, 9, 220, 22);
            ticketReason = CompanionUi.Text(ticket, "Есть ответ", 18); CompanionUi.Top(ticketReason.rectTransform, 14, 34, 220, 28);
            var read = CompanionUi.Button(ticket, "Читать", () =>
            {
                var current = client.Latest;
                string session = current != null && current.attention != null && current.attention.items.Length > 0 ? current.attention.items[0].session_id : null;
                SetInbox(true, true); if (!string.IsNullOrEmpty(session)) inbox.ShowSession(session);
            }); CompanionUi.Top(read.GetComponent<RectTransform>(), 218, 46, 72, 30);
            ticketCount = CompanionUi.Text(ticket, "", 13, CompanionUi.Accent); CompanionUi.Top(ticketCount.rectTransform, 234, 8, 56, 24);
            ticket.gameObject.SetActive(false);
            speech = CompanionUi.Panel("Companion aside", canvasRect); CompanionUi.Top(speech, 18, 32, 340, 100);
            speechText = CompanionUi.Text(speech, "", 17); CompanionUi.Top(speechText.rectTransform, 14, 10, 312, 80);
            speech.GetComponent<Image>().raycastTarget = false; speechText.raycastTarget = false;
            speech.gameObject.SetActive(false);
            foreach (var candidate in Object.FindObjectsByType<MenuActions>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (candidate.radialMenuKey == KeyCode.Mouse1) { menu = candidate; break; }
            if (menu != null)
            {
                inboxEntry = new MenuEntry { menu = inbox.Root.gameObject, blockMovement = true, blockHandTracking = true };
                popupEntry = new MenuEntry { menu = popup.gameObject, blockMovement = true, blockHandTracking = true };
                menu.menuEntries.Add(inboxEntry); menu.menuEntries.Add(popupEntry); InstallRadial();
            }
            Resize();
            if (Environment.GetEnvironmentVariable("MATEENGINE_COMPANION_AUDITION") == "1") audition = new CompanionUiAudition();
            if (Environment.GetEnvironmentVariable("MATEENGINE_CONTROL_OPEN") == "1") SetInbox(true);
            Debug.Log("[MascotControl] OMP inbox, ticket and distinct daily controls ready");
        }
        void Update()
        {
            if (client == null || inbox == null) return;
            var inputModule = EventSystem.current == null ? null : EventSystem.current.currentInputModule;
            if (WindowManager.Instance?.CompanionManager != null && inputModule != null)
            {
                if (pointerInput == null) pointerInput = gameObject.AddComponent<CompanionPointerInput>();
                if (inputModule.inputOverride != pointerInput) inputModule.inputOverride = pointerInput;
            }
            if (reopenRadialAt >= 0 && Time.frameCount >= reopenRadialAt)
            {
                reopenRadialAt = -1; if (radial != null) radial.Open();
            }
            if (oldWidth != Screen.width || oldHeight != Screen.height) Resize();
            var snapshot = client.Latest;
            bool interacting = pointerHeld || MenuActions.IsAnyMenuOpen() || inbox.IsOpen || popup.gameObject.activeInHierarchy;
            reaction.SetAttention(snapshot == null ? null : snapshot.attention, client.Fresh, pointerHeld, interacting);
            client.Surface(reaction.Presentable, interacting || TutorialMenu.IsActive);
            if (snapshot != null && client.Fresh && snapshot.control_sequence > controlSequence)
            {
                controlSequence = snapshot.control_sequence;
                if (snapshot.control_command == "home") { SetInbox(false); ClosePopup(); reaction.Home(); }
                else if (snapshot.control_command == "menu") SetInbox(true);
            }
            inbox.Tick(snapshot);
            UpdateActionRing(snapshot);
            if (client.ResultRevision != resultRevision)
            {
                resultRevision = client.ResultRevision;
                var response = client.ActionResponse;
                var action = response != null && response.requestMethod == "run" ? FindAction(response.requestAction) : null;
                feedback.text = CompanionUi.Short(action == null ? client.Result : action.label + ": " + client.Result, 130);
            }
            bool quiet = snapshot != null && snapshot.attention != null && snapshot.attention.quiet;
            var currentSeat = reaction.Seating;
            bool seated = currentSeat != null && currentSeat.IsSeated;
            bool seatEnabled = SaveLoadHandler.Instance != null && SaveLoadHandler.Instance.data.enableWindowSitting;
            if (popup.gameObject.activeSelf && (renderedRevision != snapshot?.revision ||
                renderedQuiet != quiet || renderedBusy != client.Busy || renderedFresh != client.Fresh ||
                popupKind == "behaviour" && (renderedSitting != seated || renderedSittingEnabled != seatEnabled)))
            {
                renderedRevision = snapshot?.revision; renderedQuiet = quiet;
                renderedBusy = client.Busy; renderedFresh = client.Fresh; BuildPopup();
            }
            if (popup.gameObject.activeSelf && Application.isFocused && Input.GetKeyDown(KeyCode.Escape))
            { if (confirmation != null) { confirmation = null; BuildPopup(); } else ClosePopup(); }
            UpdateTicket(snapshot);
            if (audition != null) audition.Tick(Audition);
        }
        CompanionUiObservation Audition(CompanionUiCommand command)
        {
            switch (command.operation)
            {
                case "drag.press":
                    WindowManager.Instance.OnPointerDown(new PointerEventData(EventSystem.current)
                        { button = PointerEventData.InputButton.Left });
                    break;
                case "drag.release":
                    WindowManager.Instance.OnPointerUp(null);
                    break;
                case "close":
                    SetInbox(false); ClosePopup(); radial.Close();
                    break;
                case "portrait.toggle":
                    if (reaction.Portrait == null) throw new InvalidOperationException("Portrait handler unavailable");
                    SetInbox(false); ClosePopup(); reaction.Cancel();
                    reaction.Portrait.ToggleBigScreenFromUI();
                    break;
                case "behaviour": OpenPopup("behaviour"); break;
                case "seat.sit":
                    if (!SitOnWindow(APIs.Hyprland.HyprlandUtils.AddressToIntPtr(command.value)))
                        throw new InvalidOperationException("Support is not a safe visible window top");
                    break;
                case "seat.upper":
                case "seat.lower":
                case "seat.watch":
                    if (!SitOnWindow(APIs.Hyprland.HyprlandUtils.AddressToIntPtr(command.value),
                        command.operation == "seat.upper" ? APIs.Hyprland.CompanionFramePose.Dangle :
                        command.operation == "seat.watch" ? APIs.Hyprland.CompanionFramePose.Watch : APIs.Hyprland.CompanionFramePose.Lie,
                        command.operation != "seat.upper")) throw new InvalidOperationException("No safe visible ledge for this pose");
                    break;
                case "context.toggle": reaction.ToggleContext(); break;
                case "seat.stand": StandFromWindow(); break;
                case "seat.pose":
                    if (reaction.Seating == null || !reaction.Seating.NextSeatPose()) throw new InvalidOperationException("No active window seat");
                    break;
                case "seat.offset":
                    if (reaction.Seating == null) throw new InvalidOperationException("Seat handler unavailable");
                    reaction.Seating.SetSeatOffset(float.Parse(command.value, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "place.left":
                case "place.right":
                    if (reaction.CurrentPhase != CompanionMotionPhase.Idle) throw new InvalidOperationException("Only a stationary audition can be placed");
                    var manager = WindowManager.Instance.CompanionManager;
                    if (manager.CompanionMotion || manager.SupportTracking) throw new InvalidOperationException("Placement is owned by portrait, movement or seating");
                    var output = Array.Find(manager.Monitors, item => item.id == manager.CompanionWindow.monitor);
                    var body = manager.BodyBounds; var position = manager.GetWindowPosition();
                    float placeX = command.operation == "place.left" ? output.workArea.xMin - body.xMin + 32 : output.workArea.xMax - body.xMax - 32;
                    manager.SetCompanionPosition(new Vector2Int(Mathf.RoundToInt(placeX), position.y));
                    break;
                case "open": SetInbox(true); break;
                case "dismiss":
                    var snapshot = client.Latest;
                    if (snapshot != null && snapshot.attention != null) client.Attention("dismiss", snapshot.attention.token);
                    break;
                case "radial":
                    SetInbox(false); ClosePopup(); radial.Open();
                    var radialRect = (RectTransform)radial.transform;
                    var radialCanvas = radial.GetComponentInParent<Canvas>();
                    var radialCamera = radialCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : radialCanvas.worldCamera;
                    typeof(MenuActions).GetField("screenPosition", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .SetValue(menu, new Vector3(Screen.width * .5f, Screen.height * .6f, 0));
                    Vector3 radialPoint;
                    if (RectTransformUtility.ScreenPointToWorldPointInRectangle(radialRect.parent as RectTransform,
                        new Vector2(Screen.width * .5f, Screen.height * .6f), radialCamera, out radialPoint))
                        radialRect.position = radialPoint;
                    break;
                case "radial.click":
                    var radialButton = radial.GetButtonWithId(command.value);
                    if (radialButton == null || !radialButton.unlocked) throw new InvalidOperationException("Radial action unavailable");
                    radialButton.ExecuteAction(); radial.Close();
                    break;
                case "popup.click":
                    var button = Array.Find(popupContent.GetComponentsInChildren<Button>(), item => item.gameObject.name == command.value);
                    if (button == null || !button.interactable) throw new InvalidOperationException("Popup action unavailable");
                    button.onClick.Invoke();
                    break;
                default:
                    var result = inbox.Audition(command); ObservePanel(result); return result;
            }
            var observation = inbox.Audition(new CompanionUiCommand { operation = "state" });
            ObservePanel(observation); return observation;
        }
        void ObservePanel(CompanionUiObservation observation)
        {
            observation.phase = reaction.CurrentPhase.ToString();
            var manager = WindowManager.Instance?.CompanionManager;
            var portrait = reaction.Portrait;
            observation.portraitActive = portrait != null && portrait.IsBigScreenActive;
            observation.portraitAutomatic = portrait != null && portrait.IsAutomaticActive;
            observation.portraitTransition = portrait != null && portrait.IsTransitioning;
            observation.portraitMonitor = portrait == null ? "" : portrait.AutomaticMonitor;
            observation.keyboardEligible = manager != null && manager.KeyboardEligible;
            observation.keyboardLease = manager != null && manager.KeyboardLease;
            observation.keyboardAvailable = manager != null && manager.KeyboardAvailable;
            observation.surfacePresentable = manager != null && manager.SurfacePresentable;
            observation.confirmedHidden = manager != null && manager.ConfirmedHidden;
            observation.auditionPresentable = manager != null && manager.OwnedAuditionPresentable;
            var limiter = UnityEngine.Object.FindFirstObjectByType<FPSLimiter>();
            observation.requestedFps = limiter == null ? 0 : limiter.targetFPS;
            observation.effectiveFps = Application.targetFrameRate;
            observation.framePolicy = limiter == null ? "unavailable" : limiter.RuntimePolicy;
            var seat = reaction.Seating;
            observation.sitting = seat != null && seat.IsSeated;
            observation.seatTransition = seat != null && seat.SeatTransition;
            observation.seatPose = seat == null ? 0 : seat.SeatPose;
            observation.seatSupport = seat == null ? "" : seat.SeatSupport;
            observation.seatOffset = seat == null ? 0 : seat.seatOffsetPx;
            observation.seatContact = seat == null ? "" : seat.SeatContactBasis;
            observation.seatAnimation = seat == null ? "" : seat.SeatAnimation;
            observation.seatEdge = seat == null || !seat.IsSeated ? "" : seat.BottomEdge ? "bottom" : "top";
            observation.framePose = seat == null ? "" : seat.FramePose.ToString();
            observation.context = reaction.ContextState; observation.contextCaption = reaction.ContextCaption;
            observation.contextEnabled = reaction.ContextEnabled;
            var vision = reaction.Vision;
            if (vision != null)
            {
                observation.visionPhase = vision.Choreography.Phase.ToString();
                observation.visionClip = vision.Choreography.Clip;
                observation.visionAccent = vision.Choreography.Accent;
                observation.visionObservation = vision.Choreography.CurrentObservation;
                observation.visionError = vision.Error;
                var performance = vision.Performance;
                if (performance != null)
                {
                    observation.visionApplied = performance.Applied;
                    observation.visionProps = performance.VisibleProps;
                    observation.visionLeftHand = performance.LeftHand;
                    observation.visionRightHand = performance.RightHand;
                }
            }
            observation.dragging = manager != null && manager.IsDragging;
            observation.animationDragging = seat != null && seat.GetComponent<AvatarAnimatorController>()?.isDragging == true;
            observation.pointer = manager == null ? Vector2.zero : (Vector2)manager.GetMousePosition();
            observation.mouseHeld = Input.GetMouseButton(0);
            observation.focused = Application.isFocused;
            observation.unityMouse = Input.mousePosition;
            observation.eventMouse = MateEngine.Shoji.ShojiBridge.GetUnityMousePosition();
            if (EventSystem.current != null)
            {
                var pointer = new PointerEventData(EventSystem.current) { position = observation.eventMouse };
                var hits = new System.Collections.Generic.List<RaycastResult>();
                EventSystem.current.RaycastAll(pointer, hits);
                observation.pointerTarget = hits.Count == 0 ? "" : hits[0].gameObject.name;
            }
            if (manager != null)
            {
                var body = manager.BodyBounds;
                observation.bodyBounds = new Vector4(body.x, body.y, body.width, body.height);
            }
            if (seat != null && seat.targetCamera != null)
            {
                var avatar = seat.GetComponent<Animator>();
                if (avatar != null && avatar.isHuman)
                {
                    var hips = avatar.GetBoneTransform(HumanBodyBones.Hips);
                    var head = avatar.GetBoneTransform(HumanBodyBones.Head);
                    if (hips != null) observation.hips = seat.targetCamera.WorldToScreenPoint(hips.position);
                    if (head != null) observation.head = seat.targetCamera.WorldToScreenPoint(head.position);
                    var edge = avatar.GetComponent<AvatarHideHandler>();
                    observation.edgeAnchored = edge != null && edge.IsAnchored;
                    var contact = new object[] { 0f, 0f };
                    var compute = typeof(AvatarWindowHandler).GetMethod("ComputeSeatDesktop",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (compute != null && (bool)compute.Invoke(seat, contact))
                        observation.seatDesktop = new Vector2((float)contact[0], (float)contact[1]);
                    var left = avatar.GetBoneTransform(HumanBodyBones.LeftFoot);
                    var right = avatar.GetBoneTransform(HumanBodyBones.RightFoot);
                    if (left != null) observation.leftFoot = seat.targetCamera.WorldToScreenPoint(left.position);
                    if (right != null) observation.rightFoot = seat.targetCamera.WorldToScreenPoint(right.position);
                    observation.viewport = new Vector2(seat.targetCamera.pixelWidth, seat.targetCamera.pixelHeight);
                }
            }
            var snapshot = client.Latest;
            observation.quiet = snapshot != null && snapshot.attention != null && snapshot.attention.quiet;
            observation.busy = client.Busy;
            observation.ticketVisible = ticket.gameObject.activeInHierarchy;
            observation.ticketBounds = new Vector4(ticket.anchoredPosition.x, -ticket.anchoredPosition.y, ticket.sizeDelta.x, ticket.sizeDelta.y);
            var buttons = popupContent.GetComponentsInChildren<Button>();
            observation.popup = popup.gameObject.activeInHierarchy ? popupKind : "";
            observation.controls = Array.ConvertAll(buttons, item => item.gameObject.name);
            observation.lastActionId = lastActionId; observation.lastActionResult = lastActionResult;
            observation.lastActionUnknown = lastActionUnknown; observation.panelError = client.PanelError;
            if (radial != null)
            {
                var radialCanvas = radial.GetComponentInParent<Canvas>();
                var radialCamera = radialCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : radialCanvas.worldCamera;
                observation.radialGeometry = radialCanvas.renderMode + " world=" + radial.transform.position +
                    " screen=" + RectTransformUtility.WorldToScreenPoint(radialCamera, radial.transform.position) +
                    " parent=" + radial.transform.parent.name;
                var radialButtons = radial.transform.Find("Buttons");
                observation.radialInputEnabled = radial.transform.Find("Cursor").GetComponent<Image>().isActiveAndEnabled;
                observation.selectedRadialId = radial.SelectedSegment == null ? "" : radial.SelectedSegment.GetComponent<Xamin.Button>().id;
                observation.radialMouse = Input.mousePosition;
                var skin = radial.GetComponent<CompanionRadialSkin>();
                observation.releasedRadialId = skin.ReleasedId; observation.releasedRadialMouse = skin.ReleasedMouse;
                observation.radialReleaseFrame = skin.ReleaseFrame;
                observation.pressedRadialId = skin.PressedId;
                observation.radialIds = new string[radialButtons.childCount];
                observation.radialPoints = new Vector2[radialButtons.childCount];
                observation.radialCaptions = new string[radialButtons.childCount];
                observation.radialAvailable = new bool[radialButtons.childCount];
                for (int i = 0; i < radialButtons.childCount; i++)
                {
                    var item = radialButtons.GetChild(i).GetComponent<RectTransform>();
                    observation.radialIds[i] = item.GetComponent<Xamin.Button>().id;
                    observation.radialPoints[i] = RectTransformUtility.WorldToScreenPoint(radialCamera, item.TransformPoint(item.rect.center));
                    var entry = item.GetComponent<DesktopMenuAction>();
                    observation.radialCaptions[i] = entry == null ? "" : entry.caption;
                    observation.radialAvailable[i] = item.GetComponent<Xamin.Button>().unlocked;
                }
            }
        }
        void Resize()
        {
            oldWidth = Screen.width; oldHeight = Screen.height;
            float width = Mathf.Min(520, Mathf.Max(260, Screen.width - 32));
            float height = Mathf.Min(460, Mathf.Max(320, Screen.height * .75f));
            inbox.Resize(width, height);
            CompanionUi.Top(inbox.Root, 16, Mathf.Min(72, Mathf.Max(12, Screen.height - height - 12)), width, height);
            popup.anchoredPosition = new Vector2(16, -Mathf.Min(72, Mathf.Max(12, Screen.height - popup.sizeDelta.y - 12)));
        }
        void SetInbox(bool value, bool keyboard = false)
        {
            if (value && keyboard) WindowManager.Instance?.CompanionManager?.RequestKeyboard();
            if (!value) WindowManager.Instance?.CompanionManager?.ReleaseKeyboard();
            if (inbox == null || inbox.IsOpen == value) return;
            if (value) { if (menu != null) menu.CloseAllMenus(); popup.gameObject.SetActive(false); reaction.Cancel(); }
            inbox.SetOpen(value); WindowManager.Instance.SetPassiveInput(false); client.PanelVisible(value);
        }
        void OpenPopup(string kind)
        {
            SetInbox(false); if (menu != null) menu.CloseAllMenus(); reaction.Cancel();
            popupKind = kind; confirmation = null; popup.gameObject.SetActive(true); WindowManager.Instance.SetPassiveInput(false);
            client.PanelVisible(true); BuildPopup();
        }
        void ClosePopup()
        {
            if (popup == null || !popup.gameObject.activeSelf) return;
            popup.gameObject.SetActive(false); confirmation = null; client.PanelVisible(false);
            WindowManager.Instance?.CompanionManager?.ReleaseKeyboard();
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        }
        void BuildPopup()
        {
            var selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            string selectedName = selected != null && selected.transform.IsChildOf(popupContent) ? selected.name : null;
            CompanionUi.Clear(popupContent); var snapshot = client.Latest;
            popupTitle.text = popupKind == "diagnostics" ? "Диагностика" : "Поведение";
            if (confirmation != null)
            {
                var action = confirmation; AddText("Подтвердить: " + action.label + "?", 56);
                CompanionUi.Button(popupContent, "Подтвердить", () => { confirmation = null; client.Run(action.id, true); BuildPopup(); });
                CompanionUi.Button(popupContent, "Отмена", () => { confirmation = null; BuildPopup(); });
                RestorePopupSelection(selectedName); return;
            }
            if (popupKind == "behaviour")
            {
                Button keyboard = null;
                keyboard = CompanionUi.Button(popupContent, "Клавиатурное управление", () =>
                {
                    WindowManager.Instance?.CompanionManager?.RequestKeyboard();
                    if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(keyboard.gameObject);
                });
                keyboard.interactable = WindowManager.Instance?.CompanionManager?.KeyboardAvailable == true;
                bool quiet = snapshot != null && snapshot.attention != null && snapshot.attention.quiet;
                CompanionUi.Button(popupContent, quiet ? "Снова приглашать" : "Тишина на час", () => { client.Attention(quiet ? "resume" : "quiet", ""); });
                CompanionUi.Button(popupContent, string.IsNullOrEmpty(reaction.PinnedMonitor) ? "Закрепить текущий монитор" : "Снять закрепление монитора", () => { reaction.ToggleMonitorPin(); BuildPopup(); });
                bool enabled = SaveLoadHandler.Instance != null && SaveLoadHandler.Instance.data.enableWindowSitting;
                renderedSittingEnabled = enabled;
                CompanionUi.Button(popupContent, enabled ? "Запретить посадку на окна" : "Разрешить посадку на окна", () => { SetWindowSitting(!enabled); BuildPopup(); });
                CompanionUi.Button(popupContent, reaction.ContextEnabled ? "Отключить реакции на приложения" : "Включить реакции на приложения", () => { reaction.ToggleContext(); BuildPopup(); });
                CompanionUi.Button(popupContent, "Сесть сверху · качать ногами", () =>
                {
                    var manager = WindowManager.Instance?.CompanionManager;
                    feedback.text = manager != null && SitOnWindow(manager.ForegroundSeatSupport, APIs.Hyprland.CompanionFramePose.Dangle) ? "Верхняя кромка окна" : "Нет безопасного окна с местом над ним";
                });
                CompanionUi.Button(popupContent, "Лечь на нижнюю кромку", () =>
                {
                    var manager = WindowManager.Instance?.CompanionManager;
                    feedback.text = manager != null && SitOnWindow(manager.ForegroundSeatSupport, APIs.Hyprland.CompanionFramePose.Lie, true) ? "Нижняя кромка окна" : "Нет безопасной нижней кромки";
                });
                CompanionUi.Button(popupContent, "Смотреть вместе · спиной", () =>
                {
                    var manager = WindowManager.Instance?.CompanionManager;
                    feedback.text = manager != null && SitOnWindow(manager.ForegroundSeatSupport, APIs.Hyprland.CompanionFramePose.Watch, true) ? "Смотрим вместе" : "Нет безопасной нижней кромки";
                });
                var stand = CompanionUi.Button(popupContent, "Встать", StandFromWindow);
                var seat = reaction.Seating;
                renderedSitting = seat != null && seat.IsSeated;
                stand.interactable = renderedSitting;
                if (seat != null && seat.IsSeated)
                {
                    if (seat.FramePose == APIs.Hyprland.CompanionFramePose.Stock || seat.BottomEdge)
                        CompanionUi.Button(popupContent, "Следующая поза", () => seat.NextSeatPose());
                    CompanionUi.Button(popupContent, "Посадка выше", () => seat.SetSeatOffset(seat.seatOffsetPx - 4));
                    CompanionUi.Button(popupContent, "Посадка ниже", () => seat.SetSeatOffset(seat.seatOffsetPx + 4));
                }
                CompanionUi.Button(popupContent, "Диагностика и службы", () => OpenPopup("diagnostics"));
                RestorePopupSelection(selectedName); return;
            }
            if (popupKind == "diagnostics")
            {
                AddText(SteamStatus(), 44);
                AddText(client.Error ?? "Локальный адаптер подключён", 42);
                if (!string.IsNullOrEmpty(client.PanelError)) AddText(client.PanelError, 55);
                if (snapshot != null && snapshot.issues != null) foreach (var issue in snapshot.issues) AddText(issue, 55);
            }
            if (snapshot == null) { AddText("Локальный адаптер пока недоступен.", 60); RestorePopupSelection(selectedName); return; }
            int count = 0;
            foreach (var action in snapshot.actions)
            {
                if (action.group != "services") continue;
                var captured = action; var button = CompanionUi.Button(popupContent, action.label, () =>
                {
                    if (captured.confirmation) { confirmation = captured; BuildPopup(); }
                    else client.Run(captured.id, false);
                });
                button.interactable = client.Fresh && !client.Busy && action.available;
                if (!action.available) AddText(action.reason, 32);
                count++;
            }
            if (count == 0) AddText("Нет настроенных служб.", 45);
            RestorePopupSelection(selectedName);
        }
        void RestorePopupSelection(string name)
        {
            if (name == null || EventSystem.current == null) return;
            var buttons = popupContent.GetComponentsInChildren<Button>();
            var target = Array.Find(buttons, button => button.gameObject.name == name && button.interactable) ??
                Array.Find(buttons, button => button.interactable);
            EventSystem.current.SetSelectedGameObject(target == null ? null : target.gameObject);
        }

        void SetWindowSitting(bool enabled)
        {
            var settings = SaveLoadHandler.Instance;
            if (settings == null || settings.data == null) return;
            settings.data.enableWindowSitting = enabled;
            settings.SaveToDisk();
            if (!enabled) StandFromWindow();
        }
        bool SitOnWindow(IntPtr support, APIs.Hyprland.CompanionFramePose pose = APIs.Hyprland.CompanionFramePose.Stock, bool bottom = false)
        {
            if (support == IntPtr.Zero || reaction.Seating == null) return false;
            reaction.Cancel();
            SetWindowSitting(true);
            bool seated = reaction.Seating.TrySitOnWindow(support, 0, pose, bottom);
            if (seated) { SetInbox(false); ClosePopup(); }
            return seated;
        }
        void StandFromWindow()
        {
            if (reaction.Seating != null) reaction.Seating.ForceExitWindowSitting();
        }

        void AddText(string value, float height) { var text = CompanionUi.Text(popupContent, value, 14, CompanionUi.Muted); CompanionUi.Size(text.gameObject, height); }
        static string SteamStatus()
        {
            try { return !SteamManager.Initialized ? "Steam: нет соединения" : SteamApps.BIsSubscribedApp(new AppId_t(3625270)) ? "Steam: покупка подтверждена" : "Steam: ownership не подтверждён"; }
            catch (Exception error) { return "Steam: " + error.GetType().Name; }
        }
        void UpdateTicket(ControlSnapshot snapshot)
        {
            var attention = snapshot == null ? null : snapshot.attention;
            bool has = client.Fresh && attention != null && attention.items != null && attention.items.Length > 0 && !attention.quiet;
            bool show = has && !pointerHeld && !MenuActions.IsAnyMenuOpen() && !TutorialMenu.IsActive;
            bool changed = has && ticketKey != attention.items[0].id;
            if (changed)
            {
                ticketKey = attention.items[0].id; ticketStarted = Time.unscaledTime;
                var manager = WindowManager.Instance.CompanionManager;
                if (manager != null && manager.CompanionWindow != null)
                {
                    var body = manager.BodyBounds; var size = manager.CompanionWindow.sizeVector;
                    ticketAnchor = new Vector2(body.xMax * Screen.width / Mathf.Max(1, size.x) + 14,
                        body.yMin * Screen.height / Mathf.Max(1, size.y));
                }
            }
            if (ticket.gameObject.activeSelf != show) ticket.gameObject.SetActive(show);
            if (!show) return;
            bool compact = Time.unscaledTime - ticketStarted > 8;
            var first = attention.items[0];
            string label = first.project;
            if (string.IsNullOrWhiteSpace(label)) label = first.label;
            if (string.IsNullOrWhiteSpace(label)) label = "OMP";
            if (!changed && compact == ticketCompact && ticketItemCount == attention.items.Length && ticketLabel == label) return;
            ticketCompact = compact; ticketItemCount = attention.items.Length; ticketLabel = label;
            ticketTitle.text = CompanionUi.Short(label, 30);
            ticketReason.text = first.kind == "question" ? "Нужен твой выбор" : "Есть ответ";
            ticketCount.text = attention.items.Length > 1 ? "+" + (attention.items.Length - 1) : "";
            ticket.sizeDelta = new Vector2(compact ? 240 : 300, compact ? 38 : 88);
            ticketTitle.gameObject.SetActive(!compact); ticketReason.gameObject.SetActive(!compact); ticketCount.gameObject.SetActive(!compact);
            var read = ticket.GetComponentInChildren<Button>();
            CompanionUi.Top(read.GetComponent<RectTransform>(), compact ? 0 : 208, compact ? 0 : 46, compact ? 240 : 82, compact ? 38 : 30);
            read.GetComponentInChildren<Text>().text = compact ? CompanionUi.Short(label, 26) + " · ответ" : "Читать";
        }
        void PlaceBubble(RectTransform bubble, Vector2 anchor)
        {
            if (!bubble.gameObject.activeSelf) return;
            var wm = WindowManager.Instance;
            var manager = wm == null ? null : wm.CompanionManager;
            if (manager == null || manager.CompanionWindow == null || manager.Monitors == null) return;
            var window = manager.CompanionWindow;
            var monitor = Array.Find(manager.Monitors, item => item.id == window.monitor);
            if (monitor == null) return;
            var position = manager.GetWindowPosition(); var size = window.sizeVector;
            float sx = Screen.width / (float)Mathf.Max(1, size.x), sy = Screen.height / (float)Mathf.Max(1, size.y);
            var area = monitor.workArea;
            float left = Mathf.Max(0, (area.xMin - position.x) * sx) + 12;
            float right = Mathf.Min(Screen.width, (area.xMax - position.x) * sx) - 12;
            float top = Mathf.Max(0, (area.yMin - position.y) * sy) + 12;
            float bottom = Mathf.Min(Screen.height, (area.yMax - position.y) * sy) - 12;
            float x = anchor.x;
            if (x + bubble.sizeDelta.x > right) x = manager.BodyBounds.xMin * sx - bubble.sizeDelta.x - 14;
            bubble.anchoredPosition = new Vector2(Mathf.Clamp(x, left, Mathf.Max(left, right - bubble.sizeDelta.x)),
                -Mathf.Clamp(anchor.y, top, Mathf.Max(top, bottom - bubble.sizeDelta.y)));
        }
        void LateUpdate()
        {
            PlaceBubble(ticket, ticketAnchor);
            var contextCaption = reaction == null ? "" : reaction.ContextCaption;
            bool aside = !string.IsNullOrEmpty(contextCaption) && !pointerHeld && !MenuActions.IsAnyMenuOpen() && !TutorialMenu.IsActive;
            if (speech.gameObject.activeSelf != aside) speech.gameObject.SetActive(aside);
            if (aside)
            {
                if (speechText.text != contextCaption) speechText.text = contextCaption;
                var manager = WindowManager.Instance?.CompanionManager;
                if (manager?.CompanionWindow != null)
                {
                    var body = manager.BodyBounds; var size = manager.CompanionWindow.sizeVector;
                    PlaceBubble(speech, new Vector2(body.xMax * Screen.width / Mathf.Max(1, size.x) + 14,
                        body.yMin * Screen.height / Mathf.Max(1, size.y) + 20));
                }
            }
            if (menu != null)
            {
                bool open = MenuActions.IsAnyMenuOpen();
                if (open && !followFrozen) { savedFollow = menu.followBone; menu.followBone = false; followFrozen = true; }
                else if (!open && followFrozen) { menu.followBone = savedFollow; followFrozen = false; }
            }
            if (radial != null)
            {
                bool visible = radial.transform.localScale.sqrMagnitude >= .0001f;
                if (radialWasVisible && !visible && reopenRadialAt < 0 && radial.Buttons != dailyRadial) radial.Buttons = dailyRadial;
                radialWasVisible = visible;
            }
            if (inbox == null || WindowManager.Instance == null) return;
            if (WindowManager.Instance.CompanionManager != null)
                WindowManager.Instance.CompanionManager.CompanionUiOpen = inbox.IsOpen || popup.gameObject.activeInHierarchy;
            inputRegions.Clear(); AddRegion(ticket); AddRegion(inbox.Root); AddRegion(popup);
            bool changed = inputRegions.Count != previousRegions.Length;
            if (!changed) for (int i = 0; i < inputRegions.Count; i++) if (inputRegions[i] != previousRegions[i]) { changed = true; break; }
            if (changed) { previousRegions = inputRegions.ToArray(); WindowManager.Instance.SetCompanionInputRegions(previousRegions); }
        }
        void AddRegion(RectTransform rect)
        {
            if (rect == null || !rect.gameObject.activeInHierarchy) return;
            inputRegions.Add(new Rect(rect.position.x, Screen.height - rect.position.y, rect.rect.width, rect.rect.height));
        }
        void InstallRadial()
        {
            var selector = menu.radialMenuObject == null ? null : menu.radialMenuObject.GetComponent<Xamin.CircleSelector>();
            if (selector == null || selector.buttonSource != Xamin.CircleSelector.ButtonSource.prefabs) { Debug.LogError("[MascotControl] Stock radial inventory unavailable"); return; }
            radial = selector; stockRadial = new List<GameObject>(selector.Buttons);
            GameObject chat = null, settings = null;
            foreach (var item in stockRadial)
            {
                var button = item == null ? null : item.GetComponent<Xamin.Button>();
                if (button == null) continue;
                if (button.id == "settings") settings = item;
                if (button.id != "chat") continue;
                chat = item; button.id = "omp.inbox"; button.image = CompanionIcons.Get("omp"); button.unlocked = true;
                var action = item.AddComponent<DesktopMenuAction>(); action.destination = "omp.inbox";
                button.action = new UnityEngine.Events.UnityEvent(); button.action.AddListener(OpenNativeMenu);
            }
            if (chat == null || settings == null) { Debug.LogError("[MascotControl] Required stock chat/settings slots missing"); return; }
            selector.AccentColor = CompanionUi.Ink; selector.BackgroundColor = CompanionUi.Surface; selector.DisabledColor = CompanionUi.Muted;
            selector.tiltTowardsMouse = false;
            selector.rotateButtons = true; selector.UseSeparators = false;
            var skin = selector.gameObject.AddComponent<CompanionRadialSkin>(); skin.Initialize(selector);
            selector.Buttons = new List<GameObject> { chat };
            AddRadial(selector, "companion.avatar", "Персонаж", "avatar");
            selector.Buttons.Add(settings);
            AddRadial(selector, "companion.home", "Домой", "home");
            AddRadial(selector, "companion.actions", "Быстрые действия", "actions");
            AddRadial(selector, "companion.behaviour", "Поведение", "quiet");
            dailyRadial = selector.Buttons;
            selector.Buttons = new List<GameObject>(stockRadial);
            selector.Buttons.Remove(chat); selector.Buttons.Remove(settings);
            AddRadial(selector, "companion.back", "Назад", "back");
            avatarRadial = selector.Buttons; selector.Buttons = dailyRadial;
        }
        GameObject AddRadial(Xamin.CircleSelector selector, string id, string title, string glyph)
        {
            if (selector.Buttons.Count >= 12) throw new InvalidOperationException("Radial action page exceeds native capacity");
            var holder = new GameObject(title + " template"); holder.transform.SetParent(transform, false); holder.SetActive(false);
            var item = new GameObject(title, typeof(RectTransform), typeof(Image), typeof(Xamin.Button)); item.transform.SetParent(holder.transform, false);
            item.GetComponent<RectTransform>().sizeDelta = new Vector2(42, 42);
            var button = item.GetComponent<Xamin.Button>(); button.id = id; button.unlocked = true; button.image = CompanionIcons.Get(glyph);
            item.GetComponent<Image>().sprite = button.image;
            var action = item.AddComponent<DesktopMenuAction>(); action.destination = id; action.caption = title;
            selector.Buttons.Add(item); return item;
        }
        static bool SameActions(ControlAction[] left, ControlAction[] right, bool inventoryOnly)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i].id != right[i].id || left[i].label != right[i].label || left[i].group != right[i].group ||
                    left[i].confirmation != right[i].confirmation ||
                    (!inventoryOnly && (left[i].available != right[i].available || left[i].reason != right[i].reason))) return false;
            return true;
        }
        ControlAction FindAction(string id)
        {
            if (actionInventory != null) foreach (var action in actionInventory) if (action.id == id) return action;
            return null;
        }
        static bool InActionRing(ControlAction action, string kind)
        {
            if (action.group == "services") return false;
            bool project = action.id.StartsWith("project.open.", StringComparison.Ordinal);
            bool session = action.id.StartsWith("session.new.", StringComparison.Ordinal);
            return kind == "projects" ? project : kind == "sessions" ? session : !project && !session;
        }
        int ActionCount(string kind)
        {
            int count = 0;
            if (actionInventory != null) foreach (var action in actionInventory) if (InActionRing(action, kind)) count++;
            return count;
        }
        void UpdateActionRing(ControlSnapshot snapshot)
        {
            bool resultChanged = false;
            var response = client.ActionResponse;
            if (!ReferenceEquals(response, observedActionResponse))
            {
                observedActionResponse = response;
                if (response != null && response.requestMethod == "run")
                {
                    lastActionId = response.requestAction;
                    lastActionUnknown = response.outcomeUnknown;
                    lastActionResult = response.outcomeUnknown ? "Результат неизвестен. Повтор — с подтверждением."
                        : response.ok ? response.message : "Не выполнено: " + response.message;
                    resultChanged = true;
                }
            }
            var actions = snapshot == null ? null : snapshot.actions;
            bool inventoryChanged = !SameActions(actionInventory, actions, true);
            bool changed = resultChanged || actionFresh != client.Fresh || actionBusy != client.Busy ||
                !SameActions(actionInventory, actions, false);
            actionInventory = actions; actionFresh = client.Fresh; actionBusy = client.Busy;
            if (!changed || actionRadial == null) return;
            if (radial.Buttons == actionRadial && inventoryChanged && radialConfirmation == null) OpenActions(actionKind, actionPage);
            else RefreshActionStates();
        }
        void ClearActionTemplates()
        {
            foreach (var holder in actionTemplates) if (holder != null) Object.Destroy(holder);
            actionTemplates.Clear();
            actionRadial = new List<GameObject>(); radial.Buttons = actionRadial;
        }
        GameObject AddActionEntry(string id, string title, string glyph)
        {
            var item = AddRadial(radial, id, title, glyph);
            actionTemplates.Add(item.transform.parent.gameObject);
            return item;
        }
        static string ActionGlyph(ControlAction action)
        {
            if (action.id == "factories") return "factory";
            if (action.id == "comfyui") return "nodes";
            if (action.id.StartsWith("project.open.", StringComparison.Ordinal)) return "folder";
            if (action.id.StartsWith("session.new.", StringComparison.Ordinal)) return "new";
            return "link";
        }
        void OpenActions(string kind, int page)
        {
            if (radial == null) return;
            SetInbox(false); ClosePopup(); menu.CloseAllMenus(); reaction.Cancel();
            actionKind = kind; radialConfirmation = null;
            ClearActionTemplates();
            const int pageSize = 6;
            int count = ActionCount(kind), pages = Math.Max(1, (count + pageSize - 1) / pageSize);
            actionPage = Math.Max(0, Math.Min(page, pages - 1));
            int index = 0;
            if (actionInventory != null) foreach (var action in actionInventory)
            {
                if (!InActionRing(action, kind)) continue;
                int slot = index++;
                if (slot < actionPage * pageSize || slot >= (actionPage + 1) * pageSize) continue;
                AddActionEntry("run:" + action.id, action.label, ActionGlyph(action));
            }
            if (kind == "quick")
            {
                if (ActionCount("projects") > 0) AddActionEntry("companion.projects", "Открыть проект", "folder");
                if (ActionCount("sessions") > 0) AddActionEntry("companion.sessions", "Новая OMP-сессия", "new");
            }
            if (pages > 1)
            {
                AddActionEntry("companion.actions.previous", "Предыдущая страница", "back")
                    .GetComponent<Xamin.Button>().unlocked = actionPage > 0;
                AddActionEntry("companion.actions.next", "Следующая страница", "forward")
                    .GetComponent<Xamin.Button>().unlocked = actionPage + 1 < pages;
            }
            if (actionRadial.Count == 0)
                AddActionEntry("companion.actions.empty", "Нет настроенных действий", "actions").GetComponent<Xamin.Button>().unlocked = false;
            AddActionEntry("companion.actions.back", kind == "quick" ? "Назад в главное меню" : "Назад к быстрым действиям", "back");
            RefreshActionStates();
            radialWasVisible = false; reopenRadialAt = Time.frameCount + 1;
        }
        void SelectAction(string id)
        {
            var action = FindAction(id);
            if (action == null || !client.Fresh || client.Busy || !action.available) return;
            if (!action.confirmation && !(lastActionUnknown && lastActionId == id)) { QueueAction(action, false); return; }
            radialConfirmation = action;
            ClearActionTemplates();
            AddActionEntry("companion.actions.confirm", "Подтвердить: " + action.label, "confirm");
            AddActionEntry("companion.actions.cancel", "Отмена", "back");
            RefreshActionStates();
            radialWasVisible = false; reopenRadialAt = Time.frameCount + 1;
        }
        void QueueAction(ControlAction requested, bool confirmed)
        {
            var action = FindAction(requested.id);
            if (action == null || !client.Fresh || client.Busy || !action.available) { OpenActions(actionKind, actionPage); return; }
            if (client.Run(action.id, confirmed))
            {
                lastActionId = action.id; lastActionUnknown = false; lastActionResult = "Выполняется…";
            }
            OpenActions(actionKind, actionPage);
        }
        void RefreshActionEntry(GameObject item)
        {
            var entry = item.GetComponent<DesktopMenuAction>();
            var button = item.GetComponent<Xamin.Button>();
            if (entry == null || button == null) return;
            if (button.id.StartsWith("run:", StringComparison.Ordinal))
            {
                var action = FindAction(button.id.Substring(4));
                button.unlocked = action != null && client.Fresh && !client.Busy && action.available;
                if (action == null) { entry.caption = "Действие больше не настроено"; return; }
                string detail = !client.Fresh ? "Нет связи с локальным адаптером"
                    : !action.available ? action.reason
                    : client.Busy ? (lastActionId == action.id ? "Выполняется…" : "Дождись завершения текущего действия")
                    : lastActionId == action.id ? lastActionResult : "";
                entry.caption = string.IsNullOrEmpty(detail) ? action.label : action.label + "\n" + detail;
            }
            else if (button.id == "companion.actions.confirm" && radialConfirmation != null)
            {
                var action = FindAction(radialConfirmation.id);
                button.unlocked = action != null && client.Fresh && !client.Busy && action.available;
                entry.caption = "Подтвердить: " + radialConfirmation.label +
                    (lastActionUnknown && lastActionId == radialConfirmation.id ? "\nПредыдущий результат неизвестен" : "");
            }
        }
        void RefreshActionStates()
        {
            if (actionRadial == null) return;
            foreach (var item in actionRadial) if (item != null) RefreshActionEntry(item);
            if (radial.Buttons != actionRadial) return;
            var buttons = radial.transform.Find("Buttons");
            for (int i = 0; i < buttons.childCount; i++) RefreshActionEntry(buttons.GetChild(i).gameObject);
        }
        void SwitchRadial(bool avatar)
        {
            if (radial == null) return;
            radial.Buttons = avatar ? avatarRadial : dailyRadial;
            radialWasVisible = false;
            reopenRadialAt = Time.frameCount + 1;
        }
        public static void OpenNativeMenu() { if (instance != null) instance.SetInbox(true, true); }
        public static void ExecuteMenu(string destination)
        {
            if (instance == null) return;
            switch (destination)
            {
                case "companion.home": instance.SetInbox(false); instance.ClosePopup(); instance.reaction.Home(); break;
                case "companion.actions": instance.OpenActions("quick", 0); break;
                case "companion.behaviour": instance.OpenPopup("behaviour"); break;
                case "companion.avatar": instance.SwitchRadial(true); break;
                case "companion.back": instance.SwitchRadial(false); break;
                case "companion.projects": instance.OpenActions("projects", 0); break;
                case "companion.sessions": instance.OpenActions("sessions", 0); break;
                case "companion.actions.back":
                    if (instance.actionKind == "quick") instance.SwitchRadial(false); else instance.OpenActions("quick", 0);
                    break;
                case "companion.actions.previous": instance.OpenActions(instance.actionKind, instance.actionPage - 1); break;
                case "companion.actions.next": instance.OpenActions(instance.actionKind, instance.actionPage + 1); break;
                case "companion.actions.cancel": instance.OpenActions(instance.actionKind, instance.actionPage); break;
                case "companion.actions.confirm":
                    if (instance.radialConfirmation != null) instance.QueueAction(instance.radialConfirmation, true);
                    break;
                case "companion.actions.empty": break;
                default:
                    if (destination.StartsWith("run:", StringComparison.Ordinal)) instance.SelectAction(destination.Substring(4));
                    else instance.SetInbox(true);
                    break;
            }
        }
        void OnApplicationQuit() { if (client != null) client.Dispose(); }
        void OnDestroy()
        {
            WindowManager.Instance?.CompanionManager?.ReleaseKeyboard();
            if (WindowManager.Instance?.CompanionManager != null) WindowManager.Instance.CompanionManager.CompanionUiOpen = false;
            if (client != null) client.Dispose();
            if (menu != null)
            {
                if (followFrozen) menu.followBone = savedFollow;
                menu.menuEntries.Remove(inboxEntry); menu.menuEntries.Remove(popupEntry);
                var selector = menu.radialMenuObject == null ? null : menu.radialMenuObject.GetComponent<Xamin.CircleSelector>();
                if (selector != null && stockRadial != null) selector.Buttons = stockRadial;
            }
            if (instance == this) instance = null;
        }
    }
    public sealed class DesktopMenuAction : MonoBehaviour
    {
        public string destination;
        public string caption;
        void Awake() { Bind(); }
        void Start() { Bind(); }
        void Bind()
        {
            var button = GetComponent<Xamin.Button>(); if (button == null) return;
            string id = string.IsNullOrEmpty(destination) ? button.id : destination;
            button.action = new UnityEngine.Events.UnityEvent(); button.action.AddListener(() => DesktopPanel.ExecuteMenu(id));
        }
    }
}
