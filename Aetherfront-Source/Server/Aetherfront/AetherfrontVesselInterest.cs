using Aetherfront.Core;
using Server.Client;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Server.Aetherfront
{
    /// <summary>
    /// In-memory live-vessel index for lightweight activity summaries. It never stores
    /// vessel ConfigNode data and is intentionally separate from durable faction state.
    /// </summary>
    internal static class AetherfrontVesselInterest
    {
        private static readonly ConcurrentDictionary<Guid, LiveVessel> Vessels =
            new ConcurrentDictionary<Guid, LiveVessel>();
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
        private const int MaxSummaries = 256;

        public static void UpdateProto(ClientStructure client, Guid vesselId, string name, int subspaceId)
        {
            if (!AetherfrontServerSystem.Enabled || client == null || vesselId == Guid.Empty) return;
            var accountId = AetherfrontServerSystem.GetBoundAccountId(client);
            Vessels.AddOrUpdate(vesselId,
                _ => new LiveVessel
                {
                    VesselId = vesselId,
                    OwnerAccountId = accountId,
                    FactionId = string.Empty,
                    Name = NormalizeName(name, vesselId),
                    SubspaceId = subspaceId,
                    LastSeenAtUtc = DateTime.UtcNow
                },
                (_, existing) =>
                {
                    existing.OwnerAccountId = string.IsNullOrWhiteSpace(existing.OwnerAccountId) ? accountId : existing.OwnerAccountId;
                    existing.Name = NormalizeName(name, vesselId);
                    existing.SubspaceId = subspaceId;
                    existing.LastSeenAtUtc = DateTime.UtcNow;
                    return existing;
                });
        }

        public static void UpdatePosition(ClientStructure client, Guid vesselId, string bodyName,
            int subspaceId, bool landed, bool splashed)
        {
            if (!AetherfrontServerSystem.Enabled || client == null || vesselId == Guid.Empty) return;
            var accountId = AetherfrontServerSystem.GetBoundAccountId(client);
            Vessels.AddOrUpdate(vesselId,
                _ => new LiveVessel
                {
                    VesselId = vesselId,
                    OwnerAccountId = accountId,
                    FactionId = string.Empty,
                    Name = vesselId.ToString(),
                    BodyName = NormalizeBody(bodyName),
                    SubspaceId = subspaceId,
                    Landed = landed,
                    Splashed = splashed,
                    LastSeenAtUtc = DateTime.UtcNow
                },
                (_, existing) =>
                {
                    existing.OwnerAccountId = string.IsNullOrWhiteSpace(existing.OwnerAccountId) ? accountId : existing.OwnerAccountId;
                    existing.BodyName = NormalizeBody(bodyName);
                    existing.SubspaceId = subspaceId;
                    existing.Landed = landed;
                    existing.Splashed = splashed;
                    existing.LastSeenAtUtc = DateTime.UtcNow;
                    return existing;
                });
        }

        public static void Remove(Guid vesselId)
        {
            if (vesselId != Guid.Empty) Vessels.TryRemove(vesselId, out _);
        }

        public static bool AllowsEventSync(AetherfrontState state, Guid vesselId, int actorSubspaceId)
        {
            var mode = state?.EventMode;
            if (mode == null || !mode.Active || !mode.AllowAllCrafts || vesselId == Guid.Empty) return false;
            var now = DateTime.UtcNow;
            if (mode.StartsAtUtc.HasValue && mode.StartsAtUtc.Value > now) return false;
            if (mode.EndsAtUtc.HasValue && mode.EndsAtUtc.Value <= now) return false;
            if (mode.TargetSubspaceId != int.MinValue && mode.TargetSubspaceId != actorSubspaceId) return false;
            if (!Vessels.TryGetValue(vesselId, out var live)) return false;
            if (live.SubspaceId != actorSubspaceId) return false;
            if (!string.IsNullOrWhiteSpace(mode.BodyName) &&
                !string.Equals(mode.BodyName, live.BodyName, StringComparison.OrdinalIgnoreCase)) return false;
            return now - live.LastSeenAtUtc <= StaleAfter;
        }

        public static string ForClient(AetherfrontState state, string accountId, int actorSubspaceId)
        {
            var now = DateTime.UtcNow;
            var ownFaction = state.FindFactionForAccount(accountId ?? string.Empty);
            var summaries = new List<AetherfrontVesselSummary>();
            foreach (var live in Vessels.Values)
            {
                if (now - live.LastSeenAtUtc > StaleAfter)
                {
                    Vessels.TryRemove(live.VesselId, out _);
                    continue;
                }
                var craft = state.FindCraft(live.VesselId.ToString());
                var eventSync = AllowsEventSync(state, live.VesselId, actorSubspaceId);
                if (craft == null)
                {
                    if (!eventSync) continue;
                    summaries.Add(new AetherfrontVesselSummary
                    {
                        VesselId = live.VesselId.ToString(),
                        Name = live.Name,
                        FactionId = string.Empty,
                        FactionName = "Unaffiliated",
                        BodyName = live.BodyName,
                        SubspaceId = live.SubspaceId,
                        Landed = live.Landed,
                        Splashed = live.Splashed,
                        CanControl = false,
                        FullSyncAvailable = true,
                        LastSeenAtUtc = live.LastSeenAtUtc
                    });
                    continue;
                }
                if (craft.Archived) continue;
                var faction = state.FindFaction(craft.OwnerFactionId) ?? state.FindFaction(live.FactionId);
                var own = string.Equals(craft.OwnerAccountId, accountId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(craft.OwnerFactionId, ownFaction?.Id, StringComparison.OrdinalIgnoreCase);
                var metadataAllowed = own || eventSync || (faction != null && faction.PublicProfile) ||
                    AetherfrontDomain.CanControlCraft(state, accountId ?? string.Empty, ownFaction?.Id ?? string.Empty,
                        craft.Id, CraftAccess.ViewMetadata, now, live.SubspaceId, actorSubspaceId);
                if (!metadataAllowed) continue;
                var canControl = AetherfrontDomain.CanControlCraft(state, accountId ?? string.Empty,
                    ownFaction?.Id ?? string.Empty, craft.Id, CraftAccess.Use, now, live.SubspaceId, actorSubspaceId);
                var fullSync = eventSync || AetherfrontDomain.CanControlCraft(state, accountId ?? string.Empty,
                    ownFaction?.Id ?? string.Empty, craft.Id, CraftAccess.ViewFull, now, live.SubspaceId, actorSubspaceId);
                summaries.Add(new AetherfrontVesselSummary
                {
                    VesselId = live.VesselId.ToString(),
                    Name = live.Name,
                    FactionId = faction?.Id ?? craft.OwnerFactionId ?? string.Empty,
                    FactionName = faction?.Name ?? "Unaffiliated",
                    BodyName = live.BodyName,
                    SubspaceId = live.SubspaceId,
                    Landed = live.Landed,
                    Splashed = live.Splashed,
                    CanControl = canControl,
                    FullSyncAvailable = fullSync,
                    LastSeenAtUtc = live.LastSeenAtUtc
                });
            }
            var snapshot = new AetherfrontVesselInterestSnapshot
            {
                GeneratedAtUtc = now,
                Vessels = summaries.OrderByDescending(v => v.LastSeenAtUtc).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Take(MaxSummaries).ToList()
            };
            return AetherfrontJson.Serialize(snapshot);
        }

        private static string NormalizeName(string name, Guid vesselId)
        {
            var value = (name ?? string.Empty).Trim();
            return value.Length == 0 ? vesselId.ToString() : value.Length > 96 ? value.Substring(0, 96) : value;
        }

        private static string NormalizeBody(string bodyName)
        {
            var value = (bodyName ?? string.Empty).Trim();
            return value.Length > 96 ? value.Substring(0, 96) : value;
        }

        private sealed class LiveVessel
        {
            public Guid VesselId;
            public string OwnerAccountId = string.Empty;
            public string FactionId = string.Empty;
            public string Name = string.Empty;
            public string BodyName = string.Empty;
            public int SubspaceId = int.MinValue;
            public bool Landed;
            public bool Splashed;
            public DateTime LastSeenAtUtc;
        }
    }
}
