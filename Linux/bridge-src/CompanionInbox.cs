using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MateEngine.Desktop
{
    internal sealed class CompanionInbox
    {
        sealed class SessionState
        {
            internal string draft = "", requestId, submittedText, submissionState, error, runtimeEpoch;
            internal int revision;
            internal float scroll = 1;
            internal bool clipped, hasNew, busy, loaded;
            internal readonly List<CompanionMessage> messages = new List<CompanionMessage>();
        }
        readonly DesktopClient client;
        readonly Dictionary<string, SessionState> states = new Dictionary<string, SessionState>();
        readonly List<CompanionSession> ordered = new List<CompanionSession>();
        readonly RectTransform root, tabs, header, overflow, transcriptContent;
        readonly ScrollRect transcript;
        readonly InputField composer, search;
        readonly Text target, status, deliveryLabel;
        readonly Button send, modeButton, newer;
        readonly Action close;
        CompanionSession selected;
        int inventoryRevision = -1, resultRevision = -1, focusRevision;
        string acknowledgedEvent, pendingViewEvent, observingEvent;
        bool rebuilding, steer, observedOpen;
        string tabSignature, attentionToken, requestedSession;
        readonly HashSet<string> unread = new HashSet<string>();
        float viewedSince = -1, width = 520, queryAt;
        CompanionSession controlsSession;
        string controlsDraft, controlsRequest, controlsSubmission, controlsError, controlsBridgeError;
        bool controlsBusy, controlsConnected, controlsSteer, controlsSessionBusy;
        internal RectTransform Root { get { return root; } }
        internal bool IsOpen { get { return root.gameObject.activeSelf; } }
        internal CompanionUiObservation Audition(CompanionUiCommand command)
        {
            switch (command.operation)
            {
                case "select":
                    int index = ordered.FindIndex(item => item.sessionId == command.value);
                    if (index < 0 || index >= tabs.childCount - 1) throw new InvalidOperationException("Requested session tab is not visible");
                    tabs.GetChild(index).GetComponent<Button>().onClick.Invoke();
                    break;
                case "type":
                    if (!composer.interactable) throw new InvalidOperationException("Composer is not editable");
                    if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(composer.gameObject);
                    composer.ActivateInputField();
                    foreach (char value in command.value ?? "")
                        composer.ProcessEvent(new Event { type = EventType.KeyDown, character = value, keyCode = value == '\n' ? KeyCode.Return : KeyCode.None });
                    break;
                case "send":
                    RefreshControls();
                    if (!send.interactable) throw new InvalidOperationException("Send button is not enabled");
                    send.onClick.Invoke();
                    break;
                case "overflow":
                    tabs.GetChild(tabs.childCount - 1).GetComponent<Button>().onClick.Invoke();
                    break;
                case "search": search.text = command.value ?? ""; break;
                case "scroll":
                    transcript.verticalNormalizedPosition = Mathf.Clamp01(float.Parse(command.value, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "resize":
                    Resize(Mathf.Clamp(float.Parse(command.value, System.Globalization.CultureInfo.InvariantCulture), 360, 640), 460);
                    break;
                case "close": close(); break;
                case "state": break;
                default: throw new InvalidOperationException("Unknown inbox audition operation");
            }
            Canvas.ForceUpdateCanvases();
            return new CompanionUiObservation { open = IsOpen, sessionId = selected == null ? "" : selected.sessionId,
                runtimeEpoch = selected == null ? "" : selected.runtimeEpoch, draft = composer.text,
                submission = State == null ? "" : State.submissionState, scroll = transcript.verticalNormalizedPosition };
        }
        SessionState State
        {
            get
            {
                if (selected == null) return null;
                SessionState value;
                if (!states.TryGetValue(selected.sessionId, out value)) states[selected.sessionId] = value = new SessionState();
                return value;
            }
        }
        internal CompanionInbox(Transform canvas, DesktopClient client, Action close)
        {
            this.client = client; this.close = close;
            root = CompanionUi.Panel("OMP conversations", canvas); CompanionUi.Top(root, 18, 72, 520, 460);
            header = CompanionUi.Rect("OMP header", root); CompanionUi.Top(header, 14, 8, 492, 30);
            var title = CompanionUi.Text(header, "OMP", 20); CompanionUi.Top(title.rectTransform, 0, 0, 130, 28);
            var closeButton = CompanionUi.Button(header, "×", () => close()); CompanionUi.Top(closeButton.GetComponent<RectTransform>(), 456, 0, 32, 28);
            tabs = CompanionUi.Rect("Stable session tabs", root); CompanionUi.Top(tabs, 14, 45, 492, 36);
            transcript = CompanionUi.Scroll(root, out transcriptContent);
            target = CompanionUi.Text(root, "Выбери сессию", 14, CompanionUi.Muted);
            composer = CompanionUi.Input(root, "Ответить этой сессии");
            composer.gameObject.AddComponent<CompanionKeyboardIntent>();
            composer.onValueChanged.AddListener(value => { if (!rebuilding && State != null) State.draft = value; });
            deliveryLabel = CompanionUi.Text(root, "", 13, CompanionUi.Muted);
            modeButton = CompanionUi.Button(root, "В очередь", () => { steer = !steer; RefreshControls(); });
            send = CompanionUi.Button(root, "Отправить", Send);
            status = CompanionUi.Text(root, "Подключение к OMP…", 13, CompanionUi.Muted);
            newer = CompanionUi.Button(root, "Новый ответ ниже", () => { transcript.verticalNormalizedPosition = 0; if (State != null) State.hasNew = false; });
            overflow = CompanionUi.Panel("Все OMP-сессии", root); CompanionUi.Top(overflow, 160, 83, 330, 260);
            search = CompanionUi.Input(overflow, "Найти сессию"); CompanionUi.Top(search.GetComponent<RectTransform>(), 8, 8, 314, 34);
            search.gameObject.AddComponent<CompanionKeyboardIntent>();
            search.lineType = InputField.LineType.SingleLine; search.onValueChanged.AddListener(_ => BuildOverflow());
            overflow.gameObject.SetActive(false); root.gameObject.SetActive(false);
            Resize(520, 460);
            RenderMessages(false);
        }
        internal void SetOpen(bool open)
        {
            if (IsOpen == open && observedOpen == open) return;
            Save(); root.gameObject.SetActive(open); overflow.gameObject.SetActive(false); observedOpen = open;
            client.ConversationVisible(open); focusRevision++; viewedSince = -1; acknowledgedEvent = null;
            if (open) { SyncInventory(); RefreshControls(); }
        }
        internal void Resize(float w, float h)
        {
            width = w; root.sizeDelta = new Vector2(w, h); header.sizeDelta = new Vector2(w - 28, 30);
            CompanionUi.Top(header.GetChild(1).GetComponent<RectTransform>(), w - 64, 0, 32, 28);
            tabs.sizeDelta = new Vector2(w - 28, 36);
            CompanionUi.Top(transcript.GetComponent<RectTransform>(), 14, 91, w - 28, h - 267);
            CompanionUi.Top(target.rectTransform, 18, h - 170, w - 36, 23);
            CompanionUi.Top(composer.GetComponent<RectTransform>(), 14, h - 145, w - 28, 72);
            CompanionUi.Top(modeButton.GetComponent<RectTransform>(), 14, h - 65, 122, 34);
            CompanionUi.Top(deliveryLabel.rectTransform, 146, h - 63, Mathf.Max(20, w - 282), 32);
            CompanionUi.Top(send.GetComponent<RectTransform>(), w - 126, h - 65, 112, 34);
            CompanionUi.Top(status.rectTransform, 16, h - 26, w - 32, 22);
            CompanionUi.Top(newer.GetComponent<RectTransform>(), Mathf.Max(14, w - 205), h - 214, 185, 29);
            CompanionUi.Top(overflow, Mathf.Max(8, w - 344), 83, Mathf.Min(330, w - 16), Mathf.Min(260, h - 100));
            if (IsOpen) { BuildTabs(); RenderMessages(false); }
        }
        void Save()
        {
            var state = State; if (state == null) return; state.draft = composer.text; state.scroll = transcript.verticalNormalizedPosition;
        }
        internal void ShowSession(string id)
        {
            requestedSession = CanonicalSession(id); Save(); selected = null; client.SelectSession(null);
            SyncInventory(); RenderMessages(false); RefreshControls();
        }
        static string CanonicalSession(string id) { return id != null && id.StartsWith("omp:", StringComparison.Ordinal) ? id.Substring(4) : id; }
        void Select(CompanionSession session)
        {
            var current = ordered.Find(item => item.Identity == session.Identity);
            if (current != null) session = current;
            requestedSession = null;
            Save(); selected = session; steer = false; focusRevision++; acknowledgedEvent = null; viewedSince = -1;
            var state = State;
            if (state.runtimeEpoch != session.runtimeEpoch)
            {
                state.runtimeEpoch = session.runtimeEpoch; state.revision = 0; state.messages.Clear(); state.clipped = state.hasNew = state.busy = state.loaded = false;
                if (state.requestId != null) { state.submissionState = "unknown"; state.error = "OMP перезапущен. Доставка прежнего сообщения неизвестна; автоматического повтора нет."; }
            }
            overflow.gameObject.SetActive(false); client.SelectSession(session);
            rebuilding = true; composer.text = State == null ? "" : State.draft; rebuilding = false;
            BuildTabs(); RenderMessages(false); RefreshControls();
        }
        void SyncInventory()
        {
            var available = client.Sessions;
            var byKey = new Dictionary<string, CompanionSession>();
            foreach (var item in available) byKey[item.Identity] = item;
            for (int i = ordered.Count - 1; i >= 0; i--)
            {
                CompanionSession current;
                if (byKey.TryGetValue(ordered[i].Identity, out current))
                {
                    SessionState state;
                    if (states.TryGetValue(current.sessionId, out state)) current.busy = state.busy;
                    ordered[i] = current;
                }
                else ordered.RemoveAt(i);
            }
            foreach (var item in available) if (!ordered.Exists(value => value.Identity == item.Identity)) ordered.Add(item);
            if (requestedSession != null)
            {
                var requested = ordered.Find(item => item.sessionId == requestedSession);
                if (requested != null) Select(requested);
            }
            else if (selected == null && ordered.Count > 0) Select(ordered[0]);
            else if (selected != null)
            {
                CompanionSession current;
                if (byKey.TryGetValue(selected.Identity, out current)) selected = current;
                else { selected.presence = "disconnected"; selected.canReply = false; }
            }
            RefreshTabs(); if (overflow.gameObject.activeSelf) BuildOverflow();
        }
        void RefreshTabs()
        {
            var signature = new StringBuilder();
            signature.Append(width).Append('|').Append(selected == null ? "" : selected.Identity);
            foreach (var item in ordered) signature.Append('|').Append(item.Identity).Append(':').Append(CompanionUi.SessionTitle(item)).Append(unread.Contains(item.sessionId) ? "!" : ".");
            string value = signature.ToString();
            if (tabSignature == value) return;
            tabSignature = value; BuildTabs();
        }
        void BuildTabs()
        {
            CompanionUi.Clear(tabs);
            int visible = Mathf.Min(width >= 620 ? 4 : 3, Mathf.Max(1, ordered.Count));
            float slot = (width - 86) / visible;
            for (int i = 0; i < Mathf.Min(visible, ordered.Count); i++)
            {
                var item = ordered[i]; bool active = selected != null && selected.Identity == item.Identity;
                string title = CompanionUi.SessionTitle(item);
                bool duplicate = ordered.Exists(other => other.Identity != item.Identity && CompanionUi.SessionTitle(other) == title);
                string label = (unread.Contains(item.sessionId) ? "• " : "") + CompanionUi.Short(title, width < 420 ? 5 : 9) +
                    (duplicate ? " · " + item.sessionId.Substring(Mathf.Max(0, item.sessionId.Length - 4)) : "");
                var button = CompanionUi.Button(tabs, label, () => Select(item));
                CompanionUi.Top(button.GetComponent<RectTransform>(), i * slot, 0, slot - 4, 34);
                if (active) { var line = CompanionUi.Rect("Selected session", button.transform); CompanionUi.Fill(line, 0, 0, 0, 31); CompanionUi.Background(line, CompanionUi.Accent, false); }
            }
            var more = CompanionUi.Button(tabs, "…", () => { overflow.gameObject.SetActive(!overflow.gameObject.activeSelf); if (overflow.gameObject.activeSelf) { BuildOverflow(); search.ActivateInputField(); } });
            CompanionUi.Top(more.GetComponent<RectTransform>(), width - 76, 0, 48, 34);
        }
        void BuildOverflow()
        {
            for (int i = overflow.childCount - 1; i > 0; i--) { var go = overflow.GetChild(i).gameObject; go.SetActive(false); UnityEngine.Object.Destroy(go); }
            RectTransform content; var list = CompanionUi.Scroll(overflow, out content); CompanionUi.Top(list.GetComponent<RectTransform>(), 8, 48, overflow.sizeDelta.x - 16, overflow.sizeDelta.y - 56);
            string query = search.text ?? "";
            foreach (var item in ordered)
            {
                string label = CompanionUi.SessionTitle(item) + " · " + CompanionUi.Short(item.sessionId, 8);
                if (query.Length > 0 && label.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var captured = item; CompanionUi.Button(content, label, () => Select(captured));
            }
            if (ordered.Count == 0) { var empty = CompanionUi.Text(content, "Нет подключённых OMP-сессий", 14); CompanionUi.Size(empty.gameObject, 40); }
        }
        void Send()
        {
            var state = State; if (state == null || selected == null || !selected.canReply || client.Busy) return;
            if (!string.IsNullOrEmpty(state.requestId)) { client.SubmissionStatus(selected, state.requestId); return; }
            if (string.IsNullOrWhiteSpace(composer.text)) return;
            if (Encoding.UTF8.GetByteCount(composer.text) > 32768) { state.error = "Сообщение больше 32 КиБ. Сократи его; черновик сохранён."; RefreshControls(); return; }
            string id = Guid.NewGuid().ToString(); string mode = selected.busy ? (steer ? "steer" : "queue") : "prompt";
            if (client.Send(selected, composer.text, id, mode, state.revision))
            { state.requestId = id; state.submittedText = composer.text; state.submissionState = "sending"; state.error = null; RefreshControls(); }
        }
        void HandleSubmission()
        {
            var response = client.ActionResponse;
            if (response == null) return;
            if (response.requestMethod == "inbox.viewed")
            {
                if (response.ok && response.viewed && response.event_id == pendingViewEvent) acknowledgedEvent = pendingViewEvent;
                pendingViewEvent = null; viewedSince = -1;
            }
            var submission = response.submission;
            foreach (var state in states.Values)
            {
                if (state.requestId == null) continue;
                if (submission == null || submission.requestId != state.requestId) continue;
                state.submissionState = submission.state;
                if (submission.state == "rejected") { state.requestId = null; state.error = submission.message ?? response.message; }
                if (submission.state == "observed") AcknowledgeSend(state);
            }
            if (State != null && composer.text != State.draft) { rebuilding = true; composer.text = State.draft; rebuilding = false; }
        }
        static void AcknowledgeSend(SessionState state)
        {
            if (state.draft == state.submittedText) state.draft = "";
            state.requestId = null; state.submittedText = null; state.submissionState = null; state.error = null;
        }
        internal void Tick(ControlSnapshot snapshot)
        {
            if (!IsOpen)
            {
                if (observedOpen) { Save(); observedOpen = false; client.ConversationVisible(false); focusRevision++; viewedSince = -1; }
                return;
            }
            if (!observedOpen) { observedOpen = true; client.ConversationVisible(true); focusRevision++; }
            var attention = snapshot == null ? null : snapshot.attention;
            string token = attention == null ? "" : attention.token;
            if (attentionToken != token)
            {
                attentionToken = token; unread.Clear();
                if (attention != null && attention.items != null)
                    foreach (var item in attention.items) if (!string.IsNullOrEmpty(item.session_id)) unread.Add(CanonicalSession(item.session_id));
                RefreshTabs();
            }
            if (inventoryRevision != client.SessionRevision) { inventoryRevision = client.SessionRevision; SyncInventory(); }
            if (resultRevision != client.ResultRevision) { resultRevision = client.ResultRevision; HandleSubmission(); }
            ConversationPage page;
            bool changed = false;
            while (client.TryReadPage(out page))
            {
                if (selected == null || page.sessionId != selected.sessionId || page.runtimeEpoch != selected.runtimeEpoch) continue;
                var state = State; state.revision = page.messageRevision; state.busy = selected.busy = page.busy;
                if (!state.loaded) { state.loaded = true; changed = true; }
                foreach (var message in page.messages)
                {
                    int index = state.messages.FindIndex(value => value.id == message.id);
                    if (index >= 0 && state.messages[index].revision >= message.revision) continue;
                    if (index >= 0) state.messages[index] = message; else state.messages.Add(message);
                    changed = true;
                }
                if (state.messages.Count > 512) { state.messages.RemoveRange(0, state.messages.Count - 512); state.clipped = true; }
            }
            if (changed) RenderMessages(true);
            if (State != null && composer.text != State.draft && !composer.isFocused) { rebuilding = true; composer.text = State.draft; rebuilding = false; }
            if (Application.isFocused)
            {
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                if (composer.isFocused && ctrl && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) && string.IsNullOrEmpty(Input.compositionString)) Send();
                if (ctrl && Input.GetKeyDown(KeyCode.Tab) && ordered.Count > 0) { int index = ordered.FindIndex(item => selected != null && item.Identity == selected.Identity); Select(ordered[(index + 1) % ordered.Count]); }
                if (Input.GetKeyDown(KeyCode.Escape)) { if (overflow.gameObject.activeSelf) overflow.gameObject.SetActive(false); else close(); }
            }
            if (State != null && State.requestId != null && !client.Busy && Time.unscaledTime >= queryAt)
            {
                queryAt = Time.unscaledTime + 2; client.SubmissionStatus(selected, State.requestId);
            }
            RefreshControls(); ObserveViewed(snapshot);
        }
        void ObserveViewed(ControlSnapshot snapshot)
        {
            var state = State;
            bool bottom = transcript.verticalNormalizedPosition < .025f || transcriptContent.rect.height <= transcript.viewport.rect.height + 2;
            if (!Application.isFocused || selected == null || state == null || !bottom || client.SessionError != null || snapshot == null || snapshot.attention == null || snapshot.attention.items == null)
            { viewedSince = -1; return; }
            var item = Array.Find(snapshot.attention.items, value => value.session_id == "omp:" + selected.sessionId);
            if (item == null || item.id == acknowledgedEvent || pendingViewEvent != null || state.messages.Count == 0) return;
            if (observingEvent != item.id) { observingEvent = item.id; viewedSince = -1; }
            var last = state.messages[state.messages.Count - 1];
            if (last.role != "assistant" || !last.complete) return;
            if (viewedSince < 0) { viewedSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - viewedSince >= .35f && !client.Busy && client.Viewed(selected, item.id, state.revision, ++focusRevision)) pendingViewEvent = item.id;
        }
        void RefreshControls()
        {
            var state = State; bool connected = selected != null && selected.presence == "connected" && selected.canReply;
            bool pending = state != null && !string.IsNullOrEmpty(state.requestId);
            bool showNew = state != null && state.hasNew && transcript.verticalNormalizedPosition > .025f;
            if (newer.gameObject.activeSelf != showNew) newer.gameObject.SetActive(showNew);
            string error = state == null ? null : state.error;
            string request = state == null ? null : state.requestId;
            string submission = state == null ? null : state.submissionState;
            bool sessionBusy = selected != null && selected.busy;
            if (controlsSession == selected && controlsDraft == composer.text && controlsRequest == request &&
                controlsSubmission == submission && controlsError == error && controlsBridgeError == client.SessionError &&
                controlsBusy == client.Busy && controlsConnected == connected && controlsSteer == steer && controlsSessionBusy == sessionBusy) return;
            controlsSession = selected; controlsDraft = composer.text; controlsRequest = request; controlsSubmission = submission;
            controlsError = error; controlsBridgeError = client.SessionError; controlsBusy = client.Busy; controlsConnected = connected;
            controlsSteer = steer; controlsSessionBusy = sessionBusy;
            composer.interactable = connected && !pending;
            send.interactable = connected && !client.Busy && (pending || !string.IsNullOrWhiteSpace(composer.text));
            send.GetComponentInChildren<Text>().text = pending ? "Проверить" : "Отправить";
            modeButton.gameObject.SetActive(selected != null && selected.busy && !pending);
            modeButton.GetComponentInChildren<Text>().text = steer ? "Уточнить сейчас" : "В очередь";
            target.text = selected == null ? (requestedSession == null ? "Выбери сессию" : "Сессия " + CompanionUi.Short(requestedSession, 8) + " не подключена") : "Адресат: " + CompanionUi.SessionTitle(selected) + " · " + CompanionUi.Short(selected.sessionId, 8);
            deliveryLabel.text = pending ? SubmissionLabel(state.submissionState) : (selected != null && !string.IsNullOrEmpty(selected.model) ? CompanionUi.Short(selected.model, 18) : "");
            status.text = state != null && state.error != null ? state.error : client.SessionError ?? (!connected ? "Нет подключения. Сессии нужен mascot bridge." : "Ctrl+Enter · отправить     Enter · новая строка");
        }
        static string SubmissionLabel(string value)
        {
            switch (value) { case "queued": return "В очереди"; case "submitted": return "Передано OMP"; case "observed": return "В сессии"; case "sending": return "Отправляется…"; default: return "Доставка неизвестна"; }
        }
        void RenderMessages(bool incoming)
        {
            var state = State; float previous = transcript.verticalNormalizedPosition;
            bool stick = !incoming || previous < .025f || transcriptContent.rect.height <= transcript.viewport.rect.height + 2;
            CompanionUi.Clear(transcriptContent);
            if (state == null || state.messages.Count == 0)
            {
                var empty = CompanionUi.Text(transcriptContent, selected == null ? "Здесь появятся подключённые OMP-сессии.\nСтарые события не считаются живыми агентами." :
                    state != null && state.loaded ? "В этой сессии пока нет сообщений.\nНапиши агенту ниже." : "Сообщения сессии загружаются…", 15, CompanionUi.Muted);
                CompanionUi.Size(empty.gameObject, 68); return;
            }
            if (state.clipped) { var notice = CompanionUi.Text(transcriptContent, "Показаны последние 512 сообщений. Более ранние остаются в OMP.", 13, CompanionUi.Muted); CompanionUi.Size(notice.gameObject, 38); }
            foreach (var message in state.messages)
            {
                var heading = CompanionUi.Rect("Message heading", transcriptContent); CompanionUi.Size(heading.gameObject, 27);
                var who = CompanionUi.Text(heading, message.role == "user" ? "Ты" : "OMP", 13, message.role == "user" ? CompanionUi.Muted : CompanionUi.Accent); CompanionUi.Fill(who.rectTransform, 4, 0, 100, 0);
                string raw = message.text; var copy = CompanionUi.Button(heading, "Копировать", () => GUIUtility.systemCopyBuffer = raw);
                CompanionUi.Top(copy.GetComponent<RectTransform>(), Mathf.Max(80, width - 165), 0, 110, 25);
                copy.GetComponentInChildren<Text>().fontSize = 13;
                RenderMarkdown(message.text);
                if (message.truncated) { var label = CompanionUi.Text(transcriptContent, "Фрагмент ограничен источником; полный текст доступен в OMP.", 13, CompanionUi.Muted); CompanionUi.Size(label.gameObject, 38); }
            }
            Canvas.ForceUpdateCanvases(); transcript.verticalNormalizedPosition = incoming ? (stick ? 0 : previous) : state.scroll;
            if (incoming && !stick) state.hasNew = true;
        }
        void RenderMarkdown(string markdown)
        {
            bool code = false; var block = new StringBuilder();
            Action flush = () => { if (block.Length == 0) return; AddBlock(block.ToString().TrimEnd('\n'), code, 15); block.Length = 0; };
            foreach (string raw in (markdown ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith("```", StringComparison.Ordinal)) { flush(); code = !code; continue; }
                if (!code && line.StartsWith("#", StringComparison.Ordinal))
                {
                    flush(); int level = 0; while (level < line.Length && line[level] == '#') level++;
                    AddBlock(line.Substring(level).Trim(), false, level < 3 ? 19 : 16); continue;
                }
                if (!code) { line = Regex.Replace(line, @"\[([^\]]+)\]\(([^)]+)\)", "$1 ($2)"); line = line.Replace("**", "").Replace("__", "").Replace("`", ""); if (line.StartsWith("- ", StringComparison.Ordinal)) line = "• " + line.Substring(2); }
                block.Append(line).Append('\n');
            }
            flush();
        }
        void AddBlock(string value, bool code, int size)
        {
            if (value.Length == 0) return;
            var field = CompanionUi.Input(transcriptContent, "", true); field.text = value; field.textComponent.fontSize = size;
            if (code) { field.textComponent.font = CompanionUi.Mono; field.GetComponent<Image>().color = CompanionUi.Raised; }
            var settings = field.textComponent.GetGenerationSettings(new Vector2(Mathf.Max(180, width - 72), 0));
            float height = Mathf.Max(34, field.textComponent.cachedTextGeneratorForLayout.GetPreferredHeight(value, settings) / field.textComponent.pixelsPerUnit + 18);
            CompanionUi.Size(field.gameObject, height);
        }
    }
}
