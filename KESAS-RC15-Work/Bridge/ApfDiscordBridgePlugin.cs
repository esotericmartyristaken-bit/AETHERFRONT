using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Globalization;
using LmpCommon.Message.Data.Chat;
using LmpCommon.Message.Interface;
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
        private DateTime _nextPoll = DateTime.MinValue;
        private DateTime _nextStatus = DateTime.MinValue;

        public override void OnServerStart()
        {
            try
            {
                _root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "APFDiscordBridge");
                _inbox = Path.Combine(_root, "inbox");
                _events = Path.Combine(_root, "events");
                _results = Path.Combine(_root, "results");
                _ready = Path.Combine(_root, "ready.json");
                Directory.CreateDirectory(_inbox);
                Directory.CreateDirectory(_events);
                Directory.CreateDirectory(_results);
                WriteAtomic(_ready, new { pid = Process.GetCurrentProcess().Id, startedAt = DateTime.UtcNow.ToString("O"), capabilities = new[] { "chat", "commands", "players", "announcements" } });
                WriteStatus();
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
                if (DateTime.UtcNow >= _nextStatus)
                {
                    _nextStatus = DateTime.UtcNow.AddSeconds(1);
                    WriteStatus();
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
                    bool sent = say.Func(command.Announcement ? text : renderedAuthor + ": " + text);
                    if (command.Announcement && !string.IsNullOrEmpty(command.Id) && command.Id.Length <= 80 &&
                        command.Id.All(ch => char.IsLetterOrDigit(ch) || ch == '-'))
                        WriteAtomic(Path.Combine(_results, command.Id + ".json"), new
                        { id = command.Id, serverPid = Process.GetCurrentProcess().Id, ok = sent });
                    // The say command already broadcasts to LMP. Emit one bridge
                    // event directly for Discord instead of writing a second chat
                    // log entry that can be mistaken for another inbound message.
                    if (!command.Announcement) WriteChatEvent(renderedAuthor, text);
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

        private void WriteStatus()
        {
            var clients = ClientRetriever.GetClients();
            var players = clients.Where(client => client.Authenticated).Select(client => new
            {
                name = client.PlayerName,
                id = client.UniqueIdentifier,
                kspVersion = client.KspVersion
            }).OrderBy(client => client.name).ToArray();
            WriteAtomic(Path.Combine(_root, "status.json"), new
            {
                serverPid = Process.GetCurrentProcess().Id,
                updatedAt = DateTime.UtcNow.ToString("O"),
                playerCount = players.Length,
                connectedCount = clients.Length,
                players
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
            [JsonProperty("announcement")] public bool Announcement { get; set; }
        }
    }
}
