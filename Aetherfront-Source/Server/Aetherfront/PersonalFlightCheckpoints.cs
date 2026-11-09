using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Server.Aetherfront
{
    // The native adapter captures the whole personal fleet under the fleet gate.
    // The universe directory separates science, sandbox and isolated test worlds.
    internal sealed class PersonalFlightCheckpoints
    {
        private const int MaximumFleetBytes = 32 * 1024 * 1024;
        private const int MaximumVesselBytes = 2 * 1024 * 1024;
        private const int MaximumRecordBytes = 6 * MaximumFleetBytes + 65536;
        private const long MaximumHistoryBytes = 128L * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private const string Header = "APF-FLIGHT-3\n";
        private static readonly object HistoryGate = new object();
        private readonly string root;

        internal PersonalFlightCheckpoints(string universeRoot)
        {
            root = Path.Combine(Path.GetFullPath(universeRoot), "FlightCheckpoints");
        }

        internal sealed class VesselSnapshot
        {
            public Guid Id { get; set; }
            public string Proto { get; set; }
        }

        internal sealed class Snapshot
        {
            public int Version { get; set; } = 2;
            public Guid Id { get; set; }
            public DateTimeOffset CapturedUtc { get; set; }
            public string Owner { get; set; }
            public double MissionTime { get; set; }
            public string RegistrationsJson { get; set; }
            public string ProgressionStamp { get; set; } = string.Empty;
            public string GameMode { get; set; } = string.Empty;
            public string PackStamp { get; set; } = string.Empty;
            public Guid LaunchVesselId { get; set; }
            public string FreezeScenario { get; set; } = string.Empty;
            public List<CrewSnapshot> Crew { get; set; } = new List<CrewSnapshot>();
            public List<VesselSnapshot> Vessels { get; set; } = new List<VesselSnapshot>();
        }

        internal sealed class CrewSnapshot
        {
            public string Name { get; set; }
            public string Config { get; set; }
        }

        internal sealed class RestoreTransaction
        {
            public Guid RequestId { get; set; }
            public long ExpectedGeneration { get; set; }
            public Snapshot Snapshot { get; set; }
            public List<Guid> DeleteVessels { get; set; } = new List<Guid>();
            public List<CrewSnapshot> ReleasedCrew { get; set; } = new List<CrewSnapshot>();
        }

        internal sealed class RestoreReceipt
        {
            public string Owner { get; set; }
            public Guid RequestId { get; set; }
            public Guid CheckpointId { get; set; }
            public long Generation { get; set; }
            public DateTimeOffset CompletedUtc { get; set; }
            public bool NeedsRehome { get; set; }
        }

        internal sealed class PointInfo
        {
            public Guid Id { get; set; }
            public string Label { get; set; }
            public DateTimeOffset CapturedUtc { get; set; }
            public double MissionTime { get; set; }
            public int VesselCount { get; set; }
        }

        private sealed class HistoryIndex
        {
            public string Owner { get; set; }
            public List<PointInfo> Points { get; set; } = new List<PointInfo>();
        }

        internal Snapshot Save(Snapshot snapshot, Guid expectedPrevious)
        {
            Validate(snapshot, snapshot?.Owner);
            var previous = ReadLatest(snapshot.Owner);
            if (previous?.Id == snapshot.Id) return previous;
            if ((previous?.Id ?? Guid.Empty) != expectedPrevious)
                throw new InvalidDataException("The checkpoint changed. Refresh before saving again.");
            WriteDocument(CheckpointPath(snapshot.Owner), JsonSerializer.Serialize(snapshot));
            return snapshot;
        }

        private void WriteDocument(string path, string json)
        {
            AssertOrdinaryPath(root);
            Directory.CreateDirectory(root);
            AssertOrdinaryPath(path);
            string staging = Path.Combine(root, Guid.NewGuid().ToString("N") + ".pending");
            try
            {
                byte[] payload = Utf8.GetBytes(json);
                if (payload.Length > MaximumRecordBytes) throw new InvalidDataException("Checkpoint exceeds its read limit.");
                byte[] header = Utf8.GetBytes(Header + Hash(payload) + "\n" + payload.Length.ToString("D10", CultureInfo.InvariantCulture) + "\n");
                using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                {
                    stream.Write(header, 0, header.Length);
                    using (var compressed = new BrotliStream(stream, CompressionLevel.Fastest, true))
                        compressed.Write(payload, 0, payload.Length);
                    stream.Flush(true);
                }
                AssertOrdinaryPath(path);
                File.Move(staging, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(staging)) File.Delete(staging);
            }
        }

        internal Snapshot Read(string owner, Guid expectedCheckpoint)
        {
            if (expectedCheckpoint == Guid.Empty) return null;
            var snapshot = ReadLatest(owner);
            if (snapshot?.Id == expectedCheckpoint) return snapshot;
            snapshot = ReadLaunch(owner);
            if (snapshot?.Id == expectedCheckpoint) return snapshot;
            lock (HistoryGate)
            {
                var index = ReadHistory(owner);
                if (index.Points.Any(point => point.Id == expectedCheckpoint))
                    return ReadSnapshot(owner, HistoryPath(owner, expectedCheckpoint));
            }
            snapshot = ReadSnapshot(owner, CheckpointPath(owner) + ".restore-source");
            return snapshot?.Id == expectedCheckpoint ? snapshot : null;
        }

        internal Snapshot ReadLatest(string owner)
        {
            return ReadSnapshot(owner, CheckpointPath(owner));
        }

        private Snapshot ReadSnapshot(string owner, string path)
        {
            string json = ReadDocument(path);
            if (json == null) return null;
            var snapshot = JsonSerializer.Deserialize<Snapshot>(json);
            Validate(snapshot, owner);
            return snapshot;
        }

        internal Snapshot ReadLaunch(string owner) => ReadSnapshot(owner, CheckpointPath(owner) + ".launch");

        internal void SaveTimed(Snapshot snapshot, bool launch)
        {
            Validate(snapshot, snapshot?.Owner);
            if (launch) WriteDocument(CheckpointPath(snapshot.Owner) + ".launch", JsonSerializer.Serialize(snapshot));
            string path = HistoryPath(snapshot.Owner, snapshot.Id);
            AssertOrdinaryPath(Path.GetDirectoryName(path));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            WriteDocument(path, JsonSerializer.Serialize(snapshot));
            lock (HistoryGate)
            {
                var index = ReadHistory(snapshot.Owner);
                index.Points.RemoveAll(point => point.Id == snapshot.Id);
                index.Points.Add(Info(snapshot, string.Empty));
                var bounded = new List<PointInfo>();
                long bytes = 0;
                foreach (var point in index.Points.OrderByDescending(point => point.CapturedUtc).Take(32))
                {
                    string retainedPath = HistoryPath(snapshot.Owner, point.Id);
                    AssertOrdinaryPath(retainedPath);
                    long size = new FileInfo(retainedPath).Length;
                    if (bounded.Count > 0 && bytes + size > MaximumHistoryBytes) break;
                    bounded.Add(point); bytes += size;
                }
                index.Points = bounded;
                WriteDocument(CheckpointPath(snapshot.Owner) + ".history-index", JsonSerializer.Serialize(index));
                var retained = new HashSet<string>(index.Points.Select(point => HistoryPath(snapshot.Owner, point.Id)), StringComparer.OrdinalIgnoreCase);
                foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(path), "*.checkpoint"))
                    if (!retained.Contains(file)) { AssertOrdinaryPath(file); File.Delete(file); }
            }
            CleanupTemporaryFiles();
            string restoreSource = CheckpointPath(snapshot.Owner) + ".restore-source";
            if (!File.Exists(CheckpointPath(snapshot.Owner) + ".restore") && !Receipt(snapshot.Owner).NeedsRehome &&
                File.Exists(restoreSource) && File.GetLastWriteTimeUtc(restoreSource) < DateTime.UtcNow.AddMinutes(-10))
            {
                AssertOrdinaryPath(restoreSource);
                try { File.Delete(restoreSource); } catch (IOException) { }
            }
        }

        internal List<PointInfo> Available(string owner, DateTimeOffset now, Snapshot manual)
        {
            var points = new List<PointInfo>();
            if (manual != null) points.Add(Info(manual, "Manual save"));
            lock (HistoryGate)
            {
                var history = ReadHistory(owner);
                for (int slot = 0; slot < 3; slot++)
                {
                    int minutes = slot == 0 ? 1 : slot == 1 ? 5 : 30;
                    var point = history.Points.OrderByDescending(item => item.CapturedUtc).FirstOrDefault(item => item.CapturedUtc <= now.AddMinutes(-minutes));
                    if (point != null) points.Add(new PointInfo { Id = point.Id, Label = minutes + (minutes == 1 ? " minute ago" : " minutes ago"),
                        CapturedUtc = point.CapturedUtc, MissionTime = point.MissionTime, VesselCount = point.VesselCount });
                }
            }
            var launch = ReadLaunch(owner);
            if (launch != null) points.Add(Info(launch, "Latest launch"));
            return points;
        }

        private static PointInfo Info(Snapshot snapshot, string label) => new PointInfo
        { Id = snapshot.Id, Label = label, CapturedUtc = snapshot.CapturedUtc, MissionTime = snapshot.MissionTime, VesselCount = snapshot.Vessels.Count };

        private string HistoryPath(string owner, Guid id) => Path.Combine(CheckpointPath(owner) + ".history", id.ToString("N") + ".checkpoint");

        private HistoryIndex ReadHistory(string owner)
        {
            string json = ReadDocument(CheckpointPath(owner) + ".history-index");
            if (json == null) return new HistoryIndex { Owner = owner };
            var index = JsonSerializer.Deserialize<HistoryIndex>(json);
            if (index == null || index.Owner != owner || index.Points == null || index.Points.Count > 32 ||
                index.Points.Any(point => point == null || point.Id == Guid.Empty || point.CapturedUtc == default ||
                    !double.IsFinite(point.MissionTime) || point.MissionTime < 0 || point.MissionTime > 1e12 || point.VesselCount < 0 || point.VesselCount > 512) ||
                index.Points.Select(point => point.Id).Distinct().Count() != index.Points.Count)
                throw new InvalidDataException("Invalid personal checkpoint history.");
            return index;
        }

        private void CleanupTemporaryFiles()
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.pending"))
            {
                AssertOrdinaryPath(file);
                if (File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddMinutes(-10)) continue;
                try { File.Delete(file); } catch (IOException) { }
            }
        }

        private static string ReadDocument(string path)
        {
            AssertOrdinaryPath(path);
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaximumRecordBytes) throw new InvalidDataException("Oversized checkpoint record.");
            byte[] header = new byte[Header.Length + 64 + 1 + 10 + 1];
            stream.ReadExactly(header, 0, header.Length);
            string text = Utf8.GetString(header);
            if (!text.StartsWith(Header, StringComparison.Ordinal) || text[Header.Length + 64] != '\n' || text[text.Length - 1] != '\n' ||
                !int.TryParse(text.Substring(Header.Length + 65, 10), NumberStyles.None, CultureInfo.InvariantCulture, out int length) ||
                length < 1 || length > MaximumRecordBytes)
                throw new InvalidDataException("Invalid checkpoint document header.");
            byte[] payload = new byte[length];
            using (var compressed = new BrotliStream(stream, CompressionMode.Decompress, true))
            {
                compressed.ReadExactly(payload, 0, payload.Length);
                if (compressed.ReadByte() != -1) throw new InvalidDataException("Checkpoint document exceeds its recorded size.");
            }
            if (!string.Equals(text.Substring(Header.Length, 64), Hash(payload), StringComparison.Ordinal))
                throw new InvalidDataException("Checkpoint checksum failed.");
            return Utf8.GetString(payload);
        }

        internal RestoreReceipt Receipt(string owner)
        {
            string json = ReadDocument(CheckpointPath(owner) + ".receipt");
            if (json == null) return new RestoreReceipt { Owner = owner };
            var receipt = JsonSerializer.Deserialize<RestoreReceipt>(json);
            if (receipt == null || receipt.Owner != owner || receipt.Generation < 1 || receipt.RequestId == Guid.Empty ||
                receipt.CheckpointId == Guid.Empty || receipt.CompletedUtc == default)
                throw new InvalidDataException("Invalid personal restore receipt.");
            return receipt;
        }

        internal void BeginRestore(RestoreTransaction transaction)
        {
            ValidateTransaction(transaction);
            var previous = Receipt(transaction.Snapshot.Owner);
            if (previous.Generation != transaction.ExpectedGeneration)
                throw new InvalidDataException("The restore generation changed. Refresh the checkpoint.");
            string path = CheckpointPath(transaction.Snapshot.Owner) + ".restore";
            if (File.Exists(path)) throw new InvalidDataException("A personal restore is already pending.");
            WriteDocument(CheckpointPath(transaction.Snapshot.Owner) + ".restore-source", JsonSerializer.Serialize(transaction.Snapshot));
            WriteDocument(path, JsonSerializer.Serialize(transaction));
        }

        internal RestoreTransaction Pending(string owner)
        {
            string json = ReadDocument(CheckpointPath(owner) + ".restore");
            if (json == null) return null;
            var transaction = JsonSerializer.Deserialize<RestoreTransaction>(json);
            ValidateTransaction(transaction);
            if (transaction.Snapshot.Owner != owner) throw new InvalidDataException("Restore account mismatch.");
            return transaction;
        }

        internal void FinishRestore(RestoreTransaction transaction)
        {
            ValidateTransaction(transaction);
            var previous = Receipt(transaction.Snapshot.Owner);
            long generation = checked(transaction.ExpectedGeneration + 1);
            if (previous.Generation != generation || previous.RequestId != transaction.RequestId)
            {
                if (previous.Generation != transaction.ExpectedGeneration)
                    throw new InvalidDataException("Restore receipt conflicts with pending transaction.");
                WriteDocument(CheckpointPath(transaction.Snapshot.Owner) + ".receipt", JsonSerializer.Serialize(new RestoreReceipt
                {
                    Owner = transaction.Snapshot.Owner, RequestId = transaction.RequestId,
                    CheckpointId = transaction.Snapshot.Id, Generation = generation, CompletedUtc = DateTimeOffset.UtcNow, NeedsRehome = true
                }));
            }
            string pending = CheckpointPath(transaction.Snapshot.Owner) + ".restore";
            AssertOrdinaryPath(pending);
            File.Delete(pending);
        }

        internal void MarkRehomed(RestoreReceipt receipt)
        {
            var current = Receipt(receipt.Owner);
            if (current.Generation != receipt.Generation || current.RequestId != receipt.RequestId)
                throw new InvalidDataException("The personal restore receipt changed.");
            current.NeedsRehome = false;
            WriteDocument(CheckpointPath(receipt.Owner) + ".receipt", JsonSerializer.Serialize(current));
            string source = CheckpointPath(receipt.Owner) + ".restore-source";
            AssertOrdinaryPath(source);
            try { File.Delete(source); } catch (IOException) { }
        }

        internal IEnumerable<string> PendingOwners()
        {
            AssertOrdinaryPath(root);
            if (!Directory.Exists(root)) yield break;
            foreach (string path in Directory.EnumerateFiles(root, "*.restore"))
            {
                var transaction = JsonSerializer.Deserialize<RestoreTransaction>(ReadDocument(path));
                ValidateTransaction(transaction);
                if (path != CheckpointPath(transaction.Snapshot.Owner) + ".restore")
                    throw new InvalidDataException("Personal restore filename mismatch.");
                yield return transaction.Snapshot.Owner;
            }
        }

        private static void ValidateTransaction(RestoreTransaction transaction)
        {
            if (transaction == null || transaction.RequestId == Guid.Empty || transaction.ExpectedGeneration < 0 ||
                transaction.ExpectedGeneration == long.MaxValue || transaction.DeleteVessels == null || transaction.DeleteVessels.Count > 1024 ||
                transaction.ReleasedCrew == null || transaction.ReleasedCrew.Count > 1024)
                throw new InvalidDataException("Invalid personal restore transaction.");
            Validate(transaction.Snapshot, transaction.Snapshot?.Owner);
            var ids = new HashSet<Guid>();
            foreach (var id in transaction.DeleteVessels)
                if (id == Guid.Empty || !ids.Add(id) || transaction.Snapshot.Vessels.Exists(vessel => vessel.Id == id))
                    throw new InvalidDataException("Invalid personal restore deletion.");
            foreach (var crew in transaction.ReleasedCrew) ValidateCrew(crew);
        }

        private static void Validate(Snapshot snapshot, string owner)
        {
            if (snapshot == null || snapshot.Version != 2 || snapshot.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(owner) || owner.Length > 128 ||
                !string.Equals(snapshot.Owner, owner, StringComparison.Ordinal) ||
                !double.IsFinite(snapshot.MissionTime) || snapshot.MissionTime < 0 || snapshot.MissionTime > 1e12 ||
                snapshot.CapturedUtc == default || snapshot.Vessels == null || snapshot.Vessels.Count > 512 ||
                snapshot.Crew == null || snapshot.Crew.Count > 1024 || snapshot.FreezeScenario == null ||
                Utf8.GetByteCount(snapshot.FreezeScenario) > 524288 || string.IsNullOrWhiteSpace(snapshot.RegistrationsJson))
                throw new InvalidDataException("Invalid personal fleet checkpoint.");
            long bytes = Utf8.GetByteCount(snapshot.RegistrationsJson) + Utf8.GetByteCount(snapshot.FreezeScenario);
            var ids = new HashSet<Guid>();
            foreach (var vessel in snapshot.Vessels)
            {
                if (vessel == null || vessel.Id == Guid.Empty || !ids.Add(vessel.Id) || string.IsNullOrWhiteSpace(vessel.Proto))
                    throw new InvalidDataException("Invalid checkpoint vessel.");
                int length = Utf8.GetByteCount(vessel.Proto);
                if (length > MaximumVesselBytes) throw new InvalidDataException("Checkpoint vessel exceeds its limit.");
                bytes += length;
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var crew in snapshot.Crew)
            {
                ValidateCrew(crew);
                if (!names.Add(crew.Name)) throw new InvalidDataException("Duplicate checkpoint crew member.");
                bytes += Utf8.GetByteCount(crew.Config);
            }
            if (bytes > MaximumFleetBytes) throw new InvalidDataException("Personal fleet exceeds its checkpoint limit.");
        }

        private static void ValidateCrew(CrewSnapshot crew)
        {
            if (crew == null || string.IsNullOrWhiteSpace(crew.Name) || crew.Name.Length > 128 ||
                crew.Name != Path.GetFileName(crew.Name) || crew.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                string.IsNullOrWhiteSpace(crew.Config) || Utf8.GetByteCount(crew.Config) > 65536)
                throw new InvalidDataException("Invalid checkpoint crew member.");
        }

        private string CheckpointPath(string owner)
        {
            if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128)
                throw new ArgumentException("An authenticated checkpoint owner is required.", nameof(owner));
            return Path.Combine(root, Hash(Utf8.GetBytes(owner)) + ".json");
        }

        private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

        private static void AssertOrdinaryPath(string path)
        {
            for (string current = path; current != null; current = Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Checkpoint path traverses a reparse point.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
    }
}
