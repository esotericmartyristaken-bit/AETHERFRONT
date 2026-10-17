using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System;
using System;
using System.IO;
using System.Text;

namespace ServerTest
{
    [TestClass]
    public class KerbalSystemTest
    {
        [TestMethod]
        public void KerbalPayloadRequiresSafeMatchingName()
        {
            var bytes = Encoding.UTF8.GetBytes("name = Test Kerman\ntype = Crew\nstate = Available\n");

            Assert.IsTrue(KerbalSystem.TryValidateKerbalPayload("Test Kerman", bytes, bytes.Length));
            Assert.IsFalse(KerbalSystem.TryValidateKerbalPayload("Other Kerman", bytes, bytes.Length));
            Assert.IsFalse(KerbalSystem.TryValidateKerbalPayload("../Test Kerman", bytes, bytes.Length));
            Assert.IsFalse(KerbalSystem.TryValidateKerbalPayload("Test Kerman", bytes, bytes.Length + 1));
        }

        [TestMethod]
        public void HiredCrewRecordsAreProtectedFromContractCleanup()
        {
            Assert.IsTrue(KerbalSystem.IsProtectedCrewRecord("name = Test Kerman\ntype = Crew\nstate = Available\n"));
            Assert.IsFalse(KerbalSystem.IsProtectedCrewRecord("name = Tourist Kerman\ntype = Tourist\nstate = Assigned\n"));
            Assert.IsFalse(KerbalSystem.IsProtectedCrewRecord("name = Applicant Kerman\ntype = Unowned\nstate = Available\n"));
            Assert.IsTrue(KerbalSystem.IsProtectedCrewRecord("not valid config {"));
        }

        [TestMethod]
        public void ApplicantRecordsAreRecognizedWithoutMisclassifyingCrew()
        {
            var applicant = Encoding.UTF8.GetBytes("name = Applicant Kerman\ntype = Unowned\nstate = Assigned\n");
            var crew = Encoding.UTF8.GetBytes("name = Crew Kerman\ntype = Crew\nstate = Available\n");

            Assert.IsTrue(KerbalSystem.IsEphemeralApplicantRecord(applicant, applicant.Length));
            Assert.IsFalse(KerbalSystem.IsEphemeralApplicantRecord(crew, crew.Length));
            Assert.IsFalse(KerbalSystem.IsEphemeralApplicantRecord(null, 0));
        }

        [TestMethod]
        public void EvaVesselsRequireExactlyOneCrewMember()
        {
            Assert.IsTrue(KerbalSystem.IsMalformedEvaVessel("type = EVA\nPART\n{\n name = kerbalEVA\n}\n"));
            Assert.IsFalse(KerbalSystem.IsMalformedEvaVessel("type = EVA\nPART\n{\n name = kerbalEVA\n crew = Bob Kerman\n}\n"));
            Assert.IsFalse(KerbalSystem.IsMalformedEvaVessel("type = Ship\nPART\n{\n name = mk1pod\n}\n"));
        }

        [TestMethod]
        public void CrewRosterMigration_RemovesDuplicateVesselSeatAndRepairsStates()
        {
            var root = Path.Combine(Path.GetTempPath(), "apf-roster-" + Guid.NewGuid().ToString("N"));
            var vessels = Path.Combine(root, "Vessels");
            var kerbals = Path.Combine(root, "Kerbals");
            Directory.CreateDirectory(vessels);
            Directory.CreateDirectory(kerbals);
            VesselContext.RemovedVessels.Clear();

            try
            {
                var older = Path.Combine(vessels, "11111111-1111-1111-1111-111111111111.txt");
                var newer = Path.Combine(vessels, "22222222-2222-2222-2222-222222222222.txt");
                File.WriteAllText(older, EvaVesselWithCrew("Duplicate Kerman"));
                File.WriteAllText(newer, VesselWithCrew("Duplicate Kerman"));
                File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
                File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

                File.WriteAllText(Path.Combine(kerbals, "Duplicate Kerman.txt"), "name = Duplicate Kerman\ntype = Crew\nstate = Missing\ntrait = Pilot\n");
                File.WriteAllText(Path.Combine(kerbals, "Idle Kerman.txt"), "name = Idle Kerman\ntype = Crew\nstate = Assigned\ntrait = Engineer\n");
                File.WriteAllText(Path.Combine(kerbals, "Stale Applicant.txt"), "name = Stale Applicant\ntype = Unowned\nstate = Assigned\ntrait = Pilot\n");

                var result = KerbalSystem.MigrateCrewRoster(vessels, kerbals);

                Assert.AreEqual(1, result.DuplicateAssignmentsRemoved);
                Assert.AreEqual(2, result.KerbalStatesRepaired);
                Assert.AreEqual(1, result.AssignedCrew);
                Assert.AreEqual(1, result.StaleApplicantsQuarantined);
                Assert.AreEqual(1, result.MalformedEvaVesselsQuarantined);
                Assert.IsFalse(File.Exists(older));
                Assert.IsTrue(File.ReadAllText(newer).Contains("crew = Duplicate Kerman"));
                StringAssert.Contains(File.ReadAllText(Path.Combine(kerbals, "Duplicate Kerman.txt")), "state = Assigned");
                StringAssert.Contains(File.ReadAllText(Path.Combine(kerbals, "Idle Kerman.txt")), "state = Available");
                Assert.IsFalse(File.Exists(Path.Combine(kerbals, "Stale Applicant.txt")));
                Assert.IsTrue(File.Exists(Path.Combine(root, "KerbalApplicants-Quarantine", "Stale Applicant.txt")));
                Assert.IsTrue(File.Exists(Path.Combine(root, "Vessels-Quarantine", Path.GetFileName(older))));

                // A quarantine artifact remains a deny-list across later restarts. If a
                // stale client resurrects the same active id, the next migration moves it
                // back out before VesselStoreSystem loads.
                File.WriteAllText(older, EvaVesselWithCrew("Duplicate Kerman"));
                var secondPass = KerbalSystem.MigrateCrewRoster(vessels, kerbals);
                Assert.AreEqual(1, secondPass.ActiveVesselsMovedByPersistentQuarantine);
                Assert.IsFalse(File.Exists(older));
                Assert.IsTrue(VesselContext.RemovedVessels.ContainsKey(Guid.Parse(Path.GetFileNameWithoutExtension(older))));
            }
            finally
            {
                VesselContext.RemovedVessels.Clear();
                Directory.Delete(root, true);
            }
        }

        private static string VesselWithCrew(string name)
        {
            return $@"pid = {Guid.NewGuid():N}
PART
{{
    uid = 1
    crew = {name}
	}}";
        }

        private static string EvaVesselWithCrew(string name)
        {
            return $@"pid = {Guid.NewGuid():N}
type = EVA
PART
{{
    uid = 1
    crew = {name}
}}";
        }
    }
}
