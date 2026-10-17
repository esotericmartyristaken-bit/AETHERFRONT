using System;
using System.Net;
using LmpClient.Apf;
using LmpClient.Base;
using LmpClient.Localization;
using LmpClient.Network;
using LmpClient.Systems.SettingsSys;
using LmpClient.Windows.Apf;
using LmpClient.Windows.Options;
using LmpClient.Windows.ServerList;
using LmpCommon;
using LmpCommon.Enums;
using UnityEngine;

namespace LmpClient.Windows.Connection;

public class ConnectionWindow : Window<ConnectionWindow>
{
	private static string _localPassword = string.Empty;

	private static string _serverAddress;

	private static string _serverPort = "18865";

	private static string _certificatePin = string.Empty;

	private static int _selectedIndex;

	protected const float WindowHeight = 400f;

	protected const float WindowWidth = 400f;

	private readonly string _title = $"Luna Multiplayer {LmpVersioning.CurrentVersion}";

	private static bool EditableServer
	{
		get
		{
			if (ApfClientSettings.DirectServerMode)
			{
				return !ApfClientSettings.LocalOnlyMode;
			}
			return false;
		}
	}

	private static int SelectedIndex
	{
		get
		{
			if (SettingsSystem.CurrentSettings.Servers.Count == 0)
			{
				return -1;
			}
			return _selectedIndex;
		}
		set
		{
			_selectedIndex = value;
		}
	}

	public override bool Display
	{
		get
		{
			//IL_0013: Unknown result type (might be due to invalid IL or missing references)
			//IL_0019: Invalid comparison between Unknown and I4
			if (SettingsSystem.CurrentSettings.DisclaimerAccepted && MainSystem.ToolbarShowGui && (int)HighLogic.LoadedScene == 2)
			{
				return MainSystem.NetworkState < ClientState.Running;
			}
			return false;
		}
	}

	protected override void DrawWindowContent(int windowId)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		GUI.DragWindow(MoveRect);
		GUILayout.BeginVertical(Array.Empty<GUILayoutOption>());
		if (EditableServer && _serverAddress == null)
		{
			IPEndPoint fixedServerEndpoint = ApfClientSettings.FixedServerEndpoint;
			_serverAddress = fixedServerEndpoint?.Address.ToString() ?? string.Empty;
			_serverPort = fixedServerEndpoint?.Port.ToString() ?? "18865";
		}
		DrawPlayerNameSection();
		ConnectionInstall.DrawProfile();
		DrawTopButtons();
		if (ApfClientSettings.FixedServerMode)
		{
			GUI.enabled = MainSystem.NetworkState <= ClientState.Disconnected && !ConnectionInstall.Busy;
			if (EditableServer)
			{
				GUILayout.Label("Server IP address", Array.Empty<GUILayoutOption>());
				_serverAddress = GUILayout.TextField(_serverAddress ?? string.Empty, 64, Array.Empty<GUILayoutOption>());
				GUILayout.Label("Port", Array.Empty<GUILayoutOption>());
				_serverPort = GUILayout.TextField(_serverPort, 5, Array.Empty<GUILayoutOption>());
			}
			else
			{
				IPEndPoint fixedServerEndpoint2 = ApfClientSettings.FixedServerEndpoint;
				GUILayout.Label((fixedServerEndpoint2 == null) ? "Local test server is not configured." : ("Server: " + fixedServerEndpoint2), StyleLibrary.StatusStyle, Array.Empty<GUILayoutOption>());
			}
			GUILayout.Label("Password (if required)", Array.Empty<GUILayoutOption>());
			_localPassword = GUILayout.PasswordField(_localPassword, '*', 64, Array.Empty<GUILayoutOption>());
			GUI.enabled = true;
		}
		else if (ApfClientSettings.Enabled)
		{
			GUILayout.Label("APF mode: use the private APF server directory; saved public servers are disabled.", StyleLibrary.StatusStyle, Array.Empty<GUILayoutOption>());
		}
		else
		{
			DrawCustomServers();
		}
		GUILayout.Label(MainSystem.Singleton.Status, StyleLibrary.StatusStyle, Array.Empty<GUILayoutOption>());
		GUILayout.EndVertical();
	}

	private void DrawCustomServers()
	{
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		GUILayout.Label(LocalizationContainer.ConnectionWindowText.CustomServers, Array.Empty<GUILayoutOption>());
		GUILayout.BeginVertical(Array.Empty<GUILayoutOption>());
		ScrollPos = GUILayout.BeginScrollView(ScrollPos, (GUILayoutOption[])(object)new GUILayoutOption[2]
		{
			GUILayout.Width(395f),
			GUILayout.Height(300f)
		});
		if (GUILayout.Button(StyleLibrary.PlusIcon, Array.Empty<GUILayoutOption>()))
		{
			SettingsSystem.CurrentSettings.Servers.Insert(0, new ServerEntry());
			SettingsSystem.SaveSettings();
		}
		for (int i = 0; i < SettingsSystem.CurrentSettings.Servers.Count; i++)
		{
			GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
			bool flag = GUILayout.Toggle(SelectedIndex == i, SettingsSystem.CurrentSettings.Servers[i].Name, StyleLibrary.ToggleButtonStyle, Array.Empty<GUILayoutOption>());
			bool num = GUILayout.Button(StyleLibrary.DeleteIcon, (GUILayoutOption[])(object)new GUILayoutOption[1] { GUILayout.Width(35f) });
			GUILayout.EndHorizontal();
			if (num)
			{
				SettingsSystem.CurrentSettings.Servers.RemoveAt(i);
				if (SelectedIndex > i)
				{
					SelectedIndex--;
				}
				else if (SelectedIndex >= SettingsSystem.CurrentSettings.Servers.Count)
				{
					SelectedIndex = SettingsSystem.CurrentSettings.Servers.Count - 1;
				}
				SettingsSystem.SaveSettings();
				break;
			}
			if (flag)
			{
				DrawServerEntry(i);
			}
		}
		GUILayout.EndScrollView();
		GUILayout.EndVertical();
	}

	private void DrawServerEntry(int serverPos)
	{
		SelectedIndex = serverPos;
		GUILayout.BeginVertical(Array.Empty<GUILayoutOption>());
		GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
		GUILayout.Label(LocalizationContainer.ConnectionWindowText.Name, LabelOptions);
		string text = GUILayout.TextArea(SettingsSystem.CurrentSettings.Servers[serverPos].Name, Array.Empty<GUILayoutOption>());
		if (text != SettingsSystem.CurrentSettings.Servers[serverPos].Name)
		{
			SettingsSystem.CurrentSettings.Servers[serverPos].Name = text;
			SettingsSystem.SaveSettings();
		}
		GUILayout.EndHorizontal();
		GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
		GUILayout.Label(LocalizationContainer.ConnectionWindowText.Address, LabelOptions);
		string text2 = GUILayout.TextArea(SettingsSystem.CurrentSettings.Servers[serverPos].Address, Array.Empty<GUILayoutOption>());
		if (text2 != SettingsSystem.CurrentSettings.Servers[serverPos].Address)
		{
			SettingsSystem.CurrentSettings.Servers[serverPos].Address = text2;
			SettingsSystem.SaveSettings();
		}
		GUILayout.EndHorizontal();
		GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
		GUILayout.Label(LocalizationContainer.ConnectionWindowText.Port, LabelOptions);
		if (int.TryParse(GUILayout.TextArea(SettingsSystem.CurrentSettings.Servers[serverPos].Port.ToString(), Array.Empty<GUILayoutOption>()), out var result) && result != SettingsSystem.CurrentSettings.Servers[serverPos].Port)
		{
			SettingsSystem.CurrentSettings.Servers[serverPos].Port = result;
			SettingsSystem.SaveSettings();
		}
		GUILayout.EndHorizontal();
		GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
		GUILayout.Label(LocalizationContainer.ConnectionWindowText.Password, LabelOptions);
		string text3 = GUILayout.PasswordField(SettingsSystem.CurrentSettings.Servers[serverPos].Password, '*', 30, Array.Empty<GUILayoutOption>());
		if (text3 != SettingsSystem.CurrentSettings.Servers[serverPos].Password)
		{
			SettingsSystem.CurrentSettings.Servers[serverPos].Password = text3;
			SettingsSystem.SaveSettings();
		}
		GUILayout.EndHorizontal();
		GUILayout.EndVertical();
	}

	private static void DrawTopButtons()
	{
		GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
		if (ConnectionInstall.Busy)
		{
			if (GUILayout.Button("Cancel", Array.Empty<GUILayoutOption>()))ConnectionInstall.Cancel();
		}
		else if (MainSystem.NetworkState <= ClientState.Disconnected)
		{
			GUI.enabled = (EditableServer ? (ApfClientSettings.ParseDirectEndpoint((_serverAddress ?? string.Empty).Trim() + ":" + _serverPort) != null) : (ApfClientSettings.FixedServerMode ? (ApfClientSettings.FixedServerEndpoint != null) : (!ApfClientSettings.Enabled && SettingsSystem.CurrentSettings.Servers.Count > SelectedIndex && SelectedIndex >= 0)));
			if (GUILayout.Button(StyleLibrary.ConnectBigIcon, Array.Empty<GUILayoutOption>()))
			{
				if (EditableServer)
				{
					if (ApfClientSettings.TrySaveDirectServer(_serverAddress, _serverPort, _certificatePin, out var error))
					{
						_certificatePin = string.Empty;
						NetworkConnection.ConnectToServer(new IPEndPoint[1] { ApfClientSettings.FixedServerEndpoint }, _localPassword);
					}
					else
					{
						MainSystem.Singleton.Status = error;
					}
				}
				else if (ApfClientSettings.FixedServerMode)
				{
					NetworkConnection.ConnectToServer(new IPEndPoint[1] { ApfClientSettings.FixedServerEndpoint }, _localPassword);
				}
				else
				{
					NetworkConnection.ConnectToServer(SettingsSystem.CurrentSettings.Servers[SelectedIndex].Address, SettingsSystem.CurrentSettings.Servers[SelectedIndex].Port, SettingsSystem.CurrentSettings.Servers[SelectedIndex].Password);
				}
			}
		}
		else if (GUILayout.Button(StyleLibrary.DisconnectBigIcon, Array.Empty<GUILayoutOption>()))
		{
			NetworkConnection.Disconnect("Cancelled connection to server");
		}
		GUI.enabled = true;
		Window<OptionsWindow>.Singleton.Display = GUILayout.Toggle(Window<OptionsWindow>.Singleton.Display, StyleLibrary.SettingsBigIcon, StyleLibrary.ToggleButtonStyle, Array.Empty<GUILayoutOption>());
		if (!ApfClientSettings.Enabled)
		{
			Window<ServerListWindow>.Singleton.Display = GUILayout.Toggle(Window<ServerListWindow>.Singleton.Display, StyleLibrary.ServerBigIcon, StyleLibrary.ToggleButtonStyle, Array.Empty<GUILayoutOption>());
		}
		if (ApfClientSettings.Enabled && GUILayout.Button("Diagnostics", Array.Empty<GUILayoutOption>()))
		{
			Window<ApfDiagnosticsWindow>.Singleton.Display = true;
		}
		GUILayout.EndHorizontal();
	}

	private void DrawPlayerNameSection()
	{
		GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
		GUILayout.Label(LocalizationContainer.ConnectionWindowText.PlayerName, LabelOptions);
		GUI.enabled = MainSystem.NetworkState <= ClientState.Disconnected;
		string text = GUILayout.TextArea(SettingsSystem.CurrentSettings.PlayerName, 32, Array.Empty<GUILayoutOption>());
		if (text != SettingsSystem.CurrentSettings.PlayerName)
		{
			SettingsSystem.CurrentSettings.PlayerName = text.Trim().Replace("\n", "");
			SettingsSystem.SaveSettings();
		}
		GUI.enabled = true;
		GUILayout.EndHorizontal();
	}

	public override void SetStyles()
	{
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0034: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_006d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0077: Unknown result type (might be due to invalid IL or missing references)
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Expected O, but got Unknown
		WindowRect = new Rect((float)Screen.width * 0.9f - 400f, Math.Max(0,(float)Screen.height / 2f - 300f), 400f, 600f);
		MoveRect = new Rect(0f, 0f, 2.1474836E+09f, 30f);
		GUIStyle val = new GUIStyle(GUI.skin.label);
		val.normal.textColor = Color.yellow;
		val.wordWrap = true;
		val.richText = false;
		StyleLibrary.StatusStyle = val;
		LayoutOptions = (GUILayoutOption[])(object)new GUILayoutOption[4];
		LayoutOptions[0] = GUILayout.MinWidth(400f);
		LayoutOptions[1] = GUILayout.MaxWidth(400f);
		LayoutOptions[2] = GUILayout.MinHeight(600f);
		LayoutOptions[3] = GUILayout.MaxHeight(900f);
		LabelOptions = (GUILayoutOption[])(object)new GUILayoutOption[1];
		LabelOptions[0] = GUILayout.Width(100f);
	}

	protected override void OnCloseButton()
	{
		base.OnCloseButton();
		MainSystem.ToolbarShowGui = false;
	}

	protected override void DrawGui()
	{
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0029: Expected O, but got Unknown
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		WindowRect = FixWindowPos(GUILayout.Window(1664154306, WindowRect, new GUI.WindowFunction(base.DrawContent), _title, Array.Empty<GUILayoutOption>()));
	}
}
