using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MateEngine.Desktop
{
    internal sealed class DesktopClient : IDisposable
    {
        sealed class Connection : IDisposable
        {
            readonly string path;
            readonly object gate = new object();
            readonly byte[] buffer = new byte[4096];
            byte[] sendBuffer = new byte[4096];
            readonly StringBuilder requestText = new StringBuilder(256);
            readonly UTF8Encoding encoding = new UTF8Encoding(false, true);
            readonly MemoryStream responseBytes = new MemoryStream();
            Socket socket;
            bool disposed;
            internal bool MayHaveSent { get; private set; }

            internal Connection(string path) { this.path = path; }

            internal ControlResponse Exchange(ControlRequest request)
            {
                MayHaveSent = false;
                Socket current;
                bool connect = false;
                lock (gate)
                {
                    if (disposed) throw new ObjectDisposedException("Desktop connection");
                    if (socket == null)
                    {
                        socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        socket.SendTimeout = 3000;
                        connect = true;
                    }
                    current = socket;
                }
                if (connect)
                {
                    var task = current.ConnectAsync(new UnixDomainSocketEndPoint(path));
                    if (!task.Wait(3000)) throw new IOException("Desktop connect deadline exceeded");
                    task.GetAwaiter().GetResult();
                }
                current.ReceiveTimeout = request.method == "run" ? 65000 : 10000;
                string text = DesktopProtocol.Encode(request, requestText);
                int needed = encoding.GetByteCount(text);
                if (needed > 262144) throw new IOException("Desktop request exceeds limit");
                if (needed > sendBuffer.Length) sendBuffer = new byte[Math.Min(262144, Math.Max(needed, sendBuffer.Length * 2))];
                int length = encoding.GetBytes(text, 0, text.Length, sendBuffer, 0);
                for (int sent = 0; sent < length;)
                {
                    MayHaveSent = true;
                    int count = current.Send(sendBuffer, sent, length - sent, SocketFlags.None);
                    if (count <= 0) throw new EndOfStreamException();
                    sent += count;
                }
                responseBytes.SetLength(0);
                while (true)
                {
                    int count = current.Receive(buffer);
                    if (count == 0) throw new EndOfStreamException();
                    int newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
                    int used = newline < 0 ? count : newline;
                    if (responseBytes.Length + used > 262144) throw new IOException("Desktop response exceeds limit");
                    responseBytes.Write(buffer, 0, used);
                    if (newline < 0) continue;
                    if (newline != count - 1) throw new IOException("Unexpected extra response");
                    return DesktopProtocol.Decode(encoding.GetString(responseBytes.GetBuffer(), 0, (int)responseBytes.Length));
                }
            }

            internal void Reset()
            {
                lock (gate) { if (socket != null) socket.Close(); socket = null; }
            }

            public void Dispose()
            {
                lock (gate) { disposed = true; if (socket != null) socket.Close(); socket = null; }
            }
        }

        readonly object gate = new object();
        readonly AutoResetEvent actionWake = new AutoResetEvent(false);
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        readonly Thread poller, actor, sessionPoller;
        readonly Connection statusConnection, actionConnection, sessionConnection;
        readonly AutoResetEvent sessionWake = new AutoResetEvent(false);
        readonly Queue<ConversationPage> pages = new Queue<ConversationPage>();
        volatile CompanionSession[] sessions = new CompanionSession[0];
        volatile string sessionError, panelError;
        volatile ControlResponse actionResponse;
        string selectedSession, selectedEpoch;
        int selectedCursor, selectionVersion, sessionRevision;
        bool conversationVisible;
        ControlRequest pending;
        bool? visibility;
        volatile bool stopping, busy, surfaceVisible, engaged;
        volatile ControlSnapshot latest;
        volatile string result = "", connectionError;
        volatile bool actionSucceeded;
        int resultRevision;
        long receivedAt = -100000;

        public DesktopClient(string path)
        {
            statusConnection = new Connection(path);
            actionConnection = new Connection(path);
            sessionConnection = new Connection(path);
            sessionPoller = new Thread(PollSessions) { IsBackground = true, Name = "Mascot conversations" };
            poller = new Thread(Poll) { IsBackground = true, Name = "Mascot status" };
            actor = new Thread(Act) { IsBackground = true, Name = "Mascot actions" };
            poller.Start();
            actor.Start();
            sessionPoller.Start();
        }

        public ControlSnapshot Latest { get { return latest; } }
        public string Result { get { return result; } }
        public int ResultRevision { get { return Volatile.Read(ref resultRevision); } }
        public string Error { get { return connectionError; } }
        public bool Busy { get { return busy; } }
        public bool ActionSucceeded { get { return actionSucceeded; } }
        public bool Fresh { get { return clock.ElapsedMilliseconds - Interlocked.Read(ref receivedAt) < 6500; } }
        public CompanionSession[] Sessions { get { return sessions; } }
        public int SessionRevision { get { return Volatile.Read(ref sessionRevision); } }
        public string SessionError { get { return sessionError; } }
        public string PanelError { get { return panelError; } }
        public ControlResponse ActionResponse { get { return actionResponse; } }
        public void ConversationVisible(bool value)
        {
            lock (gate) { conversationVisible = value; sessionWake.Set(); }
        }
        public void SelectSession(CompanionSession session)
        {
            lock (gate)
            {
                selectedSession = session == null ? null : session.sessionId;
                selectedEpoch = session == null ? null : session.runtimeEpoch;
                selectedCursor = 0; selectionVersion++; pages.Clear(); sessionWake.Set();
            }
        }
        public bool TryReadPage(out ConversationPage page)
        {
            lock (gate)
            {
                if (pages.Count == 0) { page = null; return false; }
                page = pages.Dequeue(); return true;
            }
        }
        public bool Send(CompanionSession session, string text, string requestId, string mode, int revision)
        {
            return Queue(new ControlRequest { method = "send", id = requestId, session_id = session.sessionId,
                runtime_epoch = session.runtimeEpoch, text = text, mode = mode, message_revision = session.busy ? -1 : revision });
        }
        public bool SubmissionStatus(CompanionSession session, string requestId)
        {
            return Queue(new ControlRequest { method = "submission_status", session_id = session.sessionId,
                runtime_epoch = session.runtimeEpoch, request_id = requestId });
        }
        public bool Viewed(CompanionSession session, string eventId, int revision, int focusRevision)
        {
            return Queue(new ControlRequest { method = "inbox.viewed", session_id = session.sessionId,
                runtime_epoch = session.runtimeEpoch, event_id = eventId, message_revision = revision, focus_revision = focusRevision, inbox_focused = true });
        }

        void Publish(string text, bool success)
        {
            result = text;
            actionSucceeded = success;
            Interlocked.Increment(ref resultRevision);
        }

        bool Queue(ControlRequest request)
        {
            lock (gate)
            {
                if (stopping || busy || pending != null || !Fresh) return false;
                pending = request;
                busy = true;
                Publish("Выполняется выбранное действие…", false);
                actionWake.Set();
                return true;
            }
        }

        public bool Run(string action, bool confirmed)
        {
            return Queue(new ControlRequest { method = "run", action = action,
                id = Guid.NewGuid().ToString("N"), confirm = confirmed });
        }

        public bool Attention(string action, string token)
        {
            return Queue(new ControlRequest { method = "attention", action = action, token = token });
        }

        public void Surface(bool visible, bool interacting)
        {
            surfaceVisible = visible;
            engaged = interacting;
        }

        public void PanelVisible(bool value)
        {
            lock (gate) { if (stopping) return; visibility = value; actionWake.Set(); }
        }

        void Poll()
        {
            while (!stopping)
            {
                int delay = 1000;
                try
                {
                    var response = statusConnection.Exchange(new ControlRequest {
                        method = "snapshot", visible = surfaceVisible, engaged = engaged });
                    if (!response.ok || response.snapshot == null || response.snapshot.version != 2)
                        throw new IOException(response.message ?? "Unsupported desktop snapshot");
                    latest = response.snapshot;
                    Interlocked.Exchange(ref receivedAt, clock.ElapsedMilliseconds);
                    connectionError = null;
                }
                catch (Exception error)
                {
                    if (stopping) break;
                    connectionError = "Источник недоступен: " + error.GetType().Name + ": " + error.Message;
                    Interlocked.Exchange(ref receivedAt, -100000);
                    statusConnection.Reset();
                    delay = 5000;
                }
                if (stop.WaitOne(delay)) break;
            }
        }

        void PollSessions()
        {
            long inventoryAt = -10000;
            while (!stopping)
            {
                string sid, epoch; int after, version; bool visible;
                lock (gate) { visible = conversationVisible; sid = selectedSession; epoch = selectedEpoch; after = selectedCursor; version = selectionVersion; }
                if (!visible) { sessionWake.WaitOne(1000); continue; }
                try
                {
                    if (clock.ElapsedMilliseconds - inventoryAt > 3000)
                    {
                        var inventory = sessionConnection.Exchange(new ControlRequest { method = "sessions" });
                        if (!inventory.ok || inventory.sessions == null) throw new IOException(inventory.message ?? "Session inventory unavailable");
                        sessions = inventory.sessions; inventoryAt = clock.ElapsedMilliseconds;
                        Interlocked.Increment(ref sessionRevision);
                    }
                    if (!string.IsNullOrEmpty(sid))
                    {
                        bool room; lock (gate) room = pages.Count < 8;
                        if (room)
                        {
                            var response = sessionConnection.Exchange(new ControlRequest { method = "messages", session_id = sid, runtime_epoch = epoch, after = after });
                            if (!response.ok || response.conversation == null) throw new IOException(response.message ?? "Conversation unavailable");
                            var page = response.conversation;
                            if (page.sessionId != sid || page.runtimeEpoch != epoch) throw new IOException("Conversation identity mismatch");
                            lock (gate)
                            {
                                if (version == selectionVersion)
                                {
                                    pages.Enqueue(page);
                                    selectedCursor = page.nextAfter;
                                }
                            }
                        }
                    }
                    sessionError = null;
                }
                catch (Exception error)
                {
                    if (stopping) break;
                    sessionError = error.Message; sessionConnection.Reset();
                }
                sessionWake.WaitOne(700);
            }
        }

        void Act()
        {
            while (!stopping)
            {
                ControlRequest request = null;
                lock (gate)
                {
                    if (pending != null) { request = pending; pending = null; }
                    else if (visibility.HasValue)
                    {
                        request = new ControlRequest { method = visibility.Value ? "panel.open" : "panel.close" };
                        visibility = null;
                    }
                }
                if (request == null) { actionWake.WaitOne(); continue; }
                bool action = request.method != "panel.open" && request.method != "panel.close";
                try
                {
                    var response = actionConnection.Exchange(request);
                    if (!action) { panelError = response.ok ? null : response.message; continue; }
                    response.requestMethod = request.method;
                    response.requestSession = request.session_id;
                    response.requestAction = request.action;
                    if (request.method == "send" && !response.ok && response.submission == null)
                        response.submission = new CompanionSubmission { requestId = request.id, state = "rejected", message = response.message };
                    actionResponse = response;
                    Publish(response.ok ? (string.IsNullOrEmpty(response.message) ? "Принято" : response.message)
                        : "Не выполнено: " + response.message, response.ok);
                }
                catch (Exception error)
                {
                    if (stopping) break;
                    if (!action) { panelError = "Фокус меню: " + error.GetType().Name + ": " + error.Message; continue; }
                    bool unknown = actionConnection.MayHaveSent;
                    actionResponse = new ControlResponse { ok = false, outcomeUnknown = unknown,
                        message = unknown ? "Результат неизвестен; автоматического повтора нет." : "Запрос не отправлен: " + error.GetType().Name,
                        submission = request.method == "send" ? new CompanionSubmission { requestId = request.id, state = unknown ? "unknown" : "rejected" } : null,
                        requestMethod = request.method, requestSession = request.session_id, requestAction = request.action };
                    Publish(actionResponse.message, false);
                }
                finally { actionConnection.Reset(); if (action) busy = false; }
            }
        }

        public void Dispose()
        {
            lock (gate) { if (stopping) return; stopping = true; }
            statusConnection.Dispose();
            actionConnection.Dispose();
            sessionConnection.Dispose();
            sessionWake.Set();
            stop.Set();
            actionWake.Set();
            if (poller.Join(1500)) stop.Dispose();
            if (actor.Join(1500)) actionWake.Dispose();
            if (sessionPoller.Join(1500)) sessionWake.Dispose();
        }
    }
}
