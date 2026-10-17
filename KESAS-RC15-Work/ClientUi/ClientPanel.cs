using System;
using System.Collections.Generic;
using LmpClient.Base;
using LmpClient.Systems.Chat;
using LmpClient.Systems.Status;
using LmpClient.Systems.SettingsSys;
using LmpClient.Windows.Aetherfront;
using LmpCommon.Enums;
using UnityEngine;

namespace LmpClient.Windows.Aetherfront
{
    internal static class ClientPanel
    {
        private static string[] _players = new string[0];
        private static string[] _lines = new string[0];
        private static string _draft = "";
        private static Vector2 _scroll;
        private static long _revision = -1;
        private static int _page;
        private static bool _follow = true;
        private static GUIStyle _textStyle;

        public static void Tick()
        {
            if (MainSystem.NetworkState >= ClientState.Running) return;
            _players = new string[0];
            _lines = new string[0];
            _draft = "";
            _revision = -1;
            _page = 0;
            _follow = true;
        }

        public static void DrawPlayers()
        {
            EnsureStyle();
            if (Event.current.type == EventType.Layout)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var player in System<StatusSystem>.Singleton.PlayerStatusList.Values)
                    if (player != null && !string.IsNullOrWhiteSpace(player.PlayerName)) names.Add(player.PlayerName);
                string me = SettingsSystem.CurrentSettings.PlayerName;
                if (!string.IsNullOrWhiteSpace(me)) names.Add(me);
                var list = new List<string>(names);
                list.Sort(StringComparer.OrdinalIgnoreCase);
                _players = list.ToArray();
            }
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Online players (" + _players.Length + ")", _textStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Global chat", GUILayout.Width(110)))
                Window<AetherfrontFactionWindow>.Singleton.OpenChat();
            GUILayout.EndHorizontal();
            for (int index = 0; index < Math.Min(8, _players.Length); index++)
                GUILayout.Label(_players[index], _textStyle);
            if (_players.Length > 8) GUILayout.Label("See Players for the full list.", _textStyle);
            GUILayout.EndVertical();
            GUILayout.Space(8);
        }

        public static void DrawGlobalChat()
        {
            EnsureStyle();
            var chat = System<ChatSystem>.Singleton;
            if (Event.current.type == EventType.Layout && _revision != chat.MessageRevision)
            {
                _revision = chat.MessageRevision;
                var lines = new List<string>();
                if (chat.ChatMessages != null)
                    foreach (var message in chat.ChatMessages)
                        lines.Add(message.Item1 + ": " + message.Item2);
                _lines = lines.ToArray();
                if (_follow) { _page = Math.Max(0, (_lines.Length - 1) / 50); _scroll.y = float.PositiveInfinity; }
            }
            chat.NewMessageReceived = false;
            int pages = Math.Max(1, (_lines.Length + 49) / 50);
            _page = Math.Max(0, Math.Min(_page, pages - 1));
            GUILayout.Label("Global chat", _textStyle);
            GUILayout.BeginHorizontal();
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && _page > 0;
            if (GUILayout.Button("Older")) { _page--; _follow = false; _scroll = Vector2.zero; }
            GUI.enabled = enabled;
            GUILayout.Label((_page + 1) + " / " + pages, _textStyle);
            GUI.enabled = enabled && _page + 1 < pages;
            if (GUILayout.Button("Newer")) { _page++; _scroll = Vector2.zero; }
            GUI.enabled = enabled;
            if (GUILayout.Button("Latest")) { _page = pages - 1; _follow = true; _scroll.y = float.PositiveInfinity; }
            GUILayout.EndHorizontal();
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true), GUILayout.MinHeight(80));
            if (_lines.Length == 0) GUILayout.Label("No messages yet.", _textStyle);
            int last = Math.Min(_lines.Length, (_page + 1) * 50);
            for (int index = _page * 50; index < last; index++) GUILayout.Label(_lines[index], _textStyle);
            GUILayout.EndScrollView();
            GUI.SetNextControlName("LMP_UnifiedInput:globalchat");
            _draft = GUILayout.TextField(_draft, 500);
            bool enter = Event.current.type == EventType.KeyDown &&
                (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) &&
                GUI.GetNameOfFocusedControl() == "LMP_UnifiedInput:globalchat";
            if (enter) Event.current.Use();
            GUI.enabled = enabled && chat.Enabled && !string.IsNullOrWhiteSpace(_draft);
            bool send = GUILayout.Button("Send");
            if (GUI.enabled && (send || enter))
            {
                chat.MessageSender.SendChatMsg(_draft.Trim());
                _draft = "";
                _follow = true;
                _scroll.y = float.PositiveInfinity;
            }
            GUI.enabled = enabled;
        }

        public static bool HidePopup(bool chatVisible, Tuple<string, string, string> message)
        {
            return chatVisible && !IsAnnouncement(message);
        }

        public static float PopupDuration(Tuple<string, string, string> message)
        {
            return IsAnnouncement(message) ? 15f : 5f;
        }

        private static bool IsAnnouncement(Tuple<string, string, string> message)
        {
            return message != null && string.Equals(message.Item1, SettingsSystem.ServerSettings.ConsoleIdentifier, StringComparison.Ordinal) &&
                !(message.Item2 ?? "").StartsWith("[Discord]", StringComparison.Ordinal);
        }

        private static void EnsureStyle()
        {
            if (_textStyle != null) return;
            _textStyle = new GUIStyle(GUI.skin.label);
            _textStyle.richText = false;
            _textStyle.wordWrap = true;
        }
    }
}
