using LmpCommon.Message.Data.Kerbal;
using LmpCommon.Message.Server;
using LmpCommon;
using LunaConfigNode.CfgNode;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Properties;
using Server.Server;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Server.System
{
    public class KerbalSystem
    {
        public static readonly string KerbalsPath = Path.Combine(ServerContext.UniverseDirectory, "Kerbals");
        public static readonly string ApplicantQuarantinePath = Path.Combine(ServerContext.UniverseDirectory, "KerbalApplicants-Quarantine");
        public static readonly string VesselQuarantinePath = Path.Combine(ServerContext.UniverseDirectory, "Vessels-Quarantine");

        public static void GenerateDefaultKerbals()
        {
            FileHandler.CreateFile(Path.Combine(KerbalsPath, "Jebediah Kerman.txt"), Resources.Jebediah_Kerman);
            FileHandler.CreateFile(Path.Combine(KerbalsPath, "Bill Kerman.txt"), Resources.Bill_Kerman);
            FileHandler.CreateFile(Path.Combine(KerbalsPath, "Bob Kerman.txt"), Resources.Bob_Kerman);
            FileHandler.CreateFile(Path.Combine(KerbalsPath, "Valentina Kerman.txt"), Resources.Valentina_Kerman);
        }

        /// <summary>
        /// Repairs crew invariants before vessels are loaded or clients connect.
        /// A crew member may be assigned to only one persisted vessel, and every crew
        /// member referenced by a vessel must have state=Assigned. Duplicate vessel
        /// assignments otherwise make KSP's CrewRoster validation oscillate between
        /// clients, which can break hiring and flood the server with kerbal updates.
        /// </summary>
        public static void MigrateCrewRoster()
        {
            var result = MigrateCrewRoster(VesselStoreSystem.VesselsPath, KerbalsPath);
            if (result.DuplicateAssignmentsRemoved > 0 || result.KerbalStatesRepaired > 0 || result.StaleApplicantsQuarantined > 0 || result.MalformedEvaVesselsQuarantined > 0 || result.ActiveVesselsMovedByPersistentQuarantine > 0)
            {
                LunaLog.Warning($"Crew roster migration removed {result.DuplicateAssignmentsRemoved} duplicate vessel assignment(s), repaired {result.KerbalStatesRepaired} kerbal state(s), quarantined {result.StaleApplicantsQuarantined} stale applicant(s), quarantined {result.MalformedEvaVesselsQuarantined} malformed EVA vessel(s), and moved {result.ActiveVesselsMovedByPersistentQuarantine} operator-quarantined active vessel(s).");
            }
        }

        internal static RosterMigrationResult MigrateCrewRoster(string vesselsPath, string kerbalsPath)
        {
            var result = new RosterMigrationResult();
            if (!Directory.Exists(vesselsPath) || !Directory.Exists(kerbalsPath))
                return result;

            var applicantQuarantinePath = Path.Combine(Path.GetDirectoryName(kerbalsPath) ?? kerbalsPath, "KerbalApplicants-Quarantine");
            var vesselQuarantinePath = Path.Combine(Path.GetDirectoryName(vesselsPath) ?? vesselsPath, "Vessels-Quarantine");

            RegisterPersistentVesselQuarantine(vesselsPath, vesselQuarantinePath, result);

            var documents = new List<VesselCrewDocument>();
            var assignments = new Dictionary<string, List<VesselCrewAssignment>>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in Directory.GetFiles(vesselsPath, $"*{VesselStoreSystem.VesselFileFormat}"))
            {
                try
                {
                    var document = new VesselCrewDocument(path, new ConfigNode(File.ReadAllText(path)), File.GetLastWriteTimeUtc(path));
                    documents.Add(document);

                    foreach (var part in document.Node.GetNodes("PART").Select(value => value.Value))
                    {
                        foreach (var crewValue in part.GetValues("crew"))
                        {
                            var crewName = crewValue.Value?.Trim();
                            if (string.IsNullOrWhiteSpace(crewName))
                                continue;

                            if (!assignments.TryGetValue(crewName, out var crewAssignments))
                            {
                                crewAssignments = new List<VesselCrewAssignment>();
                                assignments[crewName] = crewAssignments;
                            }

                            crewAssignments.Add(new VesselCrewAssignment(document, part, crewValue));
                        }
                    }
                }
                catch (Exception e)
                {
                    LunaLog.Warning($"Skipping unreadable vessel during crew migration ({Path.GetFileName(path)}): {e.Message}");
                }
            }

            foreach (var assignment in assignments.Where(entry => entry.Value.Count > 1))
            {
                var ordered = assignment.Value
                    .OrderByDescending(item => item.Document.LastWriteUtc)
                    .ThenBy(item => item.Document.Path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                // Keep the newest authoritative vessel. Remove stale copies from all
                // older vessels (or duplicate seats in the same vessel).
                foreach (var stale in ordered.Skip(1))
                {
                    stale.Part.Values.Remove(stale.Value);
                    stale.Document.Dirty = true;
                    result.DuplicateAssignmentsRemoved++;
                    LunaLog.Warning($"Removed duplicate crew assignment for {assignment.Key} from vessel {Path.GetFileNameWithoutExtension(stale.Document.Path)}; kept {Path.GetFileNameWithoutExtension(ordered[0].Document.Path)}.");
                }
            }

            // An EVA vessel must contain exactly one resolvable crew slot. Stock KSP's
            // AstronautComplex.CreateAssignedList() indexes GetVesselCrew()[0] without a
            // count check. A duplicate-seat repair can therefore turn an older EVA copy
            // into a client-crashing, crewless vessel. Quarantine it recoverably instead.
            var malformedEvaDocuments = documents.Where(item => IsMalformedEvaVessel(item.Node)).ToArray();
            foreach (var document in malformedEvaDocuments)
            {
                Directory.CreateDirectory(vesselQuarantinePath);
                var quarantineFile = GetUniqueQuarantinePath(vesselQuarantinePath, Path.GetFileName(document.Path));
                File.Move(document.Path, quarantineFile);
                if (Guid.TryParse(Path.GetFileNameWithoutExtension(document.Path), out var vesselId))
                    VesselContext.RemovedVessels.TryAdd(vesselId, 0);
                result.MalformedEvaVesselsQuarantined++;
                LunaLog.Warning($"Quarantined malformed EVA vessel {Path.GetFileNameWithoutExtension(document.Path)}; stock Astronaut Complex cannot load an EVA without exactly one crew member.");
            }

            var validDocuments = documents.Except(malformedEvaDocuments).ToArray();
            foreach (var document in validDocuments.Where(item => item.Dirty))
                FileHandler.WriteToFile(document.Path, document.Node.ToString());

            // Recompute from the final, valid documents. The original assignment map still
            // contains values removed by duplicate repair and values from quarantined EVAs.
            var assignedCrew = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var document in validDocuments)
            {
                foreach (var crewName in document.Node.GetNodes("PART").Select(value => value.Value)
                    .SelectMany(part => part.GetValues("crew"))
                    .Select(value => value.Value?.Trim())
                    .Where(value => !string.IsNullOrWhiteSpace(value)))
                {
                    assignedCrew.Add(crewName);
                }
            }
            foreach (var path in Directory.GetFiles(kerbalsPath, "*.txt"))
            {
                try
                {
                    var kerbal = new ConfigNode(File.ReadAllText(path));
                    var name = kerbal.GetValue("name")?.Value?.Trim();
                    var type = kerbal.GetValue("type")?.Value;
                    var state = kerbal.GetValue("state")?.Value;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    // Stock KSP applicants are local, short-lived candidates. Persisting and
                    // replaying every Unowned candidate grows the shared roster forever and can
                    // leave Astronaut Complex list items that KerbalRoster.HireApplicant rejects.
                    // Keep only Unowned kerbals that are actually seated in a persisted vessel
                    // (for example a rescue-contract occupant); quarantine the rest so recovery
                    // remains possible from the universe backup or quarantine directory.
                    if (string.Equals(type, "Unowned", StringComparison.OrdinalIgnoreCase) && !assignedCrew.Contains(name))
                    {
                        Directory.CreateDirectory(applicantQuarantinePath);
                        var quarantineFile = Path.Combine(applicantQuarantinePath, Path.GetFileName(path));
                        if (!File.Exists(quarantineFile))
                            File.Move(path, quarantineFile);
                        else
                            File.Delete(path);
                        result.StaleApplicantsQuarantined++;
                        continue;
                    }

                    if (!string.Equals(type, "Crew", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string correctedState = null;
                    if (assignedCrew.Contains(name) && !string.Equals(state, "Assigned", StringComparison.OrdinalIgnoreCase))
                        correctedState = "Assigned";
                    else if (!assignedCrew.Contains(name) && string.Equals(state, "Assigned", StringComparison.OrdinalIgnoreCase))
                        correctedState = "Available";

                    if (correctedState == null)
                        continue;

                    kerbal.UpdateValue("state", correctedState);
                    FileHandler.WriteToFile(path, kerbal.ToString());
                    result.KerbalStatesRepaired++;
                }
                catch (Exception e)
                {
                    LunaLog.Warning($"Skipping unreadable kerbal during crew migration ({Path.GetFileName(path)}): {e.Message}");
                }
            }

            result.AssignedCrew = assignedCrew.Count;
            return result;
        }

        public static void HandleKerbalProto(ClientStructure client, KerbalProtoMsgData data)
        {
            var kerbalName = data?.Kerbal?.KerbalName;
            var kerbalData = data?.Kerbal?.KerbalData;
            var numBytes = data?.Kerbal?.NumBytes ?? 0;
            if (!TryValidateKerbalPayload(kerbalName, kerbalData, numBytes))
            {
                LunaLog.Warning($"Rejected malformed kerbal update from {client.PlayerName}");
                return;
            }

            var path = Path.Combine(KerbalsPath, $"{kerbalName}.txt");

            // Applicant candidates are generated locally by KSP. Do not turn them into
            // permanent shared roster entries. A hired applicant arrives as type=Crew and is
            // persisted normally. Preserve Unowned kerbals that are already referenced by a
            // vessel, since rescue/contract occupants may legitimately use that type.
            if (IsEphemeralApplicantRecord(kerbalData, numBytes) && !IsKerbalAssignedToPersistedVessel(kerbalName))
            {
                if (FileHandler.FileExists(path) && IsProtectedCrewRecord(FileHandler.ReadFileText(path)))
                {
                    LunaLog.Warning($"Ignored stale applicant downgrade of hired kerbal {kerbalName} from {client.PlayerName}");
                    SendStoredKerbal(client, kerbalName, path);
                }
                return;
            }

            if (ContentChecker.ContentsAreEqual(kerbalData, numBytes, path))
                return;

            LunaLog.Debug($"Saving kerbal {kerbalName} from {client.PlayerName}");
            FileHandler.WriteToFile(path, kerbalData, numBytes);

            MessageQueuer.RelayMessage<KerbalSrvMsg>(client, data);
        }

        public static void HandleKerbalsRequest(ClientStructure client)
        {
            var kerbalFiles = FileHandler.GetFilesInPath(KerbalsPath);
            var kerbalsData = kerbalFiles.Select(k =>
            {
                var kerbalData = FileHandler.ReadFile(k);
                return new KerbalInfo
                {
                    KerbalData = kerbalData,
                    NumBytes = kerbalData.Length,
                    KerbalName = Path.GetFileNameWithoutExtension(k)
                };
            }).Where(k => !IsEphemeralApplicantRecord(k.KerbalData, k.NumBytes)).ToArray();
            LunaLog.Debug($"Sending {client.PlayerName} {kerbalsData.Length} authoritative kerbals (filtered {kerbalFiles.Length - kerbalsData.Length} local applicant(s))...");

            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<KerbalReplyMsgData>();
            msgData.Kerbals = kerbalsData;
            msgData.KerbalsCount = msgData.Kerbals.Length;

            MessageQueuer.SendToClient<KerbalSrvMsg>(client, msgData);
        }

        public static void HandleKerbalRemove(ClientStructure client, KerbalRemoveMsgData message)
        {
            var kerbalToRemove = message.KerbalName;
            if (!IsValidKerbalName(kerbalToRemove))
            {
                LunaLog.Warning($"Rejected malformed kerbal removal from {client.PlayerName}");
                return;
            }

            var path = Path.Combine(KerbalsPath, $"{kerbalToRemove}.txt");
            if (!FileHandler.FileExists(path))
                return;

            // KerbalRemove is emitted by tourism cleanup. A stale cleanup can race a
            // hire: first the new Crew proto arrives, then the old removal. Preserve
            // authoritative hired crew and send it back to repair the sender's roster.
            if (IsProtectedCrewRecord(FileHandler.ReadFileText(path)))
            {
                LunaLog.Warning($"Ignored stale removal of hired kerbal {kerbalToRemove} from {client.PlayerName}");
                SendStoredKerbal(client, kerbalToRemove, path);
                return;
            }

            LunaLog.Debug($"Removing kerbal {kerbalToRemove} from {client.PlayerName}");
            FileHandler.FileDelete(path);

            MessageQueuer.RelayMessage<KerbalSrvMsg>(client, message);
        }

        internal static bool TryValidateKerbalPayload(string kerbalName, byte[] data, int numBytes)
        {
            if (!IsValidKerbalName(kerbalName) || data == null || numBytes <= 0 || numBytes > data.Length)
                return false;

            try
            {
                var node = new ConfigNode(Encoding.UTF8.GetString(data, 0, numBytes));
                var serializedName = node.GetValue("name")?.Value;
                return string.Equals(serializedName, kerbalName, StringComparison.Ordinal) && !node.IsEmpty();
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsProtectedCrewRecord(string text)
        {
            try
            {
                var node = new ConfigNode(text ?? string.Empty);
                return string.Equals(node.GetValue("type")?.Value, "Crew", StringComparison.OrdinalIgnoreCase) || node.IsEmpty();
            }
            catch
            {
                return true;
            }
        }

        internal static bool IsEphemeralApplicantRecord(byte[] data, int numBytes)
        {
            if (data == null || numBytes <= 0 || numBytes > data.Length)
                return false;

            try
            {
                var node = new ConfigNode(Encoding.UTF8.GetString(data, 0, numBytes));
                return string.Equals(node.GetValue("type")?.Value, "Unowned", StringComparison.OrdinalIgnoreCase) && !node.IsEmpty();
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsMalformedEvaVessel(string vesselText)
        {
            try
            {
                return IsMalformedEvaVessel(new ConfigNode(vesselText ?? string.Empty));
            }
            catch
            {
                return false;
            }
        }

        private static bool IsMalformedEvaVessel(ConfigNode vessel)
        {
            if (vessel == null || vessel.IsEmpty() || !string.Equals(vessel.GetValue("type")?.Value, "EVA", StringComparison.OrdinalIgnoreCase))
                return false;

            var crew = vessel.GetNodes("PART").Select(value => value.Value)
                .SelectMany(part => part.GetValues("crew"))
                .Select(value => value.Value?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            return crew.Length != 1;
        }

        private static string GetUniqueQuarantinePath(string quarantinePath, string fileName)
        {
            var candidate = Path.Combine(quarantinePath, fileName);
            if (!File.Exists(candidate))
                return candidate;

            return Path.Combine(quarantinePath,
                $"{Path.GetFileNameWithoutExtension(fileName)}-{DateTime.UtcNow:yyyyMMddHHmmssfff}{Path.GetExtension(fileName)}");
        }

        /// <summary>
        /// Files already present in Vessels-Quarantine are a persistent deny-list. This
        /// prevents a reconnecting client with an old local copy from resurrecting a vessel
        /// that was removed to repair the universe. If an active copy exists, preserve it as
        /// another uniquely named quarantine artifact before loading the vessel store.
        /// </summary>
        private static void RegisterPersistentVesselQuarantine(string vesselsPath, string quarantinePath, RosterMigrationResult result)
        {
            if (!Directory.Exists(quarantinePath))
                return;

            foreach (var quarantineFile in Directory.GetFiles(quarantinePath, $"*{VesselStoreSystem.VesselFileFormat}"))
            {
                if (!TryGetVesselIdFromQuarantineFile(quarantineFile, out var vesselId))
                    continue;

                VesselContext.RemovedVessels.TryAdd(vesselId, 0);
                result.PersistentVesselQuarantinesRegistered++;

                var activeFile = Path.Combine(vesselsPath, $"{vesselId}{VesselStoreSystem.VesselFileFormat}");
                if (!File.Exists(activeFile))
                    continue;

                var preservedActiveCopy = GetUniqueQuarantinePath(quarantinePath, Path.GetFileName(activeFile));
                File.Move(activeFile, preservedActiveCopy);
                result.ActiveVesselsMovedByPersistentQuarantine++;
                LunaLog.Warning($"Moved active vessel {vesselId} into persistent operator quarantine before universe load.");
            }
        }

        private static bool TryGetVesselIdFromQuarantineFile(string path, out Guid vesselId)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (Guid.TryParse(name, out vesselId))
                return true;

            return name.Length >= 36 && Guid.TryParse(name.Substring(0, 36), out vesselId);
        }

        internal static bool IsKerbalAssignedToPersistedVessel(string kerbalName)
        {
            if (string.IsNullOrWhiteSpace(kerbalName) || !Directory.Exists(VesselStoreSystem.VesselsPath))
                return false;

            foreach (var vesselPath in Directory.GetFiles(VesselStoreSystem.VesselsPath, $"*{VesselStoreSystem.VesselFileFormat}"))
            {
                try
                {
                    var vessel = new ConfigNode(File.ReadAllText(vesselPath));
                    if (vessel.GetNodes("PART").Select(value => value.Value)
                        .SelectMany(part => part.GetValues("crew"))
                        .Any(value => string.Equals(value.Value?.Trim(), kerbalName, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
                catch (Exception e)
                {
                    LunaLog.Warning($"Skipping unreadable vessel during applicant validation ({Path.GetFileName(vesselPath)}): {e.Message}");
                }
            }

            return false;
        }

        private static bool IsValidKerbalName(string kerbalName)
        {
            return !string.IsNullOrWhiteSpace(kerbalName)
                && kerbalName.Length <= 128
                && string.Equals(Path.GetFileName(kerbalName), kerbalName, StringComparison.Ordinal)
                && kerbalName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        private static void SendStoredKerbal(ClientStructure client, string kerbalName, string path)
        {
            var kerbalData = FileHandler.ReadFile(path);
            if (kerbalData.Length == 0) return;

            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<KerbalProtoMsgData>();
            msgData.Kerbal.KerbalName = kerbalName;
            msgData.Kerbal.KerbalData = kerbalData;
            msgData.Kerbal.NumBytes = kerbalData.Length;
            MessageQueuer.SendToClient<KerbalSrvMsg>(client, msgData);
        }

        internal sealed class RosterMigrationResult
        {
            public int DuplicateAssignmentsRemoved { get; set; }
            public int KerbalStatesRepaired { get; set; }
            public int AssignedCrew { get; set; }
            public int StaleApplicantsQuarantined { get; set; }
            public int MalformedEvaVesselsQuarantined { get; set; }
            public int PersistentVesselQuarantinesRegistered { get; set; }
            public int ActiveVesselsMovedByPersistentQuarantine { get; set; }
        }

        private sealed class VesselCrewDocument
        {
            public string Path { get; }
            public ConfigNode Node { get; }
            public DateTime LastWriteUtc { get; }
            public bool Dirty { get; set; }

            public VesselCrewDocument(string path, ConfigNode node, DateTime lastWriteUtc)
            {
                Path = path;
                Node = node;
                LastWriteUtc = lastWriteUtc;
            }
        }

        private sealed class VesselCrewAssignment
        {
            public VesselCrewDocument Document { get; }
            public ConfigNode Part { get; }
            public CfgNodeValue<string, string> Value { get; }

            public VesselCrewAssignment(VesselCrewDocument document, ConfigNode part, CfgNodeValue<string, string> value)
            {
                Document = document;
                Part = part;
                Value = value;
            }
        }
    }
}
