using Aetherfront.Core;
using LmpCommon.Message.Data.Aetherfront;
using LmpCommon.Message.Server;
using Server.Aetherfront;
using Server.Client;
using Server.Context;
using Server.Server;
using Server.System;
using Server.System.Vessel.Classes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Aetherfront.Tracking
{
    public static class TrackingCatalog
    {
        private sealed class Poll { public Poll() { } internal long Last; }
        private static readonly ConditionalWeakTable<ClientStructure, Poll> Polls = new();

        // Called only inside the authenticated, schema-checked snapshot route.
        public static bool TryReply(ClientStructure client, AetherfrontWireRequest request)
        {
            if (request.Operation != AetherfrontWireOperation.SnapshotRequest || request.Name != Wire.Route) return false;
            if (!Guid.TryParseExact(request.CommandId, "N", out _) ||
                (request.Description != "-" && !Wire.IsRevision(request.Description))) return true;
            var account = AetherfrontServerSystem.GetBoundAccountId(client);
            if (!client.Authenticated || string.IsNullOrWhiteSpace(account) || !AetherfrontServerSystem.Enabled) return true;

            var poll = Polls.GetOrCreateValue(client);
            lock (poll)
            {
                long now = Stopwatch.GetTimestamp();
                if (poll.Last != 0 && now - poll.Last < Stopwatch.Frequency * 2) return true;
                poll.Last = now;
            }

            var markers = ReadCatalog(account, client.Subspace);
            var json = AetherfrontJson.Serialize(markers);
            if (json.Length > Wire.MaxPayload) return true;
            var revision = Revision(json);
            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<AetherfrontCommandReplyMsgData>();
            data.PayloadJson = AetherfrontProtocol.Serialize(new AetherfrontWireReply
            {
                Operation = AetherfrontWireOperation.SnapshotReply,
                Accepted = true,
                ReasonCode = Wire.Route,
                TradeId = request.CommandId,
                FactionId = revision,
                InterestJson = request.Description == revision ? string.Empty : json
            });
            MessageQueuer.SendToClient<AetherfrontSrvMsg>(client, data);
            return true;
        }

        public static string Revision(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

        // Rebound by the graft to the store's read-only projection. Never ship the donor.
        private static List<Marker> ReadCatalog(string account, int subspace) => throw new NotSupportedException();

        public static List<Marker> Build(AetherfrontState state, string account, int subspace, DateTime now,
            IReadOnlyDictionary<Guid, Vessel> vessels, IReadOnlyDictionary<Guid, byte> removed)
        {
            var result = new List<Marker>();
            var viewer = state.FindFactionForAccount(account);
            foreach (var craft in state.Crafts.OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                if (craft.Archived || !Guid.TryParse(craft.Id, out var id) || id == Guid.Empty ||
                    removed.ContainsKey(id) || !vessels.TryGetValue(id, out var vessel)) continue;
                if (string.Equals(craft.OwnerAccountId, account, StringComparison.OrdinalIgnoreCase) ||
                    (viewer != null && string.Equals(craft.OwnerFactionId, viewer.Id, StringComparison.OrdinalIgnoreCase))) continue;

                var owner = state.FindFaction(craft.OwnerFactionId ?? string.Empty);
                // Public faction ships are discoverable even offline. Private shares need a
                // known compatible timeline; do not pass the domain's unknown-subspace wildcard.
                if (owner?.PublicProfile != true)
                {
                    if (subspace == int.MinValue || !TrySubspace(id, out int actual) || actual != subspace ||
                        !AetherfrontDomain.CanControlCraft(state, account, viewer?.Id ?? string.Empty,
                            craft.Id, CraftAccess.ViewMetadata, now, actual, subspace)) continue;
                }

                var marker = Read(id, craft.Name, vessel);
                if (marker == null || removed.ContainsKey(id)) continue;
                result.Add(marker);
                // shortcut: stable bounded page; add body-scoped paging if more than 128 foreign markers are needed.
                if (result.Count == Wire.Limit) break;
            }
            return result;
        }

        private static bool TrySubspace(Guid id, out int subspace)
        {
            subspace = Server.Message.VesselMsgReader.GetVesselSubspace(id);
            return subspace != int.MinValue;
        }

        public static Marker Read(Guid id, string name, Vessel vessel)
        {
            if (!int.TryParse(vessel.Orbit.GetSingle("REF")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int body))
                return null;
            var surface = IsTrue(vessel.Fields.GetSingle("landed")?.Value) ||
                IsTrue(vessel.Fields.GetSingle("splashed")?.Value);
            var fields = surface ? vessel.Fields : vessel.Orbit;
            var keys = surface ? new[] { "lat", "lon", "alt" } : new[] { "INC", "ECC", "SMA", "LAN", "LPE", "MNA", "EPH" };
            var values = new double[keys.Length];
            for (int i = 0; i < keys.Length; i++)
                if (!double.TryParse(fields.GetSingle(keys[i])?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                    return null;
            var marker = new Marker { Id = id.ToString("N"), Name = Trim(name, 64),
                Body = Trim(vessel.GetOrbitingBodyName(), 64), ReferenceBody = body, Surface = surface, Position = values };
            return marker.HasValidPosition() ? marker : null;
        }

        private static bool IsTrue(string value) => bool.TryParse(value, out var result) && result;
        private static string Trim(string value, int limit) =>
            string.IsNullOrEmpty(value) ? string.Empty : value.Substring(0, Math.Min(value.Length, limit));
    }
}
