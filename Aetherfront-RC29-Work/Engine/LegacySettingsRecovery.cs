using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ApfCore;

namespace KesasInstall
{
    // Compatibility bridge for a successful RC28 pack applied by an already-loaded
    // RC26/RC27 helper. This is crash recovery, not protection against wholesale
    // deletion/replay of authenticated local history. Never restore whole packages.
    public static class LegacySettingsRecovery
    {
        private const int MetadataLimit = 16 * 1024 * 1024;
        private const int SettingLimit = 4 * 1024 * 1024;
        private const string Version = "kesas-rc29";
        private const string StateRelative = "GameData/LunaMultiplayer/Data/APFPackState.json";
        // Tests may throw at a checkpoint to simulate interruption. No trust bypass.
        internal static Action<string> TestCheckpoint = null;

        private sealed class Envelope
        {
            public Envelope() { }
            public string Payload { get; set; }
            public string Mac { get; set; }
        }
        private sealed class Marker
        {
            public Marker() { }
            public int Schema { get; set; }
            public int Revision { get; set; }
            public string Root { get; set; }
            public string ReceiptHash { get; set; }
            public string TransactionAuthorization { get; set; }
            public string JournalAuthorization { get; set; }
            public string JournalPath { get; set; }
            public string Status { get; set; }
            public List<Item> Items { get; set; }
        }
        private sealed class Item
        {
            public Item() { }
            public string Relative { get; set; }
            public string SourceHash { get; set; }
            public string DefaultHash { get; set; }
            public string BeforeHash { get; set; }
            public string Status { get; set; }
        }
        private sealed class Journal
        {
            public string Path;
            public ApfRecoveryJournal Value;
            public DateTimeOffset Created;
        }
        private sealed class Source
        {
            public string Relative;
            public byte[] Bytes;
            public string Hash;
            public string DefaultHash;
        }

        // Call only after StartupCasing has authenticated the profile, signed catalog,
        // completed receipt and installed code. The receipt is reauthenticated here.
        public static void Apply(string root, ApfPackManifest manifest, Dictionary<string,string> receipt)
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            ApfPaths.EnsureNoReparsePoints(root, root);
            var authenticated = Installer.ReadCompletedReceipt(root);
            Require(authenticated != null && SameDictionary(authenticated, receipt), "Completed receipt changed or is not authenticated.");
            if (Value(receipt, "settingsPreservation", "prepared28")) return;
            Require(!receipt.ContainsKey("settingsPreservation"), "Unknown settings preservation protocol.");
            Require(manifest != null && manifest.PackVersion == Version && manifest.RequiredClientVersion == Version &&
                Value(receipt, "clientVersion", Version) && Value(receipt, "packVersion", Version) &&
                Value(receipt, "catalogVersion", Version) && Value(receipt, "packId", manifest.PackId) &&
                Value(receipt, "serverId", manifest.ServerId), "Legacy settings recovery requires the completed RC29 release.");
            Require(ApfManifestValidator.Validate(manifest).IsValid, "Invalid recovery manifest.");
            string apf = Under(root, "APF");
            string markerPath = Under(root, "APF/LegacySettingsRecovery.json");
            string keyPath = Under(root, "GameData/LunaMultiplayer/Data/APFHelper.key");
            byte[] key = ApfTransactionSecurity.LoadKey(keyPath);
            string receiptHash = HashText(ApfJson.Serialize(receipt.OrderBy(p => p.Key, StringComparer.Ordinal)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)));
            string[] paths = UserSettings.Paths;
            Require(paths.Length == 8 && paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 8,
                "Unexpected exact settings allowlist.");
            int selectedSettings = paths.Count(relative => manifest.ManagedPackages.Any(p => Contains(ApfPaths.PackageRoot(p), relative)));
            if (selectedSettings == 0) return; // Unrelated authenticated installs have nothing to recover.
            Require(selectedSettings == paths.Length, "Legacy recovery requires all eight settings packages when any is selected.");
            using (var leases = new Leases(root))
            {
                leases.Directory(apf);
                using (var installerLock = new FileStream(Under(root, "APF/.installer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                using (var recoveryLock = new FileStream(Under(root, "APF/.legacy-settings.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    Marker marker = ReadMarker(root, markerPath, key, receiptHash, paths);
                    if (marker != null && marker.Status == "complete") return;
                    var core = new CoreReader();
                    Journal journal = NewestJournal(root, receipt, core, leases);
                    ApfSyncTransaction transaction = FindTransaction(root, manifest, receipt, journal, key, core, leases);
                    Require(marker == null || (marker.TransactionAuthorization == transaction.Authorization &&
                        marker.JournalAuthorization == journal.Value.Authorization && SamePath(marker.JournalPath, journal.Path)),
                        "Recovery progress belongs to different transaction history.");

                    // Validate every original package tree before considering any setting write.
                    var sources = new List<Source>();
                    foreach (string relative in paths)
                    {
                        var staged = transaction.Packages.Single(p => Contains(ApfPaths.PackageRoot(p.Package), relative));
                        string packageTarget = Under(root, "GameData/" + ApfPaths.PackageRoot(staged.Package));
                        var record = journal.Value.Records.Single(r => SamePath(r.Target, packageTarget));
                        byte[] bytes = null;
                        if (record.Existed)
                        {
                            Require(!record.Restored && IsHash(record.ContentSha256) && Directory.Exists(record.Backup),
                                "An original settings package backup is missing.");
                            leases.Tree(record.Backup);
                            Require(SameHash(core.Hash(root, record.Backup), record.ContentSha256), "Original package backup content changed.");
                            string child = relative.Substring(ApfPaths.PackageRoot(staged.Package).Length + 1);
                            string sourcePath = Under(root, Path.Combine(record.Backup, child.Replace('/', Path.DirectorySeparatorChar)));
                            Require(!Directory.Exists(sourcePath), "Original settings path is a directory.");
                            if (File.Exists(sourcePath)) bytes = ReadBytes(root, sourcePath, SettingLimit);
                        }
                        else Require(AbsentRecord(record), "Absent original package has inconsistent recovery metadata.");
                        sources.Add(new Source { Relative = relative, Bytes = bytes, Hash = bytes == null ? "" : HashBytes(bytes),
                            DefaultHash = ArchiveDefault(root, staged, relative, leases) });
                    }
                    Hit("backups-verified");
                    if (marker == null)
                    {
                        marker = new Marker { Schema = 1, Root = root, ReceiptHash = receiptHash,
                            TransactionAuthorization = transaction.Authorization, JournalAuthorization = journal.Value.Authorization,
                            JournalPath = journal.Path, Status = "pending", Items = new List<Item>() };
                        foreach (var source in sources)
                            marker.Items.Add(new Item { Relative = source.Relative, SourceHash = source.Hash,
                                DefaultHash = source.DefaultHash, BeforeHash = DestinationHash(root, source.Relative), Status = "pending" });
                        SaveMarker(root, markerPath, marker, key);
                        Hit("plan-saved");
                    }
                    for (int index = 0; index < sources.Count; index++)
                    {
                        var source = sources[index];
                        var item = marker.Items[index];
                        Require(item.Relative == source.Relative && SameHash(item.SourceHash, source.Hash) &&
                            SameHash(item.DefaultHash, source.DefaultHash), "Recovery source differs from its durable plan.");
                        if (item.Status == "done") continue;
                        string current = DestinationHash(root, source.Relative);
                        // Missing originals stay absent. Non-default destinations are later
                        // user changes and are preserved, including after an interrupted run.
                        if (source.Bytes != null && !SameHash(current, source.Hash) &&
                            (current.Length == 0 || (source.DefaultHash.Length != 0 && SameHash(current, source.DefaultHash))))
                        {
                            WriteSetting(root, source, index, receiptHash, current, leases);
                            Hit("file-written:" + index.ToString(CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            string unfinished = SettingTemporary(root, source.Relative, index, receiptHash);
                            if (File.Exists(unfinished)) QuarantineTemporary(root, unfinished);
                        }
                        item.Status = "done";
                        SaveMarker(root, markerPath, marker, key);
                        Hit("file-recorded:" + index.ToString(CultureInfo.InvariantCulture));
                    }
                    marker.Status = "complete";
                    SaveMarker(root, markerPath, marker, key);
                    Hit("complete");
                }
            }
        }

        private static Journal NewestJournal(string root, Dictionary<string,string> receipt, CoreReader core, Leases leases)
        {
            string directory = Under(root, "APF/.transaction-backup");
            Require(Directory.Exists(directory), "Replacement recovery history is missing.");
            leases.Directory(directory);
            string[] directories = System.IO.Directory.GetDirectories(directory);
            Require(directories.Length <= 256, "Too many recovery transactions.");
            var journals = new List<Journal>();
            foreach (string child in directories)
            {
                leases.Directory(child);
                string path = Under(root, Path.Combine(child, "recovery.json"));
                Require(File.Exists(path), "An incomplete recovery transaction has no journal.");
                leases.File(path, MetadataLimit);
                var value = core.Read(root, path);
                Require(value != null && value.Records != null && value.Records.Count <= 10001,
                    "Invalid recovery journal records.");
                if (value.Kind != "pack") continue;
                DateTimeOffset created;
                Require(DateTimeOffset.TryParseExact(value.CreatedUtc, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out created), "Invalid authenticated journal time.");
                journals.Add(new Journal { Path = path, Value = value, Created = created });
            }
            Require(journals.Count != 0, "No pack replacement journal exists.");
            Journal newest = journals.OrderByDescending(j => j.Created).First();
            DateTimeOffset completed = default(DateTimeOffset);
            Require(receipt.ContainsKey("updatedUtc") && DateTimeOffset.TryParseExact(receipt["updatedUtc"], "o",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out completed), "Invalid completed receipt time.");
            Require(journals.Count(j => j.Created == newest.Created) == 1 && newest.Value.Status == "complete" &&
                newest.Created <= completed && newest.Value.Records.All(r => r != null && !r.Restored),
                "Newest pack journal is ambiguous, incomplete or newer than completion. No older backup was selected.");
            return newest;
        }

        private static ApfSyncTransaction FindTransaction(string root, ApfPackManifest manifest, Dictionary<string,string> receipt,
            Journal journal, byte[] key, CoreReader core, Leases leases)
        {
            string statePath = Under(root, StateRelative);
            var stateRecord = journal.Value.Records.SingleOrDefault(r => SamePath(r.Target, statePath));
            Require(stateRecord != null, "Original pack state recovery record is missing.");
            ApfPackState oldState;
            if (stateRecord.Existed)
            {
                Require(!stateRecord.Restored && IsHash(stateRecord.ContentSha256), "Original pack state backup is missing.");
                leases.File(stateRecord.Backup, MetadataLimit);
                Require(SameHash(core.Hash(root, stateRecord.Backup), stateRecord.ContentSha256), "Original pack state backup changed.");
                oldState = ApfJson.Deserialize<ApfPackState>(ReadText(root, stateRecord.Backup, MetadataLimit));
                Require(oldState != null && (oldState.PackVersion == "kesas-rc26" || oldState.PackVersion == "kesas-rc27"),
                    "Legacy recovery requires an RC26/RC27 original pack state.");
            }
            else
            {
                Require(AbsentRecord(stateRecord), "Absent original state has inconsistent recovery metadata.");
                oldState = new ApfPackState(); // Authenticated absence, not an invented installed version.
            }
            string oldIdentity = StateIdentity(oldState);
            var installed = ApfJson.Deserialize<ApfPackState>(ReadText(root, statePath, MetadataLimit));
            Require(installed != null && installed.PackId == manifest.PackId && installed.PackVersion == Version &&
                installed.Packages != null && installed.Packages.Count == manifest.ManagedPackages.Count &&
                manifest.ManagedPackages.All(p => installed.Packages.Count(s => s != null && s.Id == p.Id && s.Version == p.Version &&
                    SameHash(s.ArchiveSha256, p.Sha256) && ApfPaths.NormalizeGameDataRelative(s.Root) == ApfPaths.PackageRoot(p)) == 1),
                "Installed pack state does not match the completed release.");
            string connect = Under(root, "APF/.staging/connect");
            Require(Directory.Exists(connect), "Consumed pack transaction history is missing.");
            leases.Directory(connect);
            string[] directories = System.IO.Directory.GetDirectories(connect);
            Require(directories.Length <= 1024, "Too many consumed pack transactions.");
            var matches = new List<ApfSyncTransaction>();
            foreach (string child in directories)
            {
                leases.Directory(child);
                string path = Under(root, Path.Combine(child, "transaction.json.consumed"));
                if (!File.Exists(path)) continue;
                leases.File(path, MetadataLimit);
                var value = ApfJson.Deserialize<ApfSyncTransaction>(ReadText(root, path, MetadataLimit));
                Require(value != null && ApfTransactionSecurity.Verify(value, key) && SamePath(value.GameRoot, root),
                    "Consumed pack transaction authentication failed.");
                if (value.Manifest == null || value.Manifest.PackVersion != Version) continue;
                Require(value.Packages != null && value.Deletes != null && value.Packages.Count <= 512 &&
                    SamePath(value.GameDataPath, Under(root, "GameData")) && SamePath(value.StatePath, statePath),
                    "Invalid RC28 transaction roots or inventory.");
                if (!Value(receipt, "packManifestSha256", HashText(value.Manifest.ToJson()))) continue;
                if (!stateRecord.Existed)
                    Require(EmptyState(value.ExistingState), "Absent original pack state requires a genuinely empty authorized existing state.");
                if (StateIdentity(value.ExistingState) != oldIdentity) continue;
                Require(SameManifest(manifest, value.Manifest), "Consumed transaction differs from the signed catalog.");
                var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { statePath };
                foreach (var staged in value.Packages)
                {
                    Require(staged != null && staged.Package != null && value.Manifest.ManagedPackages.Count(p => SamePackage(p, staged.Package, true)) == 1,
                        "Staged package is not the authorized manifest package.");
                    Require(expected.Add(Under(root, "GameData/" + ApfPaths.PackageRoot(staged.Package))), "Duplicate replacement target.");
                }
                foreach (string relative in UserSettings.Paths)
                    Require(value.Packages.Count(p => Contains(ApfPaths.PackageRoot(p.Package), relative) &&
                        p.Package.Version.EndsWith("-settings28", StringComparison.Ordinal)) == 1, "All eight settings packages must be replaced.");
                Require(value.Deletes.All(d => d != null && !UserSettings.Paths.Any(p =>
                    Contains(ApfPaths.NormalizeGameDataRelative(d.RelativePath), p))), "Settings root also appears in permanent removals.");
                Require(journal.Value.Records.Count == expected.Count && journal.Value.Records.All(r => expected.Contains(Path.GetFullPath(r.Target))),
                    "Newest recovery journal does not match this transaction's exact replacement targets.");
                matches.Add(value);
            }
            Require(matches.Count != 0, "No consumed transaction matches the newest replacement journal and completed receipt.");
            // Retries may have different process identities/authorizations/cache paths
            // but the same authorized mutation. Settings come only from the unique
            // newest journal, never from a request or another attempt's backup.
            string intent = IntentIdentity(matches[0]);
            Require(matches.All(value => IntentIdentity(value) == intent), "Consumed transactions have conflicting authorized recovery intents.");
            var available = matches.Where(value => UserSettings.Paths.All(relative => File.Exists(value.Packages.Single(p =>
                Contains(ApfPaths.PackageRoot(p.Package), relative)).ArchivePath))).OrderBy(value => value.Authorization, StringComparer.Ordinal).ToList();
            Require(available.Count != 0, "Authenticated settings archives are missing for all equivalent recovery requests.");
            return available[0];
        }

        private static bool AbsentRecord(ApfRecoveryRecord record)
        {
            return record != null && !record.Existed && !record.Restored && record.Backup == string.Empty && record.ContentSha256 == string.Empty;
        }
        private static bool EmptyState(ApfPackState state)
        {
            return state != null && state.PackId == string.Empty && state.PackVersion == string.Empty && state.Packages != null && state.Packages.Count == 0;
        }
        private static string IntentIdentity(ApfSyncTransaction transaction)
        {
            return HashText(ApfJson.Serialize(new {
                GameRoot = Path.GetFullPath(transaction.GameRoot).ToLowerInvariant(),
                GameDataPath = Path.GetFullPath(transaction.GameDataPath).ToLowerInvariant(),
                StatePath = Path.GetFullPath(transaction.StatePath).ToLowerInvariant(),
                Manifest = transaction.Manifest.ToJson(), ExistingState = StateIdentity(transaction.ExistingState),
                Packages = transaction.Packages.Select(p => ApfJson.Serialize(p.Package)).OrderBy(p => p, StringComparer.Ordinal).ToList(),
                Deletes = transaction.Deletes.Select(d => {
                    Require(d != null && ApfPaths.IsSafeRelative(d.RelativePath), "Unsafe authorized deletion intent.");
                    return ApfPaths.NormalizeGameDataRelative(d.RelativePath);
                }).OrderBy(p => p, StringComparer.Ordinal).ToList()
            }));
        }

        private static string ArchiveDefault(string root, ApfStagedPackage staged, string relative, Leases leases)
        {
            string archive = Path.GetFullPath(staged.ArchivePath);
            Require(ApfPaths.IsUnder(archive, Under(root, "APF/.staging")), "Settings archive escapes staging.");
            var held = leases.File(archive, staged.Package.SizeBytes);
            Require(held.Length == staged.Package.SizeBytes && SameHash(HashStream(held), staged.Package.Sha256),
                "Settings archive differs from its authenticated package hash.");
            // The core validates container, entry types, bounds and mapped paths; it
            // does not extract or install anything here. The archive remains locked.
            ApfArchiveInstaller.ValidateArchive(archive, staged.Package, Under(root, "GameData"));
            held.Position = 0;
            string result = "";
            bool found = false;
            using (var zip = new ZipArchive(held, ZipArchiveMode.Read, true))
            {
                Require(zip.Entries.Count <= 10000, "Too many archive entries.");
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal)) continue;
                    string path = entry.FullName.Replace('\\', '/');
                    string prefix = ApfPaths.Normalize(staged.Package.StripPrefix);
                    Require(ApfPaths.IsSafeRelative(path) && (prefix.Length == 0 || path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)), "Invalid settings archive path.");
                    string destination = ApfPaths.NormalizeGameDataRelative(staged.Package.Destination);
                    string suffix = destination.Length == 0 ? path : path.Substring(prefix.Length + 1);
                    string mapped = destination.Length == 0 ? suffix : destination + "/" + suffix;
                    if (!mapped.Equals(relative, StringComparison.OrdinalIgnoreCase)) continue;
                    Require(!found && entry.Length <= SettingLimit, "Duplicate or oversized archive-default settings.");
                    using (var input = entry.Open()) result = HashBytes(ReadStream(input, SettingLimit, entry.Length));
                    found = true;
                }
            }
            return result; // Empty means the authenticated archive contains no default file.
        }

        private static string StateIdentity(ApfPackState state)
        {
            Require(state != null && state.Packages != null && state.Packages.Count <= 512 &&
                state.Packages.All(p => p != null && p.Files != null && p.Files.Count <= 100000) &&
                state.Packages.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == state.Packages.Count,
                "Invalid semantic pack state.");
            var normalized = new ApfPackState { PackId = state.PackId, PackVersion = state.PackVersion,
                Packages = state.Packages.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => new ApfPackageState {
                    Id = p.Id, Version = p.Version, ArchiveSha256 = (p.ArchiveSha256 ?? "").ToLowerInvariant(),
                    Root = ApfPaths.NormalizeGameDataRelative(p.Root), Files = p.Files.Select(f => {
                        Require(f != null && IsHash(f.Sha256) && ApfPaths.IsSafeRelative(f.Path), "Invalid original file state.");
                        return new ApfFileState { Path = ApfPaths.Normalize(f.Path), Sha256 = f.Sha256.ToLowerInvariant() };
                    }).OrderBy(f => f.Path, StringComparer.Ordinal).ToList() }).ToList() };
            foreach (var package in normalized.Packages)
                Require(package.Files.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count() == package.Files.Count, "Duplicate original file state.");
            return HashText(ApfJson.Serialize(normalized));
        }
        private static bool SameManifest(ApfPackManifest a, ApfPackManifest b)
        {
            if (a.PackId != b.PackId || a.ServerId != b.ServerId || a.PackVersion != b.PackVersion ||
                a.RequiredClientVersion != b.RequiredClientVersion || a.KspVersion != b.KspVersion ||
                a.ManagedPackages == null || b.ManagedPackages == null || a.ManagedPackages.Count != b.ManagedPackages.Count) return false;
            // URLs are rewritten to the selected server during normal preparation.
            var copy = ApfPackManifest.FromJson(b.ToJson());
            foreach (var package in copy.ManagedPackages)
            {
                var declared = a.ManagedPackages.SingleOrDefault(p => SamePackage(p, package, false));
                if (declared == null) return false;
                package.ArchiveUrl = declared.ArchiveUrl;
            }
            return copy.ToJson() == a.ToJson();
        }
        private static bool SamePackage(ApfPackage a, ApfPackage b, bool url)
        {
            return a != null && b != null && a.Id == b.Id && a.Version == b.Version && SameHash(a.Sha256, b.Sha256) &&
                a.SizeBytes == b.SizeBytes && a.Destination == b.Destination && a.StripPrefix == b.StripPrefix && (!url || a.ArchiveUrl == b.ArchiveUrl);
        }

        private static Marker ReadMarker(string root, string path, byte[] key, string receiptHash, string[] paths)
        {
            Marker current = File.Exists(path) ? DecodeMarker(root, path, key, receiptHash, paths) : null;
            string temporary = Under(root, path + ".new");
            if (File.Exists(temporary))
            {
                Envelope envelope = ReadEnvelope(root, temporary, key);
                if (envelope == null)
                {
                    // An uncommitted write may have been interrupted before its
                    // HMAC envelope was complete. It never authorizes restoration.
                    // Keep the committed marker, or rebuild the first plan from
                    // authenticated history when no setting could yet be written.
                    QuarantineTemporary(root, temporary);
                    return current;
                }
                Marker next = DecodeMarkerPayload(root, envelope.Payload, receiptHash, paths);
                Require(current == null ? next.Revision == 1 : next.Revision == current.Revision + 1 &&
                    next.TransactionAuthorization == current.TransactionAuthorization && next.JournalAuthorization == current.JournalAuthorization &&
                    SamePath(next.JournalPath, current.JournalPath), "Interrupted recovery marker has conflicting history.");
                if (current == null) File.Move(temporary, path); else AtomicFiles.Replace(temporary, path, null);
                current = next;
            }
            return current;
        }
        private static Marker DecodeMarker(string root, string path, byte[] key, string receiptHash, string[] paths)
        {
            var envelope = ReadEnvelope(root, path, key);
            Require(envelope != null, "Settings recovery marker authentication failed.");
            return DecodeMarkerPayload(root, envelope.Payload, receiptHash, paths);
        }
        private static Envelope ReadEnvelope(string root, string path, byte[] key)
        {
            Envelope envelope;
            try { envelope = ApfJson.Deserialize<Envelope>(ReadText(root, path, 65536)); }
            catch (Exception error) when (error is FormatException ||
                error.GetType().FullName == "JsonFx.Json.JsonDeserializationException" ||
                error.GetType().FullName == "JsonFx.Serialization.DeserializationException") { return null; }
            return envelope != null && envelope.Payload != null && SameMac(envelope.Mac, Mac(envelope.Payload, key)) ? envelope : null;
        }
        private static Marker DecodeMarkerPayload(string root, string payload, string receiptHash, string[] paths)
        {
            var marker = ApfJson.Deserialize<Marker>(payload);
            Require(marker != null && marker.Schema == 1 && marker.Revision > 0 && SamePath(marker.Root, root) &&
                marker.ReceiptHash == receiptHash && !string.IsNullOrEmpty(marker.TransactionAuthorization) &&
                !string.IsNullOrEmpty(marker.JournalAuthorization) && marker.JournalPath != null &&
                ApfPaths.IsUnder(marker.JournalPath, Under(root, "APF/.transaction-backup")) &&
                (marker.Status == "pending" || marker.Status == "complete") && marker.Items != null && marker.Items.Count == paths.Length,
                "Invalid or conflicting settings recovery marker.");
            for (int i = 0; i < paths.Length; i++)
            {
                var item = marker.Items[i];
                Require(item != null && item.Relative == paths[i] && OptionalHash(item.SourceHash) && OptionalHash(item.DefaultHash) &&
                    OptionalHash(item.BeforeHash) && (item.Status == "pending" || item.Status == "done") &&
                    (marker.Status != "complete" || item.Status == "done"), "Invalid settings recovery progress record.");
            }
            return marker;
        }
        private static void SaveMarker(string root, string path, Marker marker, byte[] key)
        {
            marker.Revision = checked(marker.Revision + 1);
            string payload = ApfJson.Serialize(marker);
            string temporary = Under(root, path + ".new");
            Require(!File.Exists(temporary) && !Directory.Exists(temporary), "Unexpected settings marker temporary path.");
            byte[] bytes = Encoding.UTF8.GetBytes(ApfJson.Serialize(new Envelope { Payload = payload, Mac = Mac(payload, key) }));
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { Hit("marker-temp-open"); output.Write(bytes, 0, bytes.Length); output.Flush(true); Hit("marker-temp-written"); }
            ApfPaths.EnsureNoReparsePoints(root, path);
            if (File.Exists(path)) AtomicFiles.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        private static void WriteSetting(string root, Source source, int index, string receiptHash, string before, Leases leases)
        {
            string destination = Under(root, "GameData/" + source.Relative);
            string parent = Path.GetDirectoryName(destination);
            ApfPaths.EnsureNoReparsePoints(root, parent);
            System.IO.Directory.CreateDirectory(parent);
            leases.Directory(parent);
            string temporary = SettingTemporary(root, source.Relative, index, receiptHash);
            if (File.Exists(temporary))
            {
                // Never install an incomplete temporary file. The source bytes
                // have already been authenticated against the complete backup.
                if (!SameHash(HashBytes(ReadBytes(root, temporary, SettingLimit)), source.Hash)) QuarantineTemporary(root, temporary);
            }
            if (!File.Exists(temporary))
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { Hit("setting-temp-open:" + index); output.Write(source.Bytes, 0, source.Bytes.Length); output.Flush(true); Hit("setting-temp-written:" + index); }
            }
            Require(SameHash(DestinationHash(root, source.Relative), before), "User settings changed during recovery; current content was retained.");
            ApfPaths.EnsureNoReparsePoints(root, destination);
            if (File.Exists(destination)) AtomicFiles.Replace(temporary, destination, null); else File.Move(temporary, destination);
            Require(SameHash(DestinationHash(root, source.Relative), source.Hash), "Restored settings verification failed.");
        }
        private static string SettingTemporary(string root, string relative, int index, string receiptHash)
        {
            string parent = Path.GetDirectoryName(Under(root, "GameData/" + relative));
            return Under(root, Path.Combine(parent, ".apf-legacy-settings-" + receiptHash.Substring(0, 16) + "-" + index + ".tmp"));
        }
        private static void QuarantineTemporary(string root, string path)
        {
            path = Under(root, path);
            Require((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0,
                "Interrupted recovery temporary path is not a plain file.");
            // Keep incomplete implementation files out of managed package trees.
            string quarantine = Under(root, "APF/LegacySettingsRecovery-incomplete-" + Guid.NewGuid().ToString("N") + ".bin");
            File.Move(path, quarantine);
        }
        private static string DestinationHash(string root, string relative)
        {
            string path = Under(root, "GameData/" + relative);
            Require(!Directory.Exists(path), "Destination settings path is a directory.");
            return File.Exists(path) ? HashBytes(ReadBytes(root, path, SettingLimit)) : "";
        }

        private sealed class CoreReader
        {
            private readonly MethodInfo read = Exact("Read", typeof(ApfRecoveryJournal));
            private readonly MethodInfo hash = Exact("HashContent", typeof(string));
            private static MethodInfo Exact(string name, Type returns)
            {
                var method = typeof(ApfRecoveryStore).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(string) }, null);
                Require(method != null && method.DeclaringType == typeof(ApfRecoveryStore) && method.IsPrivate &&
                    !method.IsGenericMethod && method.ReturnType == returns, "Signed Core recovery API signature changed.");
                return method;
            }
            private static object Invoke(MethodInfo method, string root, string path)
            {
                try { return method.Invoke(null, new object[] { root, path }); }
                catch (TargetInvocationException error) { throw error.InnerException ?? error; }
            }
            public ApfRecoveryJournal Read(string root, string path) { return (ApfRecoveryJournal)Invoke(read, root, path); }
            public string Hash(string root, string path) { return (string)Invoke(hash, root, path); }
        }

        // Hold non-delete-sharing directory and file handles through hash/copy. This
        // prevents a checked ancestor or backup file being replaced with a junction.
        private sealed class Leases : IDisposable
        {
            private readonly string root;
            private readonly Dictionary<string,SafeFileHandle> directories = new Dictionary<string,SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string,FileStream> files = new Dictionary<string,FileStream>(StringComparer.OrdinalIgnoreCase);
            private int entries;
            public Leases(string root) { this.root = root; Directory(root); }
            public void Directory(string path)
            {
                path = Under(root, path, true);
                if (directories.ContainsKey(path)) return;
                Require(directories.Count < 20000, "Too many secured recovery directories.");
                if (!SamePath(path, root)) Directory(Path.GetDirectoryName(path));
                // A child rename opens its parent for writing on Windows CLR.
                // Permit read/write sharing, but never delete sharing: ancestors
                // remain pinned against rename/replacement during atomic commits.
                // Backup/archive file handles still deny writes and deletion.
                var handle = CreateFile(path, 1, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Could not secure recovery directory: " + path); }
                FileIdentity identity;
                if (!GetFileInformationByHandle(handle, out identity) || (identity.Attributes & 0x10) == 0 || (identity.Attributes & 0x400) != 0)
                { handle.Dispose(); throw new InvalidDataException("Recovery directory is not a plain directory."); }
                directories.Add(path, handle);
            }
            public FileStream File(string path, long limit)
            {
                path = Under(root, path);
                FileStream existing;
                if (files.TryGetValue(path, out existing)) return existing;
                Require(files.Count < 20000 && limit >= 0, "Recovery file inventory exceeds its limit.");
                Directory(Path.GetDirectoryName(path));
                var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                FileIdentity identity;
                if (!GetFileInformationByHandle(input.SafeFileHandle, out identity) || (identity.Attributes & 0x410) != 0 || input.Length > limit)
                { input.Dispose(); throw new InvalidDataException("Recovery file is not plain or exceeds its size limit."); }
                files.Add(path, input);
                return input;
            }
            public void Tree(string path, int depth = 0)
            {
                Require(++entries <= 100000 && depth <= 64, "Recovery tree exceeds its traversal limit.");
                if (System.IO.File.Exists(path)) { File(path, 2L * 1024 * 1024 * 1024); return; }
                Directory(path);
                foreach (string child in System.IO.Directory.GetFileSystemEntries(path)) Tree(child, depth + 1);
            }
            public void Dispose()
            { foreach (var file in files.Values) file.Dispose(); foreach (var directory in directories.Values) directory.Dispose(); }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdentity { public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity information);
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);

        private static string Under(string root, string path, bool allowRoot = false)
        {
            string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            Require((allowRoot && SamePath(full, root)) || ApfPaths.IsUnder(full, root), "Recovery path escapes its game root.");
            ApfPaths.EnsureNoReparsePoints(root, full);
            return full;
        }
        private static byte[] ReadBytes(string root, string path, int maximum)
        {
            path = Under(root, path);
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            { Require(input.Length <= maximum, "Recovery input exceeds its size limit."); return ReadStream(input, maximum, input.Length); }
        }
        private static byte[] ReadStream(Stream input, int maximum, long expected)
        {
            Require(expected >= 0 && expected <= maximum, "Recovery input exceeds its size limit.");
            using (var output = new MemoryStream())
            {
                byte[] buffer = new byte[65536]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                { Require(output.Length + count <= maximum && output.Length + count <= expected, "Recovery input expanded beyond its limit."); output.Write(buffer, 0, count); }
                Require(output.Length == expected, "Recovery input is truncated.");
                return output.ToArray();
            }
        }
        private static string ReadText(string root, string path, int limit) { return Encoding.UTF8.GetString(ReadBytes(root, path, limit)); }
        private static string HashText(string value) { return HashBytes(Encoding.UTF8.GetBytes(value)); }
        private static string HashBytes(byte[] value) { using (var hash = SHA256.Create()) return ApfJson.ToHex(hash.ComputeHash(value)); }
        private static string HashStream(Stream input)
        { input.Position = 0; using (var hash = SHA256.Create()) return ApfJson.ToHex(hash.ComputeHash(input)); }
        private static string Mac(string payload, byte[] key)
        { using (var hash = new HMACSHA256(key)) return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(payload))); }
        private static bool SameMac(string a, string b)
        { if (a == null || b == null || a.Length != b.Length) return false; int difference = 0; for (int i = 0; i < a.Length; i++) difference |= a[i] ^ b[i]; return difference == 0; }
        private static bool IsHash(string value) { return value != null && value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')); }
        private static bool OptionalHash(string value) { return value != null && (value.Length == 0 || IsHash(value)); }
        private static bool SameHash(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        private static bool SamePath(string a, string b)
        { return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        private static bool Contains(string packageRoot, string relative)
        { return !string.IsNullOrEmpty(packageRoot) && relative.StartsWith(packageRoot + "/", StringComparison.OrdinalIgnoreCase); }
        private static bool Value(Dictionary<string,string> values, string key, string expected)
        { string actual; return values != null && values.TryGetValue(key, out actual) && actual == expected; }
        private static bool SameDictionary(Dictionary<string,string> a, Dictionary<string,string> b)
        { return b != null && a.Count == b.Count && a.All(p => Value(b, p.Key, p.Value)); }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
        private static void Hit(string value) { var callback = TestCheckpoint; if (callback != null) callback(value); }
    }
}
