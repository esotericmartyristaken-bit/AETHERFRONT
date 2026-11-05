using System;
using System.Collections.Generic;
using Aetherfront.Core;

namespace Aetherfront.Personal.Client
{
    // Unity-thread intent queue. Never changes the authoritative wallet, unlocks,
    // or sequence locally. A saved unlock, not an uncorrelated reply, confirms it.
    public sealed class ResearchQueue
    {
        private readonly List<string> pending = new List<string>();
        private string server, account;
        private bool flying, refreshing, paused;
        private int attempts;
        private long sequence, snapshotAtRefresh;
        private double sentAt, nextSend;
        public bool SnapshotNeeded { get; private set; }
        public string Warning { get; private set; }
        public int Count { get { return pending.Count; } }

        // A presentation-only reservation. Never mutate the server snapshot or
        // spend a wallet locally. Protect queued stock purchases from an older
        // snapshot while keeping costs and prerequisite ordering visible.
        public HashSet<string> Project(AetherfrontState state, PlayerProgression player, out decimal science)
        {
            var desired = new HashSet<string>(player.TechnologyUnlocks, StringComparer.OrdinalIgnoreCase);
            science = player.Science;
            if (paused || state.ServerId != server || player.AccountId != account) return desired;
            foreach (string id in pending)
            {
                if (desired.Contains(id)) continue;
                var definition = state.FindTechnology(id);
                if (definition == null || definition.ScienceCost < 0 || science < definition.ScienceCost) break;
                bool any = false, all = true;
                foreach (string prerequisite in definition.PrerequisiteIds)
                { bool has = desired.Contains(prerequisite); any |= has; all &= has; }
                if (definition.PrerequisiteIds.Count > 0 && !(definition.AnyPrerequisite ? any : all)) break;
                desired.Add(definition.Id); science -= definition.ScienceCost;
            }
            return desired;
        }

        public void Context(string serverId, string accountId, bool active)
        {
            if (!active || server != serverId || account != accountId)
            {
                pending.Clear(); flying = refreshing = paused = false; attempts = 0;
                nextSend = 0; Warning = null; SnapshotNeeded = false;
                server = active ? serverId : null; account = active ? accountId : null;
            }
        }

        public bool Enqueue(string technology)
        {
            if (string.IsNullOrWhiteSpace(technology) || technology.Length > 256) return false;
            for (int i = 0; i < pending.Count; i++)
                if (string.Equals(pending[i], technology, StringComparison.OrdinalIgnoreCase))
                {
                    if (paused && i == 0) { paused = false; attempts = 0; Warning = null; }
                    return false;
                }
            if (pending.Count >= 32) { Warning = "Research queue is full. Wait for confirmation before buying more."; return false; }
            pending.Add(technology); return true;
        }

        private static bool Unlocked(PlayerProgression player, string technology)
        {
            foreach (string id in player.TechnologyUnlocks)
                if (string.Equals(id, technology, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void Remove()
        {
            pending.RemoveAt(0); flying = refreshing = paused = false; attempts = 0;
        }

        private static string Validate(AetherfrontState state, PlayerProgression player, string technology)
        {
            TechnologyDefinition definition = null;
            foreach (var item in state.TechnologyDefinitions)
                if (item != null && string.Equals(item.Id, technology, StringComparison.OrdinalIgnoreCase)) { definition = item; break; }
            if (definition == null) return "Technology is not available on this server.";
            if (player.Science < definition.ScienceCost) return "Not enough personal science for this purchase.";
            bool any = false, all = true;
            foreach (string prerequisite in definition.PrerequisiteIds)
            {
                bool has = Unlocked(player, prerequisite); any |= has; all &= has;
            }
            if (definition.PrerequisiteIds.Count > 0 && !(definition.AnyPrerequisite ? any : all))
                return "Unlock the prerequisite technology first.";
            return null;
        }

        public AetherfrontWireRequest Step(AetherfrontState state, string actor, double now, long snapshotTicks)
        {
            SnapshotNeeded = false;
            if (state == null || server != state.ServerId || account != actor) return null;
            var player = PersonalProgression.Find(state, actor);
            if (player == null) return null;
            while (pending.Count > 0 && Unlocked(player, pending[0])) { Remove(); Warning = null; }
            if (pending.Count == 0 || paused) return null;
            if (flying)
            {
                if (player.NextCommandSequence > sequence)
                {
                    // A different purchase/transfer won the race. The node is
                    // still locked, so retry this intent with the saved counter.
                    flying = refreshing = false; nextSend = Math.Max(nextSend, now + 0.5);
                }
                else if (now - sentAt >= 10)
                {
                    if (!refreshing)
                    {
                        refreshing = true; snapshotAtRefresh = snapshotTicks; SnapshotNeeded = true;
                    }
                    else if (snapshotTicks > snapshotAtRefresh)
                    {
                        flying = refreshing = false; nextSend = Math.Max(nextSend, now + 0.5);
                    }
                    else if (now - sentAt >= 30)
                    {
                        paused = true; flying = false;
                        Warning = "Research confirmation timed out. Reconnect or retry this purchase.";
                    }
                }
                return null;
            }
            if (now < nextSend) return null;
            if (attempts >= 3)
            {
                paused = true; Warning = "Research sync paused after 3 attempts. Reconnect or retry this purchase.";
                return null;
            }
            string rejection = Validate(state, player, pending[0]);
            if (rejection != null)
            {
                Remove(); Warning = rejection; SnapshotNeeded = true; nextSend = now + 1;
                return null;
            }
            flying = true; attempts++; sequence = player.NextCommandSequence;
            sentAt = now; nextSend = now + 1; Warning = null;
            return new AetherfrontWireRequest {
                CommandType = AetherfrontCommandType.UnlockTechnology, TechnologyId = pending[0],
                Amount2 = sequence, CommandId = "research:" + Guid.NewGuid().ToString("N")
            };
        }
    }
}
