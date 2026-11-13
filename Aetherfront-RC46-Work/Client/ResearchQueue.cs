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
        private readonly Dictionary<string,Intent> intents = new Dictionary<string,Intent>(StringComparer.OrdinalIgnoreCase);
        public sealed class Intent
        {
            public string TechnologyId { get; set; }
            public decimal MaximumCost { get; set; }
            public string CommandId { get; set; }
            public long Sequence { get; set; }
        }
        public const int MaximumPending=4096;
        public long Revision { get; private set; }
        private string server, account;
        private bool flying, refreshing, paused, active;
        private int attempts;
        private long sequence, snapshotAtRefresh;
        private double sentAt, nextSend;
        private bool awaitResumeSnapshot;
        private long resumeSnapshot=-1;
        private double resumeQuery;
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
            if (state.ServerId != server || player.AccountId != account) return desired;
            foreach (string id in pending)
            {
                if (desired.Contains(id)) continue;
                var definition = state.FindTechnology(id);
                if (definition == null || definition.ScienceCost < 0 || science < definition.ScienceCost || definition.ScienceCost>intents[id].MaximumCost) break;
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
            this.active=active;
            if(!active){flying=refreshing=false;if(pending.Count>0){awaitResumeSnapshot=true;resumeSnapshot=-1;}return;}
            if (server != serverId || account != accountId)
            {
                pending.Clear(); intents.Clear(); flying = refreshing = paused = false; attempts = 0;
                Revision++;
                awaitResumeSnapshot=false;resumeSnapshot=-1;
                nextSend = 0; Warning = null; SnapshotNeeded = false;
                server = active ? serverId : null; account = active ? accountId : null;
            }
        }

        public bool Enqueue(string technology) => Enqueue(technology,decimal.MaxValue);
        public bool Enqueue(string technology,decimal maximumCost)
        {
            if (string.IsNullOrWhiteSpace(technology) || technology.Length > 256 || maximumCost<0) return false;
            for (int i = 0; i < pending.Count; i++)
                if (string.Equals(pending[i], technology, StringComparison.OrdinalIgnoreCase))
                {
                    if (paused && i == 0) { paused = false; attempts = 0; Warning = null; }
                    return false;
                }
            if (pending.Count >= MaximumPending) { Warning = "Research pending list reached its safety limit. Existing purchases are retained; wait for confirmation."; return false; }
            pending.Add(technology);intents.Add(technology,new Intent{TechnologyId=technology,MaximumCost=maximumCost});Revision++; return true;
        }
        public Intent[] Export(){var result=new List<Intent>();foreach(string id in pending){var x=intents[id];result.Add(new Intent{TechnologyId=x.TechnologyId,MaximumCost=x.MaximumCost,CommandId=x.CommandId,Sequence=x.Sequence});}return result.ToArray();}
        public void Restore(Intent[] saved)
        {
            if(saved==null||saved.Length>MaximumPending)throw new ArgumentException("Invalid saved research pending list.");
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var x in saved)if(x==null||string.IsNullOrWhiteSpace(x.TechnologyId)||x.TechnologyId.Length>256||x.MaximumCost<0||x.Sequence<0||
                (!string.IsNullOrEmpty(x.CommandId)&&!System.Text.RegularExpressions.Regex.IsMatch(x.CommandId,"^research:[a-f0-9]{32}$"))||!seen.Add(x.TechnologyId))throw new ArgumentException("Invalid saved research intent.");
            pending.Clear();intents.Clear();flying=refreshing=paused=false;attempts=0;nextSend=0;
            foreach(var x in saved){pending.Add(x.TechnologyId);intents.Add(x.TechnologyId,new Intent{TechnologyId=x.TechnologyId,MaximumCost=x.MaximumCost,CommandId=x.CommandId,Sequence=x.Sequence});}
            Revision++;
            awaitResumeSnapshot=pending.Count>0;resumeSnapshot=-1;
        }

        private static bool Unlocked(PlayerProgression player, string technology)
        {
            foreach (string id in player.TechnologyUnlocks)
                if (string.Equals(id, technology, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void Remove()
        {
            intents.Remove(pending[0]);
            Revision++;
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
            if (!active || state == null || server != state.ServerId || account != actor) return null;
            var player = PersonalProgression.Find(state, actor);
            if (player == null) return null;
            if(awaitResumeSnapshot)
            {
                if(resumeSnapshot<0){resumeSnapshot=snapshotTicks;resumeQuery=now;SnapshotNeeded=true;return null;}
                if(snapshotTicks<=resumeSnapshot){if(now-resumeQuery>=10){SnapshotNeeded=true;resumeQuery=now;}return null;}
                awaitResumeSnapshot=false;
            }
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
                        flying = refreshing = false;nextSend=now+15;
                        Warning = "Research confirmation delayed. Purchases are retained and will retry automatically.";
                    }
                }
                return null;
            }
            if (now < nextSend) return null;
            string rejection = Validate(state, player, pending[0]);
            var intent=intents[pending[0]];
            if(state.FindTechnology(pending[0])?.ScienceCost>intent.MaximumCost)rejection="Research price changed. Purchase cancelled without an additional charge; select the node again to approve the new price.";
            if (rejection != null)
            {
                Remove(); Warning = rejection; SnapshotNeeded = true; nextSend = now + 1;
                return null;
            }
            flying = true; attempts=Math.Min(30,attempts+1); sequence = player.NextCommandSequence;
            sentAt = now; nextSend = now + Math.Min(30,Math.Max(1,attempts)); Warning = null;
            if(string.IsNullOrEmpty(intent.CommandId)||intent.Sequence!=sequence){intent.CommandId="research:"+Guid.NewGuid().ToString("N");intent.Sequence=sequence;Revision++;}
            return new AetherfrontWireRequest {
                CommandType = AetherfrontCommandType.UnlockTechnology, TechnologyId = pending[0],
                Amount2 = sequence, CommandId = intent.CommandId
            };
        }
    }
}
