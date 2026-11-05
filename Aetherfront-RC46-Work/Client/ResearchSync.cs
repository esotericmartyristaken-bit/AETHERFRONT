using System;
using System.Collections.Generic;
using Aetherfront.Core;
using LmpClient;
using LmpClient.Aetherfront;
using UnityEngine;

namespace Aetherfront.Personal.Client
{
    public static class ResearchSync
    {
        private static readonly ResearchQueue queue = new ResearchQueue();
        private static string displayedWarning;
        public static string Status()
        {
            return queue.Warning ?? (queue.Count > 0 ? "Research sync: " + queue.Count + " purchase(s) awaiting server confirmation." : string.Empty);
        }
        private static void Context()
        {
            queue.Context(AetherfrontClientSystem.State.ServerId, MainSystem.UniqueIdentifier, PersonalClient.Active);
        }
        public static HashSet<string> Project(AetherfrontState state, PlayerProgression player, out decimal science)
        {
            Context(); return queue.Project(state, player, out science);
        }
        public static void Request(string technology)
        {
            Context();
            if (!PersonalClient.CanRequest(technology)) return;
            queue.Enqueue(technology); Tick(); PersonalClient.Apply();
        }
        public static void Tick()
        {
            Context();
            if (!PersonalClient.Active) { displayedWarning = null; return; }
            int before = queue.Count;
            string previousWarning = queue.Warning;
            var request = queue.Step(AetherfrontClientSystem.State, MainSystem.UniqueIdentifier,
                Time.realtimeSinceStartup, AetherfrontClientSystem.LastSnapshotUtc.Ticks);
            if (queue.SnapshotNeeded) AetherfrontClientSystem.RequestSnapshot();
            if (request != null) AetherfrontClientSystem.SendCommand(request);
            if (before != queue.Count || previousWarning != queue.Warning) PersonalClient.Apply();
            if (queue.Warning != displayedWarning)
            {
                displayedWarning = queue.Warning;
                if (!string.IsNullOrEmpty(displayedWarning))
                {
                    Debug.LogWarning("[Aetherfront] " + displayedWarning);
                    ScreenMessages.PostScreenMessage(displayedWarning, 8, ScreenMessageStyle.UPPER_CENTER);
                }
            }
        }
        public static void DrawStatus()
        {
            string status = Status(); if (!string.IsNullOrEmpty(status)) GUILayout.Label(status);
        }
    }
}
