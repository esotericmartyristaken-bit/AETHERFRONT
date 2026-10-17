using System;
using System.IO;
using System.IO.Compression;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using B9PartSwitch;
using B9PartSwitch.Utils;
using ModuleManager;
using ModuleManager.Collections;
using ModuleManager.Extensions;
using ModuleManager.Logging;
using ModuleManager.Patches;
using ModuleManager.Patches.PassSpecifiers;
using ModuleManager.Progress;
using ModuleManager.Tags;
using KSP.Localization;

internal static class ModuleManagerProbe
{
    private static string root;
    private static string output;
    private static bool focused;
    private static int checks;
    private static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private static readonly Dictionary<string, ArchiveInput> archiveCache = new Dictionary<string, ArchiveInput>();
    private static readonly List<WarningCase> observed = new List<WarningCase>();
    private static readonly List<object> reports = new List<object>();

    public sealed class Catalog { public Profile full { get; set; } public Profile potato { get; set; } }
    public sealed class Profile { public string[] protectedRoots { get; set; } public Package[] managedPackages { get; set; } }
    public sealed class Package { public string id { get; set; } public string archiveUrl { get; set; } public string sha256 { get; set; } public string stripPrefix { get; set; } }
    private sealed class ArchiveInput
    {
        public string Path;
        public string Hash;
        public readonly SortedDictionary<string, string> Configs = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Assemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
    private sealed class Input
    {
        public readonly SortedDictionary<string, string> Files = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<object> Packages = new List<object>();
        public readonly List<UrlDir.UrlConfig> Configs = new List<UrlDir.UrlConfig>();
        public UrlDir GameData;
    }
    private sealed class WarningCase { public string Part; public string Module; public string Subtype; }
    private sealed class Selector
    {
        public string Part;
        public string Module;
        public string Subtype;
        public ConfigNode Modifier;
        public ConfigNode Identifier;
        public ConfigNode PartNode;
        public string Key { get { return Part + "/" + Module + "/" + Subtype; } }
    }

    public static int Main(string[] args)
    {
        root = Path.GetFullPath(args[0]);
        output = Path.GetFullPath(args[1]);
        focused = args.Length > 2 && args[2] == "focused";
        string allowed = Path.Combine(root, "Aetherfront-RC26-Work", "ModFixes", "Tests") + Path.DirectorySeparatorChar;
        if (!output.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || Directory.Exists(output))
            throw new ArgumentException("Output must be a NEW directory under RC26 ModFixes/Tests.");
        Directory.CreateDirectory(output);
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        try { return Run(); }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        if (name == "Mono.Cecil.dll")
            return Assembly.LoadFrom(Path.Combine(root, "tools/dotnet-sdk6/sdk/6.0.428/Sdks/Microsoft.NET.ILLink.Tasks/tools/net472/Mono.Cecil.dll"));
        if (name == "netstandard.dll")
            return Assembly.LoadFrom(Path.Combine(root, "tools/dotnet-sdk6/sdk/6.0.428/Microsoft/Microsoft.NET.Build.Extensions/net461/lib/netstandard.dll"));
        foreach (string directory in new[] {
            "Aetherfront-KSP-Smoke/KSP_x64_Data/Managed",
            "Aetherfront-KSP-Smoke/GameData",
            "Aetherfront-RC25-Work/P3e9e2e69/GameData/b9partswitch/Plugins",
            "Aetherfront-RC25-Work/P3e9e2e69/GameData/waterfall/Plugins"
        })
        {
            string candidate = Path.Combine(root, directory, name);
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
        }
        if (name == "ModuleManager.dll")
            return Assembly.LoadFrom(Path.Combine(root, "Aetherfront-KSP-Smoke/GameData/ModuleManager.4.2.3.dll"));
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run()
    {
        SmokeTest();
        string catalogPath = Path.Combine(root, "Aetherfront-RC25-Work/ModFixes/Staged-RC25/install-catalog.json");
        string policyPath = Path.Combine(root, "Aetherfront-RC26-Work/ModFixes/Source/ZZZ_KESASPotato/DisableExhaust.cfg");
        string catalogHash = HashFile(catalogPath), policyHash = HashFile(policyPath);
        Catalog catalog = json.Deserialize<Catalog>(File.ReadAllText(catalogPath));
        Regex warning = new Regex(@"Warning on PartSubtype (.*?) on module ModuleB9PartSwitch \(moduleID='(.*?)'\) on part ([^:]+): Could not find matching module");
        foreach (string line in File.ReadLines(Path.Combine(root, "Aetherfront-RC26-Work/Private-KSP-before-RC26.log")))
        {
            Match m = warning.Match(line);
            if (m.Success) observed.Add(new WarningCase { Subtype = m.Groups[1].Value, Module = m.Groups[2].Value, Part = m.Groups[3].Value });
        }
        Check(observed.Count == 112, "The private log must contain all 112 B9 missing-module warning cases.");
        RunProfile("full", catalog.full, false, policyPath);
        RunProfile("potato", catalog.potato, false, policyPath);
        RunProfile("legacy-potato", catalog.potato, true, policyPath);
        Check(HashFile(catalogPath) == catalogHash && HashFile(policyPath) == policyHash, "Read-only RC25 inputs unchanged.");
        WriteJson(Path.Combine(output, "Summary.json"), new {
            checks, catalogPath, catalogHash, policyPath, policyHash,
            moduleManager = new { path = typeof(MMPatchLoader).Assembly.Location, sha256 = HashFile(typeof(MMPatchLoader).Assembly.Location), version = typeof(MMPatchLoader).Assembly.GetName().Version.ToString() },
            b9 = new { path = typeof(ModuleMatcher).Assembly.Location, sha256 = HashFile(typeof(ModuleMatcher).Assembly.Location), version = typeof(ModuleMatcher).Assembly.GetName().Version.ToString() },
            configNode = new { path = typeof(ConfigNode).Assembly.Location, sha256 = HashFile(typeof(ConfigNode).Assembly.Location) },
            focused, reports,
            limits = new[] { "Headless configuration validation only; no Unity Part prefab construction or flight fuel-switch lifecycle.", "Uses real KSP ConfigNode and Localizer; real ModuleManager extraction, needs checking, pass sorting and PatchApplier.", "Assembly mod presence comes from catalog DLL metadata and directories; no plugin startup methods executed. Actual log reports no additional mods from assembly callbacks.", "B9 name-only matching invokes the actual private ModuleMatcher.NodeMatchesModule; additional identifier fields are checked against postpatch config values, not runtime BaseFields." }
        });
        Console.WriteLine("PASS: " + checks + " checks; results: " + output);
        return 0;
    }

    private static void SmokeTest()
    {
        UrlDir directory = NewDirectory(null, "");
        var file = (UrlDir.UrlFile)FormatterServices.GetUninitializedObject(typeof(UrlDir.UrlFile));
        Set(file, "_name", "smoke"); Set(file, "_fileExtension", "cfg"); Set(file, "_parent", directory);
        ConfigNode part = ConfigNode.Load(Path.Combine(root, "Aetherfront-RC25-Work/P3e9e2e69/GameData/farfuturetechnologies/Parts/Control/RCS/fft-rcs-arcjet-1.cfg")).GetNode("PART");
        ConfigNode policy = ConfigNode.Load(Path.Combine(root, "Aetherfront-RC26-Work/ModFixes/Source/ZZZ_KESASPotato/DisableExhaust.cfg"));
        using (var logger = new FileLogger(Path.Combine(output, "Smoke-ModuleManager.log")))
        {
            var progress = new PatchProgress(logger);
            int applied = 0;
            progress.OnPassStarted.Add(pass => logger.Pass(pass.Name));
            progress.OnPatchApplied.Add(() => { if (++applied % 1000 == 0) Console.WriteLine("smoke: real MM applied " + applied + " patches"); });
            var extractor = new PatchExtractor(progress, logger, new NeedsChecker(new string[0], directory, progress, logger), new TagListParser(progress), new ProtoPatchBuilder(progress), new PatchCompiler());
            ConfigNode[] nodes = new[] { part }.Concat(policy.GetNodes()).ToArray();
            IPatch[] patches = nodes.Select(n => extractor.ExtractPatch(new UrlDir.UrlConfig(file, n))).Where(p => p != null).ToArray();
            IProtoUrlConfig[] final = new PatchApplier(progress, logger).ApplyPatches(new PatchList(new string[0], patches, progress)).ToArray();
            ConfigNode result = final.Single(n => n.Node.name == "PART").Node;
            Selector[] selectors = GetSelectors(new[] { result }).ToArray();
            Check(selectors.Length == 2 && selectors.All(s => !IsWaterfall(s) && MatchCount(s) == 1), "Real ModuleManager extraction/pass/application and B9 smoke test.");
            Check((int)progress.Counter.exceptions == 0 && (int)progress.Counter.errors == 0, "Smoke engine has no diagnostics.");
            File.WriteAllText(Path.Combine(output, "Smoke-Final.cfg"), result.ToString());
        }
        Console.WriteLine("PASS: real ModuleManager + B9 smoke test");
    }

    private static void RunProfile(string name, Profile profile, bool legacy, string policyPath)
    {
        string directory = Path.Combine(output, name);
        Directory.CreateDirectory(directory);
        Console.WriteLine("Loading " + name + ": " + profile.managedPackages.Length + " packages");
        Input input = LoadInput(profile, legacy, policyPath);
        WriteJson(Path.Combine(directory, "Inputs.json"), new { packages = input.Packages, configFiles = input.Files.Count, roots = input.Mods.OrderBy(s => s).ToArray() });
        using (var logger = new FileLogger(Path.Combine(directory, "ModuleManager.log")))
        {
            var progress = new PatchProgress(logger);
            int applied = 0;
            progress.OnPassStarted.Add(pass => logger.Pass(pass.Name));
            progress.OnPatchApplied.Add(() => { if (++applied % 1000 == 0) Console.WriteLine(name + ": real MM applied " + applied + " patches"); });
            var needs = new NeedsChecker(input.Mods, input.GameData, progress, logger);
            var extractor = new PatchExtractor(progress, logger, needs, new TagListParser(progress), new ProtoPatchBuilder(progress), new PatchCompiler());
            IPatch[] patches = input.Configs.Select(c => extractor.ExtractPatch(c)).Where(p => p != null).ToArray();
            var ordered = new PatchList(input.Mods.OrderBy(s => s).ToArray(), patches, progress);
            var observer = new PolicyObserver(legacy, directory);
            Console.WriteLine("Patching " + name + ": " + patches.Length + " nodes");
            IProtoUrlConfig[] final = new PatchApplier(progress, logger).ApplyPatches(ordered.Select(p => (IPass)new ObservedPass(p, observer))).ToArray();
            ConfigNode[] parts = final.Where(c => c.Node.name == "PART").Select(c => c.Node).ToArray();
            Selector[] selectors = GetSelectors(parts).ToArray();
            Selector[] water = selectors.Where(IsWaterfall).ToArray();
            int missingWater = water.Count(s => MatchCount(s) == 0);
            int invalidPhysics = 0;
            var lines = new List<string> { "part\tmoduleID\tsubtype\tidentifier\tmatches\tmatcher" };
            foreach (Selector selector in selectors)
            {
                int count = MatchCount(selector);
                string id = string.Join(";", selector.Identifier.values.Cast<ConfigNode.Value>().Select(v => v.name + "=" + v.value));
                lines.Add(selector.Part + "\t" + selector.Module + "\t" + selector.Subtype + "\t" + id + "\t" + count + "\t" + (selector.Identifier.values.Count == 1 ? "real B9 ModuleMatcher" : "real B9 name matcher + config field equality"));
                if (!IsWaterfall(selector) && count == 0) invalidPhysics++;
            }
            File.WriteAllLines(Path.Combine(directory, "Selectors.tsv"), lines);
            File.WriteAllText(Path.Combine(directory, "FinalParts.cfg"), string.Join("\n", parts.Select(p => p.ToString())));
            var affected = new HashSet<string>(observed.Select(o => o.Part), StringComparer.Ordinal);
            File.WriteAllText(Path.Combine(directory, "AffectedAndAntimatter.cfg"), string.Join("\n", parts.Where(p => affected.Contains(KspName(p)) || (p.GetValue("name") ?? "").IndexOf("antimatter", StringComparison.OrdinalIgnoreCase) >= 0).Select(p => p.ToString())));
            int covered = 0;
            foreach (WarningCase warning in observed)
            {
                Selector[] selected = selectors.Where(s => s.Part == warning.Part && s.Module == warning.Module && s.Subtype == warning.Subtype).ToArray();
                bool subtypeExists = parts.Any(p => KspName(p) == warning.Part && p.GetNodes("MODULE").Any(m => m.GetValue("name") == "ModuleB9PartSwitch" && m.GetValue("moduleID") == warning.Module && m.GetNodes("SUBTYPE").Any(t => t.GetValue("name") == warning.Subtype)));
                Check(subtypeExists, name + " retained switch/subtype " + warning.Part + "/" + warning.Module + "/" + warning.Subtype);
                if (legacy)
                    Check(selected.Any(s => MatchCount(s) == 0), "Legacy negative control reproduces " + warning.Part + "/" + warning.Subtype);
                else if (name == "potato")
                {
                    Check(!selected.Any(IsWaterfall), "RC26 removes dangling renderer selector " + warning.Part + "/" + warning.Subtype);
                    Check(selected.All(s => MatchCount(s) > 0), "RC26 preserves matching physics selectors " + warning.Part + "/" + warning.Subtype);
                }
                else
                    Check(selected.Length > 0 && selected.All(s => MatchCount(s) > 0), "Full retains working selectors " + warning.Part + "/" + warning.Subtype);
                covered++;
            }
            Check(parts.Any(p => p.GetValue("name") == "fft-antimatter-beam-1"), name + " includes the antimatter beam engine.");
            Check(!selectors.Any(s => s.Part.IndexOf("antimatter", StringComparison.OrdinalIgnoreCase) >= 0 && !IsWaterfall(s) && MatchCount(s) == 0), name + " antimatter physics selectors retain targets.");
            Check(legacy ? logger.PolicyErrors == 2 : logger.PolicyErrors == 0, name + " policy insert-node diagnostics.");
            Check((int)progress.Counter.exceptions == 0, name + " no ModuleManager engine exceptions.");
            Check((int)progress.Counter.errors == (legacy ? 2 : 0), name + " exact ModuleManager error count.");
            if (name == "potato")
            {
                Check(observer.Applications == 1, "Exactly one real Potato cleanup application.");
                Check(water.Length == 0 && parts.All(p => !p.GetNodes("MODULE").Any(m => m.GetValue("name") == "ModuleWaterfallFX")), "Potato has no Waterfall selector or module after every pass.");
                Check(final.Count(c => c.Node.name == "KESAS_POTATO_COMPAT") == 1, "One clean profile marker remains.");
            }
            if (name == "full") Check(observer.Applications == 0 && water.Length > 0 && water.All(s => MatchCount(s) == 1), "Full retains uniquely matching renderer selectors without Potato cleanup.");
            if (legacy) Check(missingWater == 28 && invalidPhysics == 84, "Legacy negative control reproduces 28 Waterfall and 84 RCS identifier mismatches.");
            if (!legacy) Check(invalidPhysics == 0, name + " retains all matching physics selectors.");
            var report = new { profile = name, packageCount = profile.managedPackages.Length, configFiles = input.Files.Count, inputNodes = input.Configs.Count, compiledNodes = patches.Length, parts = parts.Length, selectors = selectors.Length, waterfallSelectors = water.Length, missingWaterfallSelectors = missingWater, unmatchedOtherSelectors = invalidPhysics, observedWarningsCovered = covered, policyApplications = observer.Applications, preservedParts = observer.PreservedParts, preservedModifiers = observer.PreservedModifiers, errors = (int)progress.Counter.errors, exceptions = (int)progress.Counter.exceptions, warnings = (int)progress.Counter.warnings, policyErrors = logger.PolicyErrors };
            WriteJson(Path.Combine(directory, "Report.json"), report);
            reports.Add(report);
            Console.WriteLine(json.Serialize(report));
        }
    }

    private static Input LoadInput(Profile profile, bool legacy, string policyPath)
    {
        var input = new Input();
        int loadedPackages = 0;
        foreach (Package package in profile.managedPackages)
        {
            string file = Path.GetFileName(new Uri(package.archiveUrl).AbsolutePath);
            string path = Path.Combine(root, "KESAS-VPS-Server/Server/Config", file);
            if (!File.Exists(path)) path = Path.Combine(root, "Aetherfront-RC25-Work/ModFixes/Staged-RC25/Media", file);
            ArchiveInput archive = ReadArchive(path, package.sha256);
            input.Packages.Add(new { package.id, package.stripPrefix, path = archive.Path, sha256 = archive.Hash, configs = archive.Configs.Count });
            foreach (var entry in archive.Configs)
            {
                Check(!input.Files.ContainsKey(entry.Key), "No colliding catalog config path: " + entry.Key);
                input.Files.Add(entry.Key, entry.Value);
            }
            input.Mods.UnionWith(archive.Directories);
            input.Mods.UnionWith(archive.Assemblies);
            if (++loadedPackages % 25 == 0) Console.WriteLine("Loaded " + loadedPackages + "/" + profile.managedPackages.Length + " packages");
        }
        string policyKey = input.Files.Keys.FirstOrDefault(k => k.Equals("ZZZ_KESASPotato/DisableExhaust.cfg", StringComparison.OrdinalIgnoreCase));
        if (policyKey != null)
        {
            Check(input.Files[policyKey] == File.ReadAllText(Path.Combine(root, "Aetherfront-RC25-Work/ModFixes/Source/ZZZ_KESASPotato/DisableExhaust.cfg")), "Baseline catalog policy bytes match reviewed RC25 source.");
            if (legacy)
            {
                string old = Path.Combine(root, "KESAS-VPS-Server/Server/Config/kesas-potato-policy-95941eedff917252.zip");
                input.Files[policyKey] = ReadArchive(old, "95941eedff917252f10554c727ee3b1002364c5dcdd2284a4ac30fa60c9a9dc4").Configs["ZZZ_KESASPotato/DisableExhaust.cfg"];
            }
            else input.Files[policyKey] = File.ReadAllText(policyPath);
        }
        foreach (string protectedRoot in profile.protectedRoots)
        {
            string path = Path.Combine(root, "Aetherfront-KSP-Smoke/GameData", protectedRoot);
            if (protectedRoot == "000_Harmony") path = Path.Combine(root, "Aetherfront-RC25-Work/ModFixes/Staged-RC25/ClientOverlay/GameData/000_Harmony");
            if (protectedRoot == "LunaMultiplayer") path = Path.Combine(root, "Aetherfront-RC25-Work/P3e9e2e69/GameData/LunaMultiplayer");
            if (!Directory.Exists(path)) continue;
            input.Mods.Add(protectedRoot);
            foreach (string file in Directory.EnumerateFiles(path, "*.cfg", SearchOption.AllDirectories))
            {
                string key = protectedRoot + "/" + file.Substring(path.Length + 1).Replace('\\', '/');
                if (!key.Contains("/PluginData/")) input.Files.Add(key, File.ReadAllText(file));
            }
            foreach (string file in Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories))
                input.Mods.Add(AssemblyName.GetAssemblyName(file).Name);
        }
        input.Mods.Add("ModuleManager");
        var parsed = input.Files.Select(p => new { Path = p.Key, Node = ConfigNode.Parse(p.Value) }).ToArray();
        var tags = new Dictionary<string, string>();
        foreach (var file in parsed)
            foreach (ConfigNode localization in file.Node.GetNodes("Localization"))
                foreach (ConfigNode language in localization.GetNodes("en-us"))
                    foreach (ConfigNode.Value value in language.values) tags[value.name] = value.value;
        object localizer = FormatterServices.GetUninitializedObject(typeof(Localizer));
        Set(localizer, "tagValues", tags);
        Set(localizer, "_currentLanguage", "en-us");
        typeof(Localizer).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public).GetSetMethod(true).Invoke(null, new[] { localizer });
        input.GameData = NewDirectory(null, "");
        foreach (var file in parsed)
        {
            UrlDir parent = input.GameData;
            string[] segments = file.Path.Split('/');
            for (int i = 0; i < segments.Length - 1; i++)
            {
                UrlDir child = parent.children.FirstOrDefault(d => d.name == segments[i]);
                if (child == null) { child = NewDirectory(parent, segments[i]); parent.children.Add(child); }
                parent = child;
            }
            var urlFile = (UrlDir.UrlFile)FormatterServices.GetUninitializedObject(typeof(UrlDir.UrlFile));
            Set(urlFile, "_name", Path.GetFileNameWithoutExtension(segments.Last()));
            Set(urlFile, "_fileExtension", "cfg");
            Set(urlFile, "_path", file.Path);
            Set(urlFile, "_root", input.GameData);
            Set(urlFile, "_parent", parent);
            Set(urlFile, "_configs", new List<UrlDir.UrlConfig>());
            parent.files.Add(urlFile);
            foreach (ConfigNode node in file.Node.GetNodes())
            {
                if (focused && node.name == "PART" && !observed.Any(w => w.Part == KspName(node)) && (node.GetValue("name") ?? "").IndexOf("antimatter", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (node.name != "Localization") Localizer.TranslateBranch(node);
                var config = new UrlDir.UrlConfig(urlFile, node);
                urlFile.configs.Add(config);
                input.Configs.Add(config);
                string valueName;
                if (CommandParser.Parse(node.name, out valueName) != Command.Insert)
                {
                    string compact = string.Concat(valueName.Where(c => !char.IsWhiteSpace(c)));
                    int start = compact.IndexOf(":FOR[", StringComparison.Ordinal);
                    if (start >= 0)
                    {
                        int end = compact.IndexOf(']', start + 5);
                        Check(end > start, "Balanced FOR declaration: " + file.Path);
                        input.Mods.Add(compact.Substring(start + 5, end - start - 5));
                    }
                }
            }
        }
        return input;
    }

    private static ArchiveInput ReadArchive(string path, string hash)
    {
        ArchiveInput cached;
        if (archiveCache.TryGetValue(path, out cached)) return cached;
        Check(File.Exists(path), "Catalog archive exists: " + path);
        var data = new ArchiveInput { Path = path, Hash = HashFile(path) };
        Check(data.Hash == hash, "Catalog archive hash: " + path);
        using (ZipArchive zip = ZipFile.OpenRead(path))
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (name.IndexOf('/') >= 0) data.Directories.Add(name.Split('/')[0].Replace(" ", ""));
                if (name.IndexOf("/PluginData/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (name.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
                    using (var read = new StreamReader(entry.Open())) data.Configs.Add(name, read.ReadToEnd());
                else if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    using (Stream read = entry.Open())
                    using (var memory = new MemoryStream())
                    {
                        read.CopyTo(memory);
                        try
                        {
                            byte[] bytes = memory.ToArray();
                            using (var source = new MemoryStream(bytes, false))
                            using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(source))
                            {
                                data.Assemblies.Add(assembly.Name.Name);
                                if (assembly.Name.Name == "B9PartSwitch")
                                    using (var sha = SHA256.Create())
                                        Check(BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() == HashFile(typeof(ModuleMatcher).Assembly.Location), "Loaded B9 DLL matches catalog archive exactly.");
                            }
                        }
                        catch (BadImageFormatException) { /* Native libraries do not advertise managed mods. */ }
                    }
                }
            }
        archiveCache.Add(path, data);
        return data;
    }

    private static UrlDir NewDirectory(UrlDir parent, string name)
    {
        var directory = (UrlDir)FormatterServices.GetUninitializedObject(typeof(UrlDir));
        Set(directory, "_name", name); Set(directory, "_parent", parent);
        Set(directory, "_root", parent == null ? directory : parent.root);
        Set(directory, "_children", new List<UrlDir>()); Set(directory, "_files", new List<UrlDir.UrlFile>());
        return directory;
    }

    private static void Set(object target, string field, object value)
    {
        FieldInfo info = target.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (info == null) throw new MissingFieldException(target.GetType().FullName, field);
        info.SetValue(target, value);
    }

    private static IEnumerable<Selector> GetSelectors(IEnumerable<ConfigNode> parts)
    {
        foreach (ConfigNode part in parts)
            foreach (ConfigNode module in part.GetNodes("MODULE").Where(m => m.GetValue("name") == "ModuleB9PartSwitch"))
                foreach (ConfigNode subtype in module.GetNodes("SUBTYPE"))
                    foreach (ConfigNode modifier in subtype.GetNodes("MODULE"))
                    {
                        ConfigNode identifier = modifier.GetNode("IDENTIFIER");
                        Check(identifier != null && identifier.HasValue("name"), "B9 modifier has named identifier.");
                        yield return new Selector { Part = KspName(part), Module = module.GetValue("moduleID"), Subtype = subtype.GetValue("name"), Identifier = identifier, Modifier = modifier, PartNode = part };
                    }
    }

    private static bool IsWaterfall(Selector selector)
    {
        return StringMatcher.Parse(selector.Identifier.GetValue("name")).Match("ModuleWaterfallFX");
    }

    private static int MatchCount(Selector selector)
    {
        var matcher = new ModuleMatcher(selector.Identifier);
        MethodInfo method = typeof(ModuleMatcher).GetMethod("NodeMatchesModule", BindingFlags.Instance | BindingFlags.NonPublic);
        if (selector.Identifier.values.Count == 1)
            return selector.PartNode.GetNodes("MODULE").Count(n => (bool)method.Invoke(matcher, new object[] { null, n }));
        var name = StringMatcher.Parse(selector.Identifier.GetValue("name"));
        return selector.PartNode.GetNodes("MODULE").Count(n => name.Match(n.GetValue("name") ?? "") && selector.Identifier.values.Cast<ConfigNode.Value>().All(v => v.name == "name" || n.GetValue(v.name) == v.value));
    }

    private static string KspName(ConfigNode part) { return (part.GetValue("name") ?? "").Replace('_', '.'); }

    private static string HashFile(string path)
    {
        using (var sha = SHA256.Create())
        using (Stream read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            return BitConverter.ToString(sha.ComputeHash(read)).Replace("-", "").ToLowerInvariant();
    }

    private static void WriteJson(string path, object value) { File.WriteAllText(path, json.Serialize(value)); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); checks++; }

    private sealed class FileLogger : IBasicLogger, IDisposable
    {
        private readonly StreamWriter writer;
        public int PolicyErrors;
        public FileLogger(string path) { writer = new StreamWriter(path); writer.AutoFlush = true; }
        public void Pass(string name) { writer.WriteLine("REAL MODULEMANAGER PASS " + name); }
        public void Log(ILogMessage message)
        {
            if (message.Message.Contains("pass specifier detected on insert node") && message.Message.IndexOf("ZZZ_KESASPotato", StringComparison.OrdinalIgnoreCase) >= 0) PolicyErrors++;
            if (message.LogType != UnityEngine.LogType.Log || message.Message.Contains("DisableExhaust")) writer.WriteLine(message.ToLogString());
        }
        public void Dispose() { writer.Dispose(); }
    }

    private sealed class ObservedPass : IPass
    {
        private readonly IPass inner;
        private readonly PolicyObserver observer;
        public ObservedPass(IPass inner, PolicyObserver observer) { this.inner = inner; this.observer = observer; }
        public string Name { get { return inner.Name; } }
        public IEnumerator<IPatch> GetEnumerator() { return inner.Select(p => (IPatch)new ObservedPatch(p, observer)).GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }

    private sealed class ObservedPatch : IPatch
    {
        private readonly IPatch inner;
        private readonly PolicyObserver observer;
        public ObservedPatch(IPatch inner, PolicyObserver observer) { this.inner = inner; this.observer = observer; }
        public UrlDir.UrlConfig UrlConfig { get { return inner.UrlConfig; } }
        public IPassSpecifier PassSpecifier { get { return inner.PassSpecifier; } }
        public bool CountsAsPatch { get { return inner.CountsAsPatch; } }
        public void Apply(LinkedList<IProtoUrlConfig> configs, IPatchProgress progress, IBasicLogger logger)
        {
            bool policy = inner.CountsAsPatch && inner.UrlConfig.parent.url.EndsWith("ZZZ_KESASPotato/DisableExhaust", StringComparison.OrdinalIgnoreCase);
            if (policy) observer.Before(configs);
            inner.Apply(configs, progress, logger);
            if (policy) observer.After(configs);
        }
    }

    private sealed class PolicyObserver
    {
        private readonly bool legacy;
        private readonly string directory;
        private Dictionary<string, ConfigNode> before;
        public int Applications;
        public int PreservedParts;
        public int PreservedModifiers;
        public PolicyObserver(bool legacy, string directory) { this.legacy = legacy; this.directory = directory; }
        public void Before(IEnumerable<IProtoUrlConfig> configs)
        {
            before = configs.Where(c => c.Node.name == "PART").ToDictionary(c => c.FullUrl, c => c.Node.DeepCopy());
            File.WriteAllText(Path.Combine(directory, "BeforePolicyParts.cfg"), string.Join("\n", before.Values.Select(p => p.ToString())));
        }
        public void After(IEnumerable<IProtoUrlConfig> configs)
        {
            Applications++;
            if (legacy) return;
            foreach (IProtoUrlConfig config in configs.Where(c => c.Node.name == "PART"))
            {
                ConfigNode expected = before[config.FullUrl];
                foreach (ConfigNode module in expected.GetNodes("MODULE").Where(m => m.GetValue("name") == "ModuleB9PartSwitch"))
                    foreach (ConfigNode subtype in module.GetNodes("SUBTYPE"))
                        foreach (ConfigNode modifier in subtype.GetNodes("MODULE"))
                            if (modifier.GetNode("IDENTIFIER") != null && modifier.GetNode("IDENTIFIER").GetValue("name") == "ModuleWaterfallFX") subtype.nodes.Remove(modifier);
                            else PreservedModifiers++;
                foreach (ConfigNode node in expected.GetNodes())
                {
                    if (node.name == "MODULE" && node.GetValue("name") == "ModuleWaterfallFX" || node.name == "MODEL" && (node.GetValue("model") ?? "").StartsWith("StockWaterfallEffects/", StringComparison.Ordinal)) expected.nodes.Remove(node);
                    else if (node.name == "EFFECTS")
                        foreach (ConfigNode group in node.GetNodes()) { group.nodes.Clear(); group.values.Clear(); }
                }
                foreach (ConfigNode.Value value in expected.values.Cast<ConfigNode.Value>().ToArray())
                    if (value.name.StartsWith("fx_", StringComparison.Ordinal) || value.name.StartsWith("sound_", StringComparison.Ordinal)) expected.values.Remove(value);
                Check(expected.ToString() == config.Node.ToString(), "Only enumerated visual changes on " + config.FullUrl);
                PreservedParts++;
            }
        }
    }
}
