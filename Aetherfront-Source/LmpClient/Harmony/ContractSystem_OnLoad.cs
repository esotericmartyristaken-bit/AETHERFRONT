using Contracts;
using HarmonyLib;
using LmpClient.Systems.ShareContracts;
using LmpCommon.Enums;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// Suppress LMP's contract event handlers for the duration of <see cref="ContractSystem.OnLoad(ConfigNode)"/>.
    ///
    /// Background: when the bulk <c>ContractSystem</c> scenario data arrives from the server
    /// during the initial connect handshake, <c>ScenarioSystem.LoadSingleScenarioEntry</c> hands
    /// the ConfigNode to KSP's stock scenario loader. KSP then deserialises every saved contract
    /// and fires <c>GameEvents.Contract.onOffered</c> for each one in the <c>Offered</c> state,
    /// which lets ContractConfigurator (and us) react to "newly available" contracts.
    ///
    /// LMP's <see cref="ShareContractsEvents.ContractOffered"/> handler reacts to that event by
    /// killing any contract whose contract-lock is not held by the local player, on the (correct)
    /// theory that only the lock holder is allowed to spawn brand-new contracts for everyone. But
    /// during a bulk scenario restore the contract is NOT brand-new - it is being restored from
    /// the server's authoritative store, which is exactly the case we already special-case for
    /// the incremental <see cref="ShareContractsMessageHandler.ContractUpdate"/> path by setting
    /// <see cref="ShareContractsSystem.IgnoreEvents"/> while we replay restored state.
    ///
    /// Without this prefix/postfix wrap, every offered contract that arrives via the bulk path is
    /// immediately <c>Withdraw()</c>ed and <c>Kill()</c>ed by the connecting client - typically
    /// reducing a server with 1000+ offered contracts down to one or zero visible contracts in
    /// the local Mission Control. We've already paid for the bulk transfer at this point, so the
    /// loss is silent and easy to misdiagnose as "the server only has one contract".
    ///
    /// Bookkeeping notes:
    /// - We snapshot <c>IgnoreEvents</c> before flipping it so a nested OnLoad (defensive: we do
    ///   not believe KSP nests these, but we have seen ContractConfigurator drive secondary
    ///   loads) does not prematurely re-enable the handlers when the inner call returns.
    /// - The connection guard mirrors <c>ContractSystem_OnAwake</c> so single-player KSP runs
    ///   are unaffected.
    /// </summary>
    [HarmonyPatch(typeof(ContractSystem))]
    [HarmonyPatch("OnLoad")]
    public class ContractSystem_OnLoad
    {
        private static bool _wasIgnoringEvents;

        [HarmonyPrefix]
        private static void PrefixOnLoad()
        {
            if (MainSystem.NetworkState < ClientState.Connected) return;

            _wasIgnoringEvents = ShareContractsSystem.Singleton.IgnoreEvents;
            if (!_wasIgnoringEvents)
                ShareContractsSystem.Singleton.StartIgnoringEvents();
        }

        [HarmonyPostfix]
        private static void PostfixOnLoad()
        {
            if (MainSystem.NetworkState < ClientState.Connected) return;

            if (!_wasIgnoringEvents)
                ShareContractsSystem.Singleton.StopIgnoringEvents();
        }
    }
}
