using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ApfCore;
using UnityEngine;

// The signed client pins the two immutable licensed payloads. Ordinary mod
// packages retain their existing prohibition on writing Squad/SquadExpansion.
// Instantly runs before GameDatabase constructs its UrlDir and before the
// stock expansion loader verifies and loads the original expansion bundles.
[KSPAddon(KSPAddon.Startup.Instantly, true)]
public sealed class AetherfrontExpansionBootstrap : MonoBehaviour
{
    private string failure;
    private Rect window = new Rect(60, 60, 570, 155);
    private static readonly string[] Names = { "MakingHistory", "Serenity" };
    private static readonly HashSet<string> Executables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".ps1", ".bat", ".cmd", ".vbs", ".js", ".msi", ".com", ".scr" };

    public void Awake()
    {
        DontDestroyOnLoad(this);
        try
        {
            string root = Path.GetFullPath(KSPUtil.ApplicationRootPath);
            foreach (string name in Names) Install(root, name);
            Debug.Log("[Aetherfront DLC] Making History and Breaking Ground assets ready for stock expansion loading.");
            Destroy(this);
        }
        catch (Exception error)
        {
            failure = "The DLC installation could not finish. Existing DLC files were preserved. " +
                "See KSP.log for the installation reason. Do not join until this is resolved.";
            Debug.LogError("[Aetherfront DLC] " + error.GetType().Name + ": " + error.Message);
        }
    }

    private static string Safe(string root, string relative)
    {
        if (!ApfPaths.IsSafeRelative(relative)) throw new InvalidDataException("Unsafe expansion path.");
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!ApfPaths.IsUnder(path, root)) throw new InvalidDataException("Expansion path escaped its root.");
        ApfPaths.EnsureNoReparsePoints(root, path);
        return path;
    }

    private static string Hash(Stream stream)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    private static string FileHash(string path) { using (var stream = File.OpenRead(path)) return Hash(stream); }

    private static IEnumerable<string> Files(string root)
    {
        foreach (string path in Directory.GetFileSystemEntries(root))
        {
            ApfPaths.EnsureNoReparsePoints(root, path);
            if (Directory.Exists(path)) { foreach (string child in Files(path)) yield return child; }
            else yield return path;
        }
    }

    private static void Install(string game, string name)
    {
        if (!Names.Contains(name, StringComparer.Ordinal)) throw new InvalidDataException("Unknown expansion.");
        string payloadRoot = name == "MakingHistory" ? "AetherfrontMakingHistory" : "AetherfrontBreakingGround";
        string archive = Safe(game, "GameData/" + payloadRoot + "/PluginData/" + name + ".zip");
        string destination = Safe(game, "GameData/SquadExpansion/" + name);
        string receiptPath = Safe(game, "APF/ExpansionReceipts/" + name + ".cfg");
        string expected = name == "MakingHistory" ? ExpansionPayloadHashes.MakingHistory : ExpansionPayloadHashes.Serenity;
        if (!File.Exists(archive)) throw new FileNotFoundException("Signed expansion payload is missing: " + name);
        var archiveInfo = new FileInfo(archive);
        if (FastReceipt(game, destination, receiptPath, expected, archiveInfo)) return;
        if (archiveInfo.Length > 512L * 1024 * 1024 || FileHash(archive) != expected)
            throw new InvalidDataException("Expansion payload fingerprint does not match signed client: " + name);

        string stage = Safe(game, "APF/.expansion-staging/" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        bool created = false;
        bool existing = Directory.Exists(destination);
        try
        {
            var records = new List<Tuple<string, string, long>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Count == 0 || zip.Entries.Count > 2000) throw new InvalidDataException("Invalid expansion entry count.");
                foreach (var entry in zip.Entries)
                {
                    string prefix = name + "/";
                    if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) || entry.FullName.Contains("\\") ||
                        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0)
                        throw new InvalidDataException("Unsafe expansion entry.");
                    string relative = entry.FullName.Substring(prefix.Length);
                    if (entry.Name.Length == 0) throw new InvalidDataException("Unexpected directory entry.");
                    if (!seen.Add(relative) || Executables.Contains(Path.GetExtension(relative)))
                        throw new InvalidDataException("Duplicate or executable expansion entry.");
                    if (entry.Length < 0 || entry.Length > 1024L * 1024 * 1024 - expanded)
                        throw new InvalidDataException("Expansion exceeds its size bound.");
                    expanded += entry.Length;
                    string target = Safe(existing ? destination : stage, relative);
                    string hash;
                    if (existing)
                    {
                        if (!File.Exists(target) || new FileInfo(target).Length != entry.Length)
                            throw new InvalidDataException("Existing expansion differs from this release: " + name);
                        using (var input = entry.Open()) hash = Hash(input);
                        if (FileHash(target) != hash) throw new InvalidDataException("Existing expansion differs from this release: " + name);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var input = entry.Open()) using (var output = File.Create(target)) input.CopyTo(output);
                        if (new FileInfo(target).Length != entry.Length) throw new InvalidDataException("Incomplete expansion entry.");
                        hash = FileHash(target);
                    }
                    records.Add(Tuple.Create(relative, hash, entry.Length));
                }
            }
            string metadata = name == "MakingHistory" ? "makinghistory.kspexpansion" : "serenity.kspexpansion";
            if (!seen.Contains(metadata)) throw new InvalidDataException("Expansion metadata is absent.");
            if (existing)
            {
                // A pre-existing manual DLC is never overwritten or deleted.
                // Identical copies can adopt a fast receipt without another copy.
                if (Files(destination).Count() != records.Count) throw new InvalidDataException("Existing expansion differs from this release: " + name);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                // Whole DLC appears at its canonical path only after validation.
                Directory.Move(stage, destination);
                created = true;
            }
            var receipt = new ConfigNode();
            receipt.AddValue("archive", expected);
            receipt.AddValue("sourceBytes", archiveInfo.Length.ToString(CultureInfo.InvariantCulture));
            receipt.AddValue("sourceTicks", archiveInfo.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            foreach (var record in records)
            {
                var file = new FileInfo(Safe(destination, record.Item1));
                var node = receipt.AddNode("FILE");
                node.AddValue("path", Convert.ToBase64String(Encoding.UTF8.GetBytes(record.Item1)));
                node.AddValue("bytes", file.Length.ToString(CultureInfo.InvariantCulture));
                node.AddValue("ticks", file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(receiptPath));
            string temporary = receiptPath + ".new-" + Guid.NewGuid().ToString("N");
            receipt.Save(temporary);
            if (File.Exists(receiptPath)) File.Replace(temporary, receiptPath, null); else File.Move(temporary, receiptPath);
            Debug.Log("[Aetherfront DLC] " + name + (created ? " installed atomically." : " existing assets verified."));
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                ApfPaths.EnsureNoReparsePoints(game, stage);
                foreach (string item in Files(stage)) ApfPaths.EnsureNoReparsePoints(game, item);
                Directory.Delete(stage, true);
            }
        }
    }

    private static bool FastReceipt(string game, string destination, string path, string hash, FileInfo source)
    {
        if (!File.Exists(path) || !Directory.Exists(destination) || new FileInfo(path).Length > 1024 * 1024) return false;
        try
        {
            var receipt = ConfigNode.Load(path);
            if (receipt == null || receipt.GetValue("archive") != hash ||
                receipt.GetValue("sourceBytes") != source.Length.ToString(CultureInfo.InvariantCulture) ||
                receipt.GetValue("sourceTicks") != source.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)) return false;
            var nodes = receipt.GetNodes("FILE");
            if (nodes.Length == 0 || nodes.Length > 2000 || Files(destination).Count() != nodes.Length) return false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes)
            {
                string relative = Encoding.UTF8.GetString(Convert.FromBase64String(node.GetValue("path")));
                if (!seen.Add(relative)) return false;
                var file = new FileInfo(Safe(destination, relative));
                if (!file.Exists || node.GetValue("bytes") != file.Length.ToString(CultureInfo.InvariantCulture) ||
                    node.GetValue("ticks") != file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    public void OnGUI()
    {
        if (failure != null && HighLogic.LoadedScene == GameScenes.MAINMENU)
            window = GUILayout.Window(0x41504644, window, _ => GUILayout.Label(failure), "Aetherfront DLC installation");
    }
}
