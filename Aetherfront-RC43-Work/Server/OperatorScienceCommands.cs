using System;
using System.Globalization;
using System.Linq;
using Aetherfront.Core;
using LmpCommon.Message.Data.Aetherfront;
using LmpCommon.Message.Server;
using Server.Aetherfront;
using Server.Command;
using Server.Context;
using Server.Log;
using Server.Server;

namespace Aetherfront.Personal.Server
{
    // Console/ACL-protected bridge only; never exposed as a player wire command.
    // The native /setscience command edits the retired shared wallet, so replace
    // it with an explicit account selector. A one-number global edit is refused.
    public static class OperatorScienceCommands
    {
        private static readonly object Gate = new object();
        private static bool installed;
        public static void Install()
        {
            if (!AetherfrontServerSystem.Enabled) return;
            lock (Gate)
            {
                if (installed) return;
                CommandDefinition current;
                if (!CommandHandler.Commands.TryGetValue("setscience", out current) ||
                    current.Func.Method.DeclaringType.FullName != "Server.Command.Command.SetScienceCommand") return;
                CommandHandler.Commands["setscience"] = new CommandDefinition("setscience", Execute,
                    "Personal science: PLAYER BALANCE, or PLAYER +AMOUNT UNIQUE-RECEIPT-ID. No global edits.");
                installed = true;
            }
        }

        public static bool Execute(string argument)
        {
            try
            {
                var parts = (argument ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                decimal amount;
                bool grant = parts.Length == 3 && parts[1].StartsWith("+", StringComparison.Ordinal);
                if ((parts.Length != 2 && !grant) || parts[0].Length > 120 ||
                    !decimal.TryParse(grant ? parts[1].Substring(1) : parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount) ||
                    amount < 0 || amount > PersonalProgression.MaxScience || decimal.Round(amount, 6) != amount ||
                    (grant && (amount <= 0 || amount > 10000 || parts[2].Length < 8 || parts[2].Length > 80 ||
                        parts[2].Any(c => !((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')))))
                { LunaLog.Error("Use setscience PLAYER BALANCE or PLAYER +AMOUNT UNIQUE-RECEIPT-ID; no global science command is supported."); return false; }
                string selector = parts[0], marker = grant ? "operator-science:" + parts[2] : null;
                decimal before = 0, after = 0; bool replay = false;
                var result = AetherfrontServerSystem.Execute(state =>
                {
                    var matches = state.Accounts.Where(a => string.Equals(a.AccountId, selector, StringComparison.Ordinal) ||
                        string.Equals(a.DisplayName, selector, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length != 1 || matches[0].Suspended) return AetherfrontResult.Reject(state.Revision, "UniqueActiveAccountRequired");
                    var player = PersonalProgression.Find(state, matches[0].AccountId);
                    if (player == null) return AetherfrontResult.Reject(state.Revision, "PersonalProgressionMissing");
                    before = player.Science;
                    var previous = grant ? player.ScienceSources.FirstOrDefault(s => s.Id == marker) : null;
                    if (previous != null)
                    {
                        if (previous.CumulativeScience != amount || previous.CreditedScience != 0) return AetherfrontResult.Reject(state.Revision, "OperatorReceiptConflict");
                        replay = true; after = before; return AetherfrontResult.Success(state.Revision);
                    }
                    after = grant ? checked(before + amount) : amount;
                    if (before < 0 || after < 0 || after > PersonalProgression.MaxScience || state.Revision == long.MaxValue ||
                        (grant && player.ScienceSources.Count >= PersonalProgression.MaxSources)) return AetherfrontResult.Reject(state.Revision, "PersonalScienceLimit");
                    player.Science = after;
                    // Administrative adjustments are research credit, not earned
                    // science: no shared pool, tradable mint or sequence change.
                    player.TradableScience = Math.Min(player.TradableScience, after);
                    if (grant) player.ScienceSources.Add(new ScienceSourceCredit { Id = marker, CumulativeScience = amount, CreditedScience = 0 });
                    state.Revision++;
                    return AetherfrontResult.Success(state.Revision);
                });
                if (!result.Accepted) { LunaLog.Error("Personal science adjustment rejected: " + result.ReasonCode); return false; }
                LunaLog.Normal("Personal science adjustment " + (replay ? "already applied" : "saved") + ": player=" + selector +
                    " before=" + before.ToString(CultureInfo.InvariantCulture) + " after=" + after.ToString(CultureInfo.InvariantCulture) +
                    (grant ? " receipt=" + parts[2] : ""));
                if (!replay)
                {
                  try
                  {
                    var current = AetherfrontServerSystem.Read(s => new { s.Revision, s.ServerId });
                    var message = ServerContext.ServerMessageFactory.CreateNewMessageData<AetherfrontEventNoticeMsgData>();
                    message.PayloadJson = AetherfrontProtocol.Serialize(new AetherfrontWireReply { Operation = AetherfrontWireOperation.EventNotice,
                        Accepted = true, EventsJson = "[]", Revision = current.Revision, ServerId = current.ServerId });
                    MessageQueuer.SendToAllClients<AetherfrontSrvMsg>(message);
                  }
                  catch (Exception error) { LunaLog.Warning("Personal science adjustment was saved; refresh notice failed: " + error.GetType().Name); }
                }
                return true;
            }
            catch (Exception error) { LunaLog.Error("Personal science adjustment failed: " + error.GetType().Name + "; inspect durable state before retrying."); return false; }
        }
    }
}
