using Contracts;
using Contracts.Templates;
using LmpClient.Base;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Locks;

namespace LmpClient.Systems.ShareContracts
{
    public class ShareContractsEvents : SubSystem<ShareContractsSystem>
    {
        /// <summary>
        /// If we get the contract lock then generate contracts
        /// </summary>
        public void LockAcquire(LockDefinition lockDefinition)
        {
            if (lockDefinition.Type == LockType.Contract && lockDefinition.PlayerName == SettingsSystem.CurrentSettings.PlayerName)
            {
                ContractSystem.generateContractIterations = ShareContractsSystem.Singleton.DefaultContractGenerateIterations;
            }
        }

        /// <summary>
        /// Try to get contract lock
        /// </summary>
        public void LockReleased(LockDefinition lockDefinition)
        {
            if (lockDefinition.Type == LockType.Contract)
            {
                System.TryGetContractLock();
            }
        }

        /// <summary>
        /// Try to get contract lock when loading a level
        /// </summary>
        public void LevelLoaded(GameScenes data)
        {
            System.TryGetContractLock();
        }

        #region EventHandlers

        public void ContractAccepted(Contract contract)
        {
            if (System.IgnoreEvents) return;

            System.MessageSender.SendContractMessage(contract);
            LunaLog.Log($"Contract accepted: {contract.ContractGuid}");
        }

        public void ContractCancelled(Contract contract)
        {
            if (System.IgnoreEvents) return;

            System.MessageSender.SendContractMessage(contract);
            LunaLog.Log($"Contract cancelled: {contract.ContractGuid}");
        }

        public void ContractCompleted(Contract contract)
        {
            if (System.IgnoreEvents) return;

            System.MessageSender.SendContractMessage(contract);
            LunaLog.Log($"Contract completed: {contract.ContractGuid}");
        }

        public void ContractsListChanged()
        {
            LunaLog.Log("Contract list changed.");
        }

        public void ContractsLoaded()
        {
            LunaLog.Log("Contracts loaded.");
        }

        public void ContractDeclined(Contract contract)
        {
            if (System.IgnoreEvents) return;

            System.MessageSender.SendContractMessage(contract);
            LunaLog.Log($"Contract declined: {contract.ContractGuid}");
        }

        public void ContractFailed(Contract contract)
        {
            if (System.IgnoreEvents) return;

            System.MessageSender.SendContractMessage(contract);
            LunaLog.Log($"Contract failed: {contract.ContractGuid}");
        }

        public void ContractFinished(Contract contract)
        {
            // KSP fires onFinished on EVERY transition into a terminal state, but it
            // does not expose dedicated onWithdrawn / onDeadlineExpired events. The
            // sibling handlers (Accepted/Cancelled/Completed/Declined/Failed) already
            // call SendContractMessage for the transitions that DO have dedicated
            // events, so we only need to act here for the terminal states with no
            // dedicated event of their own. Without this, an Offered contract whose
            // offer-expiry passes (Withdrawn) silently disappears on the local client
            // and the server keeps storing it as state = Offered, so reconnecting
            // players see a phantom Available contract that vanishes from their UI
            // the moment their UT crosses the stale expiry. Same story for
            // DeadlineExpired on Active contracts (the contract's post-acceptance
            // deadline elapses without onFailed firing).
            if (System.IgnoreEvents) return;

            if (contract.ContractState != Contract.State.Withdrawn &&
                contract.ContractState != Contract.State.DeadlineExpired)
                return;

            System.MessageSender.SendContractMessage(contract);
            LunaLog.Log($"Contract finished ({contract.ContractState}): {contract.ContractGuid}");
        }

        public void ContractOffered(Contract contract)
        {
            // Honor IgnoreEvents the same way every other handler does. KSP fires
            // onOffered both for genuinely-new contracts AND for contracts we've just
            // restored locally via ShareContractsMessageHandler.AddContract (which
            // explicitly calls GameEvents.Contract.onOffered.Fire). In the latter
            // case ContractUpdate sets IgnoreEvents=true precisely so handlers like
            // this one don't react to state we just installed from the wire. Without
            // this guard, an incoming Offered contract from a peer who DID hold the
            // contract lock gets immediately Withdrawn+Killed locally because our
            // own lock query (still racing the lock-acquire round-trip) returns false.
            if (System.IgnoreEvents) return;

            if (!LockSystem.LockQuery.ContractLockBelongsToPlayer(SettingsSystem.CurrentSettings.PlayerName))
            {
                //We don't have the contract lock so remove the contract that we spawned.
                //The idea is that ONLY THE PLAYER with the contract lock spawn contracts to the other players
                contract.Withdraw();
                contract.Kill();
                return;
            }

            if (contract.GetType().Name == "RecoverAsset")
            {
                //We don't support rescue contracts. See: https://github.com/LunaMultiplayer/LunaMultiplayer/issues/226#issuecomment-431831526
                contract.Withdraw();
                contract.Kill();
                return;
            }

            if (contract.GetType().Name == "TourismContract")
            {
                //We don't support tourism contracts.
                contract.Withdraw();
                contract.Kill();
                return;
            }

            LunaLog.Log($"Contract offered: {contract.ContractGuid} - {contract.Title}");

            //This should be only called on the client with the contract lock, because it has the generationCount != 0.
            System.MessageSender.SendContractMessage(contract);
        }

        public void ContractParameterChanged(Contract contract, ContractParameter contractParameter)
        {
            //Do not send contract parameter changes as other players might override them
            //See: https://github.com/LunaMultiplayer/LunaMultiplayer/issues/186

            //TODO: Perhaps we can send only when the parameters are complete?
            //if (contractParameter.State == ParameterState.Complete)
            //    System.MessageSender.SendContractMessage(contract);

            LunaLog.Log($"Contract parameter changed on:{contract.ContractGuid}");
        }

        public void ContractRead(Contract contract)
        {
            LunaLog.Log($"Contract read:{contract.ContractGuid}");
        }

        public void ContractSeen(Contract contract)
        {
            LunaLog.Log($"Contract seen:{contract.ContractGuid}");
        }

        #endregion
    }
}
