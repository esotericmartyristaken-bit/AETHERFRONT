using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Aetherfront.Core;
using LmpClient;
using LmpClient.Aetherfront;
using UnityEngine;

namespace Aetherfront.Personal.Client
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class ScienceEvents : MonoBehaviour
    {
        private float nextHookAttempt;
        private bool subjectHookInstalled, historyHookInstalled, awardHookInstalled, submissionHookInstalled, hookErrorLogged;
        private static ScienceEvents live;
        private sealed class ObservedAward
        {
            internal int Frame; internal float Input; internal decimal Amount; internal long Reason;
            internal string Server, Account; internal bool HasSubject;
            internal Submission Owner;
        }
        public sealed class Submission
        {
            internal string Server, Account, Source;
            internal float Before, Multiplier;
            internal int ObservationStart;
        }
        private readonly List<ObservedAward> observed = new List<ObservedAward>();
        private readonly Stack<Submission> submissions = new Stack<Submission>();
        private void Awake() { live = this; DontDestroyOnLoad(this); GameEvents.OnScienceRecieved.Add(Received); }
        private void OnDestroy() { GameEvents.OnScienceRecieved.Remove(Received); if (live == this) live = null; }
        private void OnApplicationQuit() { FlushAwards(true); ScienceOutbox.Save(true); }
        private static bool Context() => PersonalClient.Active && HighLogic.CurrentGame != null && HighLogic.CurrentGame.Mode != Game.Modes.SANDBOX;
        private static bool ValidSubject(ScienceSubject subject) => subject != null && !string.IsNullOrEmpty(subject.id) && subject.id.Length <= 256 &&
            !subject.id.Any(char.IsControl) && !float.IsNaN(subject.science) && !float.IsInfinity(subject.science) && subject.science >= 0 && subject.science <= (float)PersonalProgression.MaxScience;
        private void Update()
        {
            FlushAwards(false); ScienceOutbox.Tick();
            if (subjectHookInstalled || Time.realtimeSinceStartup < nextHookAttempt) return;
            nextHookAttempt = Time.realtimeSinceStartup + 10;
            var harmony = Type.GetType("HarmonyLib.Harmony, 0Harmony", false);
            var harmonyMethod = Type.GetType("HarmonyLib.HarmonyMethod, 0Harmony", false);
            if (harmony == null || harmonyMethod == null) return;
            try
            {
                var patch = harmony.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length >= 5 && m.GetParameters()[0].ParameterType == typeof(MethodBase));
                var owner = Activator.CreateInstance(harmony, "aetherfront.personal.subject-history");
                if (!historyHookInstalled)
                {
                    var target = typeof(ResearchAndDevelopment).GetMethod("getScienceSubject", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(ScienceSubject) }, null);
                    if (target == null) throw new MissingMethodException("KSP science subject history hook");
                    var parameters = new object[patch.GetParameters().Length]; parameters[0] = target;
                    parameters[2] = Activator.CreateInstance(harmonyMethod, typeof(PersonalClient).GetMethod(nameof(PersonalClient.RestoreSubject)));
                    patch.Invoke(owner, parameters); historyHookInstalled = true;
                }
                if (!awardHookInstalled)
                {
                    var target = typeof(ResearchAndDevelopment).GetMethod("AddScience", new[] { typeof(float), typeof(TransactionReasons) });
                    if (target == null) throw new MissingMethodException("KSP science reward hook");
                    var parameters = new object[patch.GetParameters().Length]; parameters[0] = target;
                    parameters[1] = Activator.CreateInstance(harmonyMethod, typeof(ScienceEvents).GetMethod(nameof(BeforeScience)));
                    parameters[4] = Activator.CreateInstance(harmonyMethod, typeof(ScienceEvents).GetMethod(nameof(AfterScience)));
                    patch.Invoke(owner, parameters); awardHookInstalled = true;
                }
                if (!submissionHookInstalled)
                {
                    // Recovery reads scienceSubjects directly and never calls
                    // getScienceSubject. Patch the canonical overload shared by
                    // recovery and transmission, before KSP computes the reward.
                    var target = typeof(ResearchAndDevelopment).GetMethod("SubmitScienceData", new[] { typeof(float), typeof(float), typeof(ScienceSubject), typeof(float), typeof(ProtoVessel), typeof(bool) });
                    if (target == null) throw new MissingMethodException("KSP science submission hook");
                    var parameters = new object[patch.GetParameters().Length]; parameters[0] = target;
                    parameters[1] = Activator.CreateInstance(harmonyMethod, typeof(ScienceEvents).GetMethod(nameof(BeforeSubmission)));
                    parameters[4] = Activator.CreateInstance(harmonyMethod, typeof(ScienceEvents).GetMethod(nameof(AfterSubmission)));
                    patch.Invoke(owner, parameters); submissionHookInstalled = true;
                }
                subjectHookInstalled = true; hookErrorLogged = false;
                Debug.Log("[Aetherfront] Personal science history, reward and submission hooks ready.");
            }
            catch (Exception error)
            {
                // A partial installation must not permanently disable missing
                // hooks, nor duplicate already installed hooks on retry.
                if (!hookErrorLogged) Debug.LogError("[Aetherfront] Science capture hook incomplete; retrying: " + (error.InnerException ?? error));
                hookErrorLogged = true;
            }
        }
        public static void BeforeScience(ResearchAndDevelopment __instance, out float __state) { __state = __instance.Science; }
        public static void AfterScience(ResearchAndDevelopment __instance, float value, TransactionReasons reason, float __state, Exception __exception)
        {
            if (live == null || value <= 0 || value > (float)PersonalProgression.MaxScience || float.IsNaN(value) || float.IsInfinity(value) ||
                (long)reason < 0 || ((long)reason & ~PersonalProgression.ScienceRewardReasons) != 0) return;
            // KSP mutates its wallet before firing currency events. An event
            // exception skips a postfix; a finalizer still captures the actual
            // wallet increase. Never treat a pre-wallet failure as an award.
            if (__exception != null && !(__instance.Science > __state)) return;
            var submission = live.submissions.Count > 0 ? live.submissions.Peek() : null;
            if (submission == null && !Context()) return;
            live.observed.Add(new ObservedAward { Frame = Time.frameCount, Input = value, Amount = decimal.Round((decimal)(double)value, 6), Reason = (long)reason,
                Server = submission == null ? AetherfrontClientSystem.State.ServerId : submission.Server,
                Account = submission == null ? MainSystem.UniqueIdentifier : submission.Account, Owner = submission });
        }
        public static void BeforeSubmission(ScienceSubject __2, out Submission __state)
        {
            __state = null;
            if (live == null || live.submissions.Count >= 128 || !Context() || !ValidSubject(__2)) return;
            try
            {
                PersonalClient.RestoreSubject(__2);
                __state = new Submission { Server = AetherfrontClientSystem.State.ServerId, Account = MainSystem.UniqueIdentifier, Source = __2.id,
                    Before = __2.science, Multiplier = HighLogic.CurrentGame.Parameters.Career.ScienceGainMultiplier, ObservationStart = live.observed.Count };
                live.submissions.Push(__state);
            }
            catch (Exception error) { Debug.LogError("[Aetherfront] Science submission preparation failed: " + error.GetType().Name); }
        }
        public static void AfterSubmission(ScienceSubject __2, float __result, Submission __state, Exception __exception)
        {
            if (live == null || __state == null) return;
            try
            {
                if (!ValidSubject(__2) || __2.id != __state.Source || __2.science <= __state.Before) return;
                float expected = __result > 0 ? __result : (__2.science - __state.Before) * __state.Multiplier;
                // Subtracting two cumulative floats loses low reward bits.
                // Exception correlation must allow their float ULP, not only
                // a relative tolerance based on the much smaller reward.
                float tolerance = Math.Max(.00001f, Math.Max(Math.Abs(expected) / 100000f,
                    Math.Max(Math.Abs(__2.science), Math.Abs(__state.Before)) * .00000024f * Math.Abs(__state.Multiplier)));
                bool matched = false;
                for (int i = live.observed.Count - 1; i >= __state.ObservationStart; i--)
                {
                    var award = live.observed[i];
                    if (!ReferenceEquals(award.Owner, __state) || award.Server != __state.Server || award.Account != __state.Account || (award.Reason != 0x20 && award.Reason != 0x400)) continue;
                    if (Math.Abs(award.Input - expected) <= tolerance)
                    { award.HasSubject = true; matched = true; break; }
                }
                if (__exception != null && !matched) return; // No observed wallet award: never invent one.
                if (__exception == null && !( __result > 0)) return;
                // Use the identity captured before KSP changed its wallet. A
                // disconnect or an earlier event handler exception cannot lose
                // already-earned evidence. Sending still requires that identity.
                Capture(__state.Server, __state.Account, __2);
            }
            catch (Exception error) { Debug.LogError("[Aetherfront] Science submission capture failed: " + error.GetType().Name); }
            finally
            {
                if (live.submissions.Count > 0 && ReferenceEquals(live.submissions.Peek(), __state)) live.submissions.Pop();
                else Debug.LogError("[Aetherfront] Science submission scope order changed.");
            }
            // A void finalizer deliberately leaves KSP's original exception intact.
        }
        private static void Capture(string server, string account, ScienceSubject subject)
        {
            // The direct float-to-decimal conversion retains only seven
            // significant digits. Widen first so small increments on larger
            // subject totals survive the six-decimal wire representation.
            decimal cumulative = decimal.Round((decimal)(double)subject.science, 6, MidpointRounding.ToEven);
            if (cumulative <= 0) return;
            ScienceOutbox.Enqueue(new PendingScienceClaim { ServerId = server, AccountId = account, Command = 40, SourceId = subject.id, Amount = cumulative });
            ScienceOutbox.Save(true);
        }
        private void Received(float amount, ScienceSubject subject, ProtoVessel vessel, bool reversed)
        {
            if (!Context() || !ValidSubject(subject)) return;
            string server = AetherfrontClientSystem.State.ServerId, account = MainSystem.UniqueIdentifier;
            var owner = submissions.Count > 0 ? submissions.Peek() : null;
            var paired = observed.LastOrDefault(a => !a.HasSubject && ReferenceEquals(a.Owner, owner) && a.Frame == Time.frameCount && a.Input == amount && a.Server == server && a.Account == account);
            if (paired != null) paired.HasSubject = true;
            Capture(server, account, subject);
        }
        private void FlushAwards(bool all)
        {
            for (int i = observed.Count - 1; i >= 0; i--)
            {
                var award = observed[i]; if (!all && award.Frame == Time.frameCount) continue;
                if (!award.HasSubject) ScienceOutbox.Enqueue(new PendingScienceClaim { ServerId = award.Server, AccountId = award.Account, Command = 42,
                    SourceId = "award:" + Guid.NewGuid().ToString("N"), Amount = award.Amount, Reason = award.Reason });
                observed.RemoveAt(i);
            }
        }
    }
}
