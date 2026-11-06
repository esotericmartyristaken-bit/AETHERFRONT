using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Aetherfront.Core;
using ApfCore;
using UnityEngine;

namespace Aetherfront.Personal.Client
{
    internal static class RecoverySubjects
    {
        private static readonly FieldInfo Subjects = typeof(ResearchAndDevelopment).GetField("scienceSubjects", BindingFlags.Instance | BindingFlags.NonPublic);
        public sealed class Evidence
        {
            public int Version { get; set; } = 1;
            public string ServerId { get; set; }
            public string AccountId { get; set; }
            public string VesselId { get; set; }
            public string CreatedAtUtc { get; set; }
            public List<string> ScienceData { get; set; } = new List<string>();
            public List<string> SubjectDefinitions { get; set; } = new List<string>();
        }

        internal static void Prepare(ResearchAndDevelopment research, ProtoVessel vessel, string server, string account)
        {
            // Stock recovery checks this dictionary BEFORE SubmitScienceData.
            // A reconnect can leave carried ScienceData without an R&D subject;
            // an award-event-only hook can never observe that skipped experiment.
            try
            {
                var subjects = Subjects == null ? null : Subjects.GetValue(research) as Dictionary<string, ScienceSubject>;
                if (subjects == null) throw new InvalidDataException("R&D recovery subject dictionary unavailable");
                var evidence = new Evidence { ServerId = server, AccountId = account, VesselId = vessel.vesselID.ToString(), CreatedAtUtc = DateTime.UtcNow.ToString("o") };
                int restored = 0, missing = 0;
                foreach (var part in vessel.GetAllProtoPartsIncludingCargo())
                    foreach (var module in part.modules)
                        foreach (var data in module.moduleValues.GetNodes("ScienceData"))
                        {
                            if (evidence.ScienceData.Count >= 4096) throw new InvalidDataException("Recovery experiment count exceeds evidence limit");
                            evidence.ScienceData.Add(data.ToString());
                            string id = data.GetValue("subjectID");
                            ScienceSubject subject;
                            if (string.IsNullOrEmpty(id) || id.Length > 256 || id.Any(char.IsControl)) { missing++; continue; }
                            if (!subjects.TryGetValue(id, out subject))
                            {
                                subject = Reconstruct(id);
                                if (subject == null) { missing++; continue; }
                                subjects.Add(id, subject); restored++;
                            }
                            SubjectHistory.Restore(subject);
                            var definition = new ConfigNode("Science"); subject.Save(definition);
                            evidence.SubjectDefinitions.Add(definition.ToString());
                        }
                // Preserve raw carried data before another recovery callback can
                // throw or the vessel disappears. This is evidence, never an
                // automatic wallet award or an invented receipt.
                if (evidence.ScienceData.Count > 0)
                {
                    string root = Path.GetFullPath(KSPUtil.ApplicationRootPath);
                    string directory = Path.Combine(root, "APF", "RecoveryEvidence-v1");
                    string path = Path.Combine(directory, vessel.vesselID.ToString("N") + "-" + Guid.NewGuid().ToString("N") + ".json");
                    ApfPaths.EnsureNoReparsePoints(root, path);
                    Directory.CreateDirectory(directory);
                    string json = AetherfrontJson.Serialize(evidence);
                    if (json.Length > 4 * 1024 * 1024) throw new InvalidDataException("Recovery evidence exceeds local limit");
                    KesasInstall.Installer.AtomicText(path, json);
                }
                Debug.Log("[Aetherfront] Recovery preflight: experiments=" + evidence.ScienceData.Count + " restoredSubjects=" + restored + " unresolvedSubjects=" + missing + ".");
                if (missing > 0) Debug.LogWarning("[Aetherfront] Some recovery subjects could not be reconstructed safely. Raw science data was retained for diagnosis; no synthetic award was created.");
            }
            catch (Exception error) { Debug.LogError("[Aetherfront] Recovery preflight/evidence failed: " + error.GetType().Name + ". Stock recovery was not suppressed."); }
        }

        internal static ScienceSubject Reconstruct(string id)
        {
            int separator = id.IndexOf('@');
            if (separator <= 0 || FlightGlobals.Bodies == null) return null;
            var experiment = ResearchAndDevelopment.GetExperiment(id.Substring(0, separator));
            if (experiment == null) return null;
            string location = id.Substring(separator + 1);
            foreach (var body in FlightGlobals.Bodies.Where(b => b != null).OrderByDescending(b => b.name.Length))
                foreach (ExperimentSituations situation in Enum.GetValues(typeof(ExperimentSituations)))
                {
                    string prefix = body.name + situation;
                    if (!location.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    string biome = location.Substring(prefix.Length);
                    // An underscore can identify an asteroid/comet source UID.
                    // Its cap is different: never guess a body-scaled substitute.
                    if (biome.Contains("_")) return null;
                    var subject = new ScienceSubject(experiment, situation, body, biome, biome);
                    if (subject.id != id || subject.scienceCap <= 0 || subject.scienceCap > 1000000 || float.IsNaN(subject.scienceCap) ||
                        subject.dataScale <= 0 || float.IsNaN(subject.dataScale) || float.IsInfinity(subject.dataScale)) return null;
                    return subject;
                }
            return null;
        }
    }
}
