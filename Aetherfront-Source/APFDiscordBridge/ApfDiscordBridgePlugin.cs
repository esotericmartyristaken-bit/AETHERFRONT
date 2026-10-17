using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Globalization;
using Aetherfront.Core;
using LmpCommon.Message.Data.Chat;
using LmpCommon.Message.Interface;
using Server.Aetherfront;
using Newtonsoft.Json;
using Server.Client;
using Server.Command;
using Server.Log;
using Server.Plugin;

namespace APFDiscordBridge
{
    /// <summary>
    /// Server-only bridge. Discord never loads inside LMP; the bot exchanges small
    /// atomic JSON files through APFDiscordBridge/inbox and /events.
    /// </summary>
    public sealed class ApfDiscordBridgePlugin : LmpPlugin
    {
        private const int MaxMessageLength = 500;
        private const int MaxCommandArgumentLength = 260;
        private const int MaxEventFiles = 200;
        private static readonly HashSet<string> AllowedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "help", "countclients", "listclients", "listlocks", "connectionstats", "backup", "changesettings",
            "dekessler", "nukeksc", "clearvessels", "kick", "ban", "vessel", "setfunds", "setscience"
        };
        private string _root;
        private string _inbox;
        private string _events;
        private string _results;
        private string _ready;
        private string _cursor;
        private DateTime _nextPoll = DateTime.MinValue;
        private DateTime _nextChroniclePoll = DateTime.MinValue;
        private long _lastChronicleRevision;

        public override void OnServerStart()
        {
            try
            {
                _root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "APFDiscordBridge");
                _inbox = Path.Combine(_root, "inbox");
                _events = Path.Combine(_root, "events");
                _results = Path.Combine(_root, "results");
                _ready = Path.Combine(_root, "ready.json");
                _cursor = Path.Combine(_root, "chronicle-cursor.json");
                Directory.CreateDirectory(_inbox);
                Directory.CreateDirectory(_events);
                Directory.CreateDirectory(_results);
                _lastChronicleRevision = ReadChronicleCursor();
                WriteAtomic(_ready, new { pid = Process.GetCurrentProcess().Id, startedAt = DateTime.UtcNow.ToString("O"), capabilities = new[] { "chat", "commands", "aetherfront-events" } });
                LunaLog.Info("[APF Discord] Bridge ready");
            }
            catch (Exception e) { LunaLog.Error("[APF Discord] Startup failed: " + e.Message); }
        }

        public override void OnServerStop()
        {
            try { if (!string.IsNullOrEmpty(_ready)) File.Delete(_ready); }
            catch (Exception e) { LunaLog.Debug("[APF Discord] Stop cleanup failed: " + e.Message); }
        }

        public override void OnUpdate()
        {
            if (DateTime.UtcNow < _nextPoll || string.IsNullOrEmpty(_inbox)) return;
            _nextPoll = DateTime.UtcNow.AddMilliseconds(250);
            try
            {
                foreach (var file in Directory.GetFiles(_inbox, "*.json").OrderBy(f => f).Take(8)) ProcessInboxFile(file);
                if (DateTime.UtcNow >= _nextChroniclePoll)
                {
                    _nextChroniclePoll = DateTime.UtcNow.AddMilliseconds(500);
                    PublishChronicleEvents();
                }
            }
            catch (Exception e) { LunaLog.Debug("[APF Discord] Inbox poll failed: " + e.Message); }
        }

        private void ProcessInboxFile(string file)
        {
            try
            {
                if (new FileInfo(file).Length > 64 * 1024) { File.Delete(file); return; }
                var command = JsonConvert.DeserializeObject<BridgeCommand>(File.ReadAllText(file));
                if (command != null && command.ServerPid != 0 && command.ServerPid != Process.GetCurrentProcess().Id) { File.Delete(file); return; }
                if (!string.IsNullOrEmpty(command?.ExpiresAt) &&
                    (!DateTime.TryParse(command.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt) || expiresAt.ToUniversalTime() < DateTime.UtcNow))
                { File.Delete(file); return; }
                if (string.Equals(command?.Kind, "command", StringComparison.OrdinalIgnoreCase))
                {
                    ProcessServerCommand(command);
                    File.Delete(file);
                    return;
                }
                var text = (command?.Text ?? string.Empty).Trim();
                if (text.Length == 0 || text.Length > MaxMessageLength) { File.Delete(file); return; }
                text = text.Replace('\r', ' ').Replace('\n', ' ');
                var author = (command.Author ?? "Discord").Trim().Replace('\r', ' ').Replace('\n', ' ');
                if (author.Length > 64) author = author.Substring(0, 64);
                if (CommandHandler.Commands.TryGetValue("say", out var say))
                {
                    var renderedAuthor = "[Discord] " + author;
                    say.Func(renderedAuthor + ": " + text);
                    // The say command already broadcasts to LMP. Emit one bridge
                    // event directly for Discord instead of writing a second chat
                    // log entry that can be mistaken for another inbound message.
                    WriteChatEvent(renderedAuthor, text);
                }
                File.Delete(file);
            }
            catch (Exception e) { LunaLog.Debug("[APF Discord] Inbox item failed: " + e.Message); TryDelete(file); }
        }

        private void ProcessServerCommand(BridgeCommand request)
        {
            var id = (request.Id ?? string.Empty).Trim();
            if (id.Length == 0 || id.Length > 80 || !id.All(ch => char.IsLetterOrDigit(ch) || ch == '-')) return;

            var command = (request.Command ?? string.Empty).Trim().ToLowerInvariant();
            var argument = (request.Argument ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            var ok = false;
            string reason = null;

            if (!AllowedCommands.Contains(command)) reason = "command-not-allowlisted";
            else if (argument.Length > MaxCommandArgumentLength) reason = "argument-too-long";
            else if (!CommandHandler.Commands.TryGetValue(command, out var definition)) reason = "command-unavailable";
            else
            {
                try
                {
                    LunaLog.Normal("[APF Discord] Executing /" + command);
                    ok = definition.Func(argument);
                    if (!ok) reason = "command-returned-false";
                }
                catch (Exception e)
                {
                    reason = "command-exception";
                    LunaLog.Error("[APF Discord] /" + command + " failed: " + e.Message);
                }
            }

            if (string.IsNullOrEmpty(_results)) return;
            WriteAtomic(Path.Combine(_results, id + ".json"), new
            {
                type = "command-result",
                id,
                serverPid = Process.GetCurrentProcess().Id,
                command,
                ok,
                reason,
                processedAt = DateTime.UtcNow.ToString("O")
            });
        }

        public override void OnMessageReceived(ClientStructure client, IClientMessageBase messageData)
        {
            try
            {
                if (!(messageData?.Data is ChatMsgData chat) || !chat.Relay || string.IsNullOrWhiteSpace(chat.Text) || string.IsNullOrEmpty(_events)) return;
                WriteChatEvent(chat.From, chat.Text);
            }
            catch (Exception e) { LunaLog.Debug("[APF Discord] Chat event failed: " + e.Message); }
        }

        private void WriteChatEvent(string from, string message)
        {
            if (string.IsNullOrEmpty(_events) || Directory.GetFiles(_events, "*.json").Length >= MaxEventFiles) return;
            var text = (message ?? string.Empty).Trim().Replace('\r', ' ').Replace('\n', ' ');
            if (text.Length == 0 || text.Length > MaxMessageLength) return;
            WriteAtomic(Path.Combine(_events, DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json"), new
            {
                type = "lmp-chat",
                id = Guid.NewGuid().ToString("N"),
                serverPid = Process.GetCurrentProcess().Id,
                from = (from ?? string.Empty).Trim(),
                text,
                receivedAt = DateTime.UtcNow.ToString("O")
            });
        }

        private void PublishChronicleEvents()
        {
            if (!AetherfrontServerSystem.Enabled || string.IsNullOrEmpty(_events)) return;
            var snapshot = AetherfrontServerSystem.Snapshot();
            if (snapshot?.Events == null) return;
            foreach (var item in snapshot.Events
                .Where(e => e != null && e.Public && e.Revision > _lastChronicleRevision)
                .OrderBy(e => e.Revision)
                .Take(32))
            {
                if (Directory.GetFiles(_events, "*.json").Length >= MaxEventFiles) return;
                var eventId = (item.Id ?? string.Empty).Trim();
                if (eventId.Length == 0) eventId = "revision-" + item.Revision.ToString(CultureInfo.InvariantCulture);
                var safeId = new string(eventId.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-').ToArray());
                var path = Path.Combine(_events, "af-" + item.Revision.ToString("D12", CultureInfo.InvariantCulture) + "-" + safeId + ".json");
                if (!File.Exists(path))
                {
                    WriteAtomic(path, new
                    {
                        type = "aetherfront-event",
                        id = eventId,
                        eventType = item.Type,
                        revision = item.Revision,
                        serverPid = Process.GetCurrentProcess().Id,
                        actorAccountId = item.ActorAccountId,
                        factionId = item.FactionId,
                        secondaryFactionId = item.SecondaryFactionId,
                        entityId = item.EntityId,
                        occurredAt = item.OccurredAtUtc.ToString("O"),
                        fields = item.Fields ?? new List<EventField>(),
                        receivedAt = DateTime.UtcNow.ToString("O")
                    });
                    WriteChronicleChatEvent(item, eventId);
                }
                _lastChronicleRevision = Math.Max(_lastChronicleRevision, item.Revision);
                WriteAtomic(_cursor, new { revision = _lastChronicleRevision, updatedAt = DateTime.UtcNow.ToString("O") });
            }
        }

        private void WriteChronicleChatEvent(AetherfrontEvent item, string eventId)
        {
            if (item == null || string.IsNullOrEmpty(_events) || Directory.GetFiles(_events, "*.json").Length >= MaxEventFiles) return;
            var faction = string.IsNullOrWhiteSpace(item.FactionId) ? string.Empty : " faction=" + item.FactionId;
            var entity = string.IsNullOrWhiteSpace(item.EntityId) ? string.Empty : " entity=" + item.EntityId;
            var text = ("Chronicle: " + item.Type + faction + entity).Trim();
            if (text.Length > MaxMessageLength) text = text.Substring(0, MaxMessageLength);
            WriteAtomic(Path.Combine(_events, "af-chat-" + item.Revision.ToString("D12", CultureInfo.InvariantCulture) + ".json"), new
            {
                type = "lmp-chat",
                id = "chronicle-" + eventId,
                serverPid = Process.GetCurrentProcess().Id,
                from = "Aetherfront",
                text,
                receivedAt = DateTime.UtcNow.ToString("O")
            });
        }

        private long ReadChronicleCursor()
        {
            try
            {
                if (!File.Exists(_cursor)) return 0;
                var cursor = JsonConvert.DeserializeObject<ChronicleCursor>(File.ReadAllText(_cursor));
                return Math.Max(0, cursor?.Revision ?? 0);
            }
            catch { return 0; }
        }

        private static void WriteAtomic(string path, object value)
        {
            var temp = path + "." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonConvert.SerializeObject(value), Encoding.UTF8);
                if (File.Exists(path)) File.Replace(temp, path, null, true);
                else File.Move(temp, path);
            }
            finally { TryDelete(temp); }
        }

        private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

        private sealed class BridgeCommand
        {
            [JsonProperty("kind")] public string Kind { get; set; }
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("serverPid")] public int ServerPid { get; set; }
            [JsonProperty("author")] public string Author { get; set; }
            [JsonProperty("text")] public string Text { get; set; }
            [JsonProperty("command")] public string Command { get; set; }
            [JsonProperty("argument")] public string Argument { get; set; }
            [JsonProperty("expiresAt")] public string ExpiresAt { get; set; }
        }

        private sealed class ChronicleCursor
        {
            [JsonProperty("revision")] public long Revision { get; set; }
        }
    }
}
