using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MateEngine.Shoji;

namespace MateEngine.Desktop
{
    internal sealed class ControlAction { public string id, label, group, reason; public bool available, confirmation; }
    internal sealed class ControlAgent
    {
        public string id, session_id, label, state, source, cwd, action_id, project, profile, health;
        public int completion;
    }
    internal sealed class AttentionItem { public string id, session_id, kind, label, project, profile, action_id; }
    internal sealed class AttentionView { public string token, phase, profile, caption; public bool quiet; public AttentionItem[] items; }
    internal sealed class ControlSnapshot
    {
        public int version, completion, control_sequence;
        public string revision, state, control_command;
        public AttentionView attention;
        public ControlAction[] actions;
        public ControlAgent[] agents;
        public string[] issues;
    }
    internal sealed class CompanionSession
    {
        public string sessionId, runtimeEpoch, title, cwd, model, presence;
        public bool busy, canReply;
        public int messageRevision;
        public string Identity { get { return sessionId + ":" + runtimeEpoch; } }
    }
    internal sealed class CompanionMessage
    {
        public string id, role, text;
        public int revision;
        public bool complete, truncated;
    }
    internal sealed class ConversationPage
    {
        public string sessionId, runtimeEpoch;
        public int messageRevision, nextAfter;
        public bool hasMore, truncated;
        public bool busy;
        public CompanionMessage[] messages;
    }
    internal sealed class CompanionSubmission { public string requestId, state, delivery, messageId, message; }
    internal sealed class ControlRequest
    {
        public string method, id, action, token, session_id, runtime_epoch, text, mode, request_id, event_id;
        public bool confirm, visible, engaged, inbox_focused;
        public int after, limit = 50, message_revision = -1, focus_revision;
    }
    internal sealed class ControlResponse
    {
        public bool ok, viewed, outcomeUnknown;
        public string message, event_id, requestMethod, requestSession, requestAction;
        public ControlSnapshot snapshot;
        public CompanionSession[] sessions;
        public ConversationPage conversation;
        public CompanionSubmission submission;
    }
    internal static class DesktopProtocol
    {
        static Dictionary<string, object> Map(object value)
        {
            var map = value as Dictionary<string, object>;
            if (map == null) throw new IOException("Expected desktop JSON object");
            return map;
        }
        static string Text(Dictionary<string, object> map, string name)
        {
            object value;
            if (!map.TryGetValue(name, out value) || value == null) return "";
            if (!(value is string)) throw new IOException("Invalid desktop text: " + name);
            return (string)value;
        }
        static int Number(Dictionary<string, object> map, string name)
        {
            object value;
            if (!map.TryGetValue(name, out value)) return 0;
            if (!(value is double)) throw new IOException("Invalid desktop number: " + name);
            return checked((int)(double)value);
        }
        static bool Flag(Dictionary<string, object> map, string name)
        {
            object value;
            if (!map.TryGetValue(name, out value)) return false;
            if (!(value is bool)) throw new IOException("Invalid desktop flag: " + name);
            return (bool)value;
        }
        static List<object> Items(Dictionary<string, object> map, string name, int maximum)
        {
            object value;
            if (!map.TryGetValue(name, out value) || !(value is List<object>)) throw new IOException("Invalid desktop list: " + name);
            var list = (List<object>)value;
            if (list.Count > maximum) throw new IOException("Desktop list limit: " + name);
            return list;
        }
        public static ControlResponse Decode(string text)
        {
            var map = Map(MiniJson.Parse(text));
            var response = new ControlResponse { ok = Flag(map, "ok"), viewed = Flag(map, "viewed"), event_id = Text(map, "event_id"), message = Text(map, "message") };
            object value;
            if (map.TryGetValue("sessions", out value))
            {
                var sessions = Items(map, "sessions", 64);
                response.sessions = new CompanionSession[sessions.Count];
                for (int i = 0; i < sessions.Count; i++)
                {
                    var item = Map(sessions[i]); var key = Map(item["key"]);
                    var caps = Items(item, "capabilities", 16);
                    response.sessions[i] = new CompanionSession { sessionId = Text(key, "sessionId"), runtimeEpoch = Text(key, "runtimeEpoch"),
                        title = Text(item, "title"), cwd = Text(item, "cwd"), model = Text(item, "model"), presence = Text(item, "presence"),
                        busy = Flag(item, "busy"), canReply = caps.Contains("send"), messageRevision = Number(item, "messageRevision") };
                }
            }
            if (map.TryGetValue("conversation", out value))
            {
                var item = Map(value); var key = Map(item["key"]); var messages = Items(item, "messages", 50);
                var page = new ConversationPage { sessionId = Text(key, "sessionId"), runtimeEpoch = Text(key, "runtimeEpoch"),
                    messageRevision = Number(item, "messageRevision"), nextAfter = Number(item, "nextAfter"), hasMore = Flag(item, "hasMore"),
                    truncated = Flag(item, "truncated"), messages = new CompanionMessage[messages.Count] };
                for (int i = 0; i < messages.Count; i++)
                {
                    var m = Map(messages[i]);
                    page.messages[i] = new CompanionMessage { id = Text(m, "id"), role = Text(m, "role"), text = Text(m, "text"),
                        revision = Number(m, "revision"), complete = Flag(m, "complete"), truncated = Flag(m, "truncated") };
                }
                response.conversation = page;
                page.busy = Flag(item, "busy");
            }
            if (map.TryGetValue("submission", out value))
            {
                var item = Map(value);
                response.submission = new CompanionSubmission { requestId = Text(item, "requestId"), state = Text(item, "state"),
                    delivery = Text(item, "delivery"), messageId = Text(item, "messageId"), message = Text(item, "message") };
            }
            if (!map.TryGetValue("snapshot", out value) || value == null) return response;
            var data = Map(value); var actions = Items(data, "actions", 128); var agents = Items(data, "agents", 64); var issues = Items(data, "issues", 16);
            var snapshot = new ControlSnapshot { version = Number(data, "version"), completion = Number(data, "completion"),
                revision = Text(data, "revision"), state = Text(data, "state"), control_sequence = Number(data, "control_sequence"), control_command = Text(data, "control_command"),
                actions = new ControlAction[actions.Count], agents = new ControlAgent[agents.Count], issues = new string[issues.Count] };
            for (int i = 0; i < actions.Count; i++)
            {
                var item = Map(actions[i]); snapshot.actions[i] = new ControlAction { id = Text(item, "id"), label = Text(item, "label"),
                    group = Text(item, "group"), reason = Text(item, "reason"), available = Flag(item, "available"), confirmation = Flag(item, "confirmation") };
            }
            for (int i = 0; i < agents.Count; i++)
            {
                var item = Map(agents[i]); snapshot.agents[i] = new ControlAgent { id = Text(item, "id"), session_id = Text(item, "session_id"), label = Text(item, "label"),
                    state = Text(item, "state"), source = Text(item, "source"), cwd = Text(item, "cwd"), action_id = Text(item, "action_id"), project = Text(item, "project"),
                    profile = Text(item, "profile"), health = Text(item, "health"), completion = Number(item, "completion") };
            }
            for (int i = 0; i < issues.Count; i++) { if (!(issues[i] is string)) throw new IOException("Invalid source issue"); snapshot.issues[i] = (string)issues[i]; }
            var attention = Map(data["attention"]); var reasons = Items(attention, "items", 64);
            snapshot.attention = new AttentionView { token = Text(attention, "token"), phase = Text(attention, "phase"), profile = Text(attention, "profile"),
                caption = Text(attention, "caption"), quiet = Flag(attention, "quiet"), items = new AttentionItem[reasons.Count] };
            for (int i = 0; i < reasons.Count; i++)
            {
                var item = Map(reasons[i]); snapshot.attention.items[i] = new AttentionItem { id = Text(item, "id"), session_id = Text(item, "session_id"), kind = Text(item, "kind"),
                    label = Text(item, "label"), project = Text(item, "project"), profile = Text(item, "profile"), action_id = Text(item, "action_id") };
            }
            response.snapshot = snapshot;
            return response;
        }
        static void Quoted(StringBuilder builder, string text)
        {
            if (text == null) { builder.Append("null"); return; }
            builder.Append('"');
            foreach (char value in text)
            {
                switch (value)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default: if (value < 32) builder.Append("\\u").Append(((int)value).ToString("x4")); else builder.Append(value); break;
                }
            }
            builder.Append('"');
        }
        static void Field(StringBuilder b, string name, string value) { b.Append(",\"").Append(name).Append("\":"); Quoted(b, value); }
        public static string Encode(ControlRequest request, StringBuilder builder)
        {
            builder.Length = 0; builder.Append("{\"method\":"); Quoted(builder, request.method);
            Field(builder, "id", request.id); Field(builder, "action", request.action); Field(builder, "token", request.token);
            Field(builder, "session_id", request.session_id); Field(builder, "runtime_epoch", request.runtime_epoch);
            Field(builder, "text", request.text); Field(builder, "mode", request.mode); Field(builder, "request_id", request.request_id); Field(builder, "event_id", request.event_id);
            builder.Append(",\"after\":").Append(request.after).Append(",\"limit\":").Append(request.limit);
            if (request.message_revision >= 0) builder.Append(",\"message_revision\":").Append(request.message_revision);
            builder.Append(",\"focus_revision\":").Append(request.focus_revision);
            builder.Append(",\"visible\":").Append(request.visible ? "true" : "false").Append(",\"engaged\":").Append(request.engaged ? "true" : "false");
            builder.Append(",\"inbox_focused\":").Append(request.inbox_focused ? "true" : "false");
            builder.Append(",\"confirm\":").Append(request.confirm ? "true" : "false").Append("}\n"); return builder.ToString();
        }
    }
}
