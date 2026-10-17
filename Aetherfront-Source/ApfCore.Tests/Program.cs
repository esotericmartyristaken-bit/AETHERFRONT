using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using ApfCore;

namespace ApfCoreTests
{
    internal static class Program
    {
        private static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "apf-core-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var gameData = Path.Combine(root, "GameData");
                Directory.CreateDirectory(Path.Combine(gameData, "Squad"));
                Directory.CreateDirectory(Path.Combine(gameData, "ExtraMod"));
                File.WriteAllText(Path.Combine(gameData, "ExtraMod", "old.cfg"), "old");

                var package = new ApfPackage
                {
                    Id = "example",
                    Version = "1.0.0",
                    ArchiveUrl = "https://example.invalid/example.zip",
                    Destination = "GameData/ExampleMod",
                    StripPrefix = "ExampleMod"
                };
                var manifest = new ApfPackManifest
                {
                    ServerId = "test",
                    PackId = "test-pack",
                    PackVersion = "1",
                    RequiredDlc = { "SquadExpansion/MakingHistory" },
                    ProtectedRoots = { "Squad", "LunaMultiPlayer" },
                    ManagedPackages = { package }
                };

                var collisionManifest = new ApfPackManifest
                {
                    ServerId = "test",
                    PackId = "collision",
                    PackVersion = "1",
                    ManagedPackages =
                    {
                        new ApfPackage { Id = "one", Version = "1", ArchiveUrl = "https://example.invalid/one.zip", Sha256 = new string('a', 64), Destination = "GameData/Shared", StripPrefix = "Shared" },
                        new ApfPackage { Id = "two", Version = "1", ArchiveUrl = "https://example.invalid/two.zip", Sha256 = new string('b', 64), Destination = "GameData/Shared", StripPrefix = "Shared" }
                    }
                };
                var collisionValidation = ApfManifestValidator.Validate(collisionManifest);
                Assert(!collisionValidation.IsValid && collisionValidation.Errors.Any(e => e.Contains("collides")), "package collision rejected");

                var clientOnlyFile = Path.Combine(gameData, "GraphicsMod", "Graphics.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(clientOnlyFile));
                File.WriteAllText(clientOnlyFile, "graphics");
                manifest.ClientOnly.Add(new ApfClientOnlyRule
                {
                    Id = "graphics",
                    PathRoots = { "GameData/GraphicsMod" },
                    Dlls = { new ApfClientOnlyDll { Path = "GameData/GraphicsMod/Graphics.dll", Sha256 = ApfJson.Sha256File(clientOnlyFile) } }
                });

                var generatedGameData = Path.Combine(root, "GeneratedGameData");
                Directory.CreateDirectory(Path.Combine(generatedGameData, "ExtraMod"));
                File.WriteAllText(Path.Combine(generatedGameData, "ExtraMod", "mod.cfg"), "mod");
                File.WriteAllText(Path.Combine(generatedGameData, "ModuleManager.ConfigCache"), "generated");
                var generatedPack = ApfPackBuilder.Build(new ApfPackBuildOptions
                {
                    SourceGameData = generatedGameData,
                    OutputDirectory = Path.Combine(root, "pack-output-generated"),
                    ServerId = "test",
                    PackId = "generated-pack",
                    PackVersion = "1.0.0",
                    ArchiveBaseUrl = "https://cdn.example.invalid/apf/"
                });
                Assert(generatedPack.Manifest.ProtectedRoots.Any(rootName => rootName.Equals("modulemanager.configcache", StringComparison.OrdinalIgnoreCase)), "generated ModuleManager state retained");
                File.WriteAllText(Path.Combine(generatedGameData, "unknown.txt"), "unexpected");
                var loosePack = ApfPackBuilder.Build(new ApfPackBuildOptions
                {
                    SourceGameData = generatedGameData,
                    OutputDirectory = Path.Combine(root, "pack-output-unknown-loose"),
                    ServerId = "test",
                    PackId = "unknown-loose-pack",
                    PackVersion = "1.0.0",
                    ArchiveBaseUrl = "https://cdn.example.invalid/apf/"
                });
                Assert(loosePack.Manifest.ManagedPackages.Any(p => p.Id.Equals("loose-unknown.txt", StringComparison.OrdinalIgnoreCase)), "unknown loose GameData file packaged");
                var loosePackage = loosePack.Manifest.ManagedPackages.Single(p => p.Id.Equals("loose-unknown.txt", StringComparison.OrdinalIgnoreCase));
                var looseExtract = Path.Combine(root, "loose-extract");
                ApfArchiveInstaller.ExtractPackage(loosePack.ArchivePaths.Single(path => path.Contains("unknown.txt")), loosePackage, looseExtract);
                Assert(File.Exists(Path.Combine(looseExtract, "unknown.txt")), "loose GameData file installs at root");
                var looseState = new ApfPackState { PackId = loosePack.Manifest.PackId, PackVersion = loosePack.Manifest.PackVersion };
                looseState.Packages.Add(new ApfPackageState
                {
                    Id = loosePackage.Id,
                    Version = loosePackage.Version,
                    ArchiveSha256 = loosePackage.Sha256,
                    Root = ApfPaths.PackageRoot(loosePackage),
                    Files = ApfPackScanner.BuildFileState(looseExtract, Path.Combine(looseExtract, "unknown.txt"))
                });
                Assert(!ApfPackScanner.Scan(looseExtract, loosePack.Manifest, looseState).Install
                        .Any(change => change.Package.Id.Equals(loosePackage.Id, StringComparison.OrdinalIgnoreCase)),
                    "installed loose-file package remains clean");

                var builtPack = ApfPackBuilder.Build(new ApfPackBuildOptions
                {
                    SourceGameData = gameData,
                    OutputDirectory = Path.Combine(root, "pack-output"),
                    ServerId = "test",
                    PackId = "built-pack",
                    PackVersion = "2.0.0",
                    KspVersion = "1.12.5",
                    RequiredClientVersion = "apf-client-1",
                    ArchiveBaseUrl = "https://cdn.example.invalid/apf/"
                });
                Assert(builtPack.Manifest.ManagedPackages.Count == 2 && builtPack.Manifest.ManagedPackages.Any(p => p.Id == "ExtraMod") && builtPack.Manifest.ManagedPackages.Any(p => p.Id == "GraphicsMod"), "pack builder managed roots");
                Assert(builtPack.Manifest.ClientOnly.Count == 0, "pack builder default client-only set");
                Assert(builtPack.ArchivePaths.Count == 2 && builtPack.ArchivePaths.All(File.Exists), "pack builder archives");
                Assert(ApfManifestValidator.Validate(builtPack.Manifest).IsValid, "pack builder manifest validation");
                var builtArchive = builtPack.ArchivePaths.Single(path => Path.GetFileName(path).StartsWith("ExtraMod-", StringComparison.OrdinalIgnoreCase));
                using (var builtZip = ZipFile.OpenRead(builtArchive))
                    Assert(builtZip.Entries.Any(entry => entry.FullName.Equals("ExtraMod/old.cfg", StringComparison.OrdinalIgnoreCase)), "builder archive preserves stripPrefix");
                var extractedBuiltGameData = Path.Combine(root, "built-extract", "GameData");
                Directory.CreateDirectory(extractedBuiltGameData);
                var builtPackage = builtPack.Manifest.ManagedPackages.Single(p => p.Id == "ExtraMod");
                ApfArchiveInstaller.ExtractPackage(builtArchive, builtPackage, extractedBuiltGameData);
                Assert(File.Exists(Path.Combine(extractedBuiltGameData, "ExtraMod", "old.cfg")), "builder archive installs successfully");

                var builtClientOnlyPack = ApfPackBuilder.Build(new ApfPackBuildOptions
                {
                    SourceGameData = gameData,
                    OutputDirectory = Path.Combine(root, "pack-output-client-only"),
                    ServerId = "test",
                    PackId = "built-client-only-pack",
                    PackVersion = "2.0.0",
                    ArchiveBaseUrl = "https://cdn.example.invalid/apf/",
                    ClientOnlyRoots = { "GraphicsMod" }
                });
                Assert(builtClientOnlyPack.Manifest.ManagedPackages.Count == 1, "pack builder excludes client-only root");
                Assert(builtClientOnlyPack.Manifest.ClientOnly.Count == 1 && builtClientOnlyPack.Manifest.ClientOnly[0].Dlls.Count == 1, "pack builder client-only hash");

                var archive = Path.Combine(root, "example.zip");
                using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                {
                    var entry = zip.CreateEntry("ExampleMod/Example.dll");
                    using (var writer = new StreamWriter(entry.Open())) writer.Write("mod");
                }
                package.Sha256 = ApfJson.Sha256File(archive);
                package.SizeBytes = new FileInfo(archive).Length;

                Assert(ApfManifestValidator.Validate(manifest).IsValid, "manifest validation");
                var bomRoundTrip = ApfPackManifest.FromJson("\uFEFF" + manifest.ToJson());
                Assert(bomRoundTrip.PackId == manifest.PackId, "manifest UTF-8 BOM tolerated");
                var httpManifest = new ApfPackManifest
                {
                    ServerId = "test",
                    PackId = "http-rejected",
                    PackVersion = "1",
                    ManagedPackages = { new ApfPackage { Id = "http", Version = "1", ArchiveUrl = "http://example.invalid/mod.zip", Sha256 = new string('a', 64), Destination = "GameData/Http", StripPrefix = "Http" } }
                };
                Assert(!ApfManifestValidator.Validate(httpManifest).IsValid, "non-HTTPS package rejected");
                var traversalManifest = new ApfPackManifest
                {
                    ServerId = "test",
                    PackId = "traversal-rejected",
                    PackVersion = "1",
                    ManagedPackages = { new ApfPackage { Id = "traversal", Version = "1", ArchiveUrl = "https://example.invalid/mod.zip", Sha256 = new string('a', 64), Destination = "GameData/../Escape", StripPrefix = "Escape" } }
                };
                Assert(!ApfManifestValidator.Validate(traversalManifest).IsValid, "manifest traversal rejected");
                var unsafeProtectedManifest = new ApfPackManifest
                {
                    ServerId = "test",
                    PackId = "unsafe-protected",
                    PackVersion = "1",
                    ProtectedRoots = { "C:/outside" }
                };
                Assert(!ApfManifestValidator.Validate(unsafeProtectedManifest).IsValid, "absolute protected root rejected");
                var nestedProtectedManifest = new ApfPackManifest
                {
                    ServerId = "test",
                    PackId = "nested-protected",
                    PackVersion = "1",
                    ProtectedRoots = { "GameData/Protected/Keep" },
                    ManagedPackages =
                    {
                        new ApfPackage
                        {
                            Id = "protected-parent",
                            Version = "1",
                            ArchiveUrl = "https://example.invalid/protected.zip",
                            Sha256 = new string('c', 64),
                            Destination = "GameData/Protected",
                            StripPrefix = "Protected"
                        }
                    }
                };
                Assert(!ApfManifestValidator.Validate(nestedProtectedManifest).IsValid, "protected ancestor collision rejected");
                var nestedProtectedGameData = Path.Combine(root, "nested-protected-gamedata");
                Directory.CreateDirectory(Path.Combine(nestedProtectedGameData, "Protected", "Keep"));
                File.WriteAllText(Path.Combine(nestedProtectedGameData, "Protected", "Keep", "keep.cfg"), "keep");
                var nestedProtectedScan = ApfPackScanner.Scan(nestedProtectedGameData, new ApfPackManifest
                {
                    ServerId = "test",
                    PackId = "nested-protected-scan",
                    PackVersion = "1",
                    ProtectedRoots = { "GameData/Protected/Keep" }
                }, new ApfPackState());
                Assert(!nestedProtectedScan.Delete.Any(change => change.RelativePath.Equals("GameData/Protected", StringComparison.OrdinalIgnoreCase)), "nested protected parent retained");
                Assert(!ApfPaths.IsSafeRelative(@"C:relative"), "drive-relative path rejected");
                Assert(!ApfPaths.IsSafeRelative("GameData/CON"), "Windows device path rejected");
                Assert(!ApfPaths.IsSafeRelative("GameData/Mod."), "trailing-dot path rejected");
                Assert(!ApfPaths.IsSafeRelative("GameData/Mod "), "trailing-space path rejected");
                Assert(ApfPaths.NormalizeGameDataRelative("GameData//ExampleMod\\Plugin") == "examplemod/plugin", "path separators normalized");
                var duplicateClientDllManifest = new ApfPackManifest
                {
                    ServerId = "test",
                    PackId = "duplicate-client-dll",
                    PackVersion = "1",
                    ClientOnly =
                    {
                        new ApfClientOnlyRule
                        {
                            Id = "graphics-one",
                            PathRoots = { "GameData/GraphicsMod" },
                            Dlls = { new ApfClientOnlyDll { Path = "GameData/GraphicsMod/Graphics.dll" } }
                        },
                        new ApfClientOnlyRule
                        {
                            Id = "graphics-two",
                            PathRoots = { "GameData/GraphicsMod2" },
                            Dlls = { new ApfClientOnlyDll { Path = "GameData/GraphicsMod/Graphics.dll" } }
                        }
                    }
                };
                Assert(!ApfManifestValidator.Validate(duplicateClientDllManifest).IsValid, "duplicate client-only DLL rejected");
                var nestedClientOnlyManifest = new ApfPackManifest
                {
                    ServerId = "test",
                    PackId = "nested-client-only",
                    PackVersion = "1",
                    ClientOnly =
                    {
                        new ApfClientOnlyRule
                        {
                            Id = "nested",
                            PathRoots = { "GameData/GraphicsMod/Textures" }
                        }
                    }
                };
                Assert(!ApfManifestValidator.Validate(nestedClientOnlyManifest).IsValid, "nested client-only root rejected");
                Assert(ApfManifestValidator.Validate(manifest).IsValid, "required DLC path validation");
                var initial = ApfPackScanner.Scan(gameData, manifest, new ApfPackState());
                Assert(initial.Install.Count == 1, "missing package detected");
                Assert(initial.Delete.Count == 1, "extra root detected");
                Assert(initial.Errors.Count == 0, "client-only hash accepted");
                var corruptState = new ApfPackState
                {
                    Packages =
                    {
                        null,
                        new ApfPackageState { Id = "example", Version = "old" },
                        new ApfPackageState { Id = "example", Version = "new" }
                    }
                };
                Assert(ApfPackScanner.Scan(gameData, manifest, corruptState).Install.Count == 1, "corrupt state tolerated");

                ApfArchiveInstaller.ExtractPackage(archive, package, gameData);
                var state = new ApfPackState { PackId = manifest.PackId, PackVersion = manifest.PackVersion };
                state.Packages.Add(new ApfPackageState
                {
                    Id = package.Id,
                    Version = package.Version,
                    ArchiveSha256 = package.Sha256,
                    Root = ApfPaths.PackageRoot(package),
                    Files = ApfPackScanner.BuildFileState(gameData, Path.Combine(gameData, "ExampleMod"))
                });
                var clean = ApfPackScanner.Scan(gameData, manifest, state);
                Assert(clean.Install.Count == 0, "installed package clean: " + string.Join(" | ", clean.Install.ConvertAll(c => c.Reason)));
                Assert(clean.Delete.Count == 1, "unmanaged root still visible");

                File.WriteAllText(Path.Combine(gameData, "ExampleMod", "Example.dll"), "tampered");
                Assert(ApfPackScanner.Scan(gameData, manifest, state).Install.Count == 1, "tamper detected");

                File.WriteAllText(clientOnlyFile, "tampered graphics");
                Assert(ApfPackScanner.Scan(gameData, manifest, state).Errors.Any(e => e.Contains("Client-only DLL hash mismatch")), "client-only tamper detected");

                var reparseGameData = Path.Combine(root, "reparse-gamedata");
                var reparseOutside = Path.Combine(root, "reparse-outside");
                Directory.CreateDirectory(reparseGameData);
                Directory.CreateDirectory(reparseOutside);
                File.WriteAllText(Path.Combine(reparseOutside, "outside.cfg"), "outside");
                var reparseLink = Path.Combine(reparseGameData, "LinkedMod");
                try
                {
                    Directory.CreateSymbolicLink(reparseLink, reparseOutside);
                    var reparsePlan = ApfPackScanner.Scan(reparseGameData, new ApfPackManifest
                    {
                        ServerId = "test",
                        PackId = "reparse-pack",
                        PackVersion = "1"
                    }, new ApfPackState());
                    Assert(reparsePlan.Errors.Any(e => e.Contains("reparse point")), "GameData reparse point rejected before scan");
                }
                catch (UnauthorizedAccessException)
                {
                    Console.WriteLine("Symlink security test skipped: symbolic-link privilege unavailable");
                }
                catch (PlatformNotSupportedException)
                {
                    Console.WriteLine("Symlink security test skipped: symbolic links unsupported");
                }
                catch (IOException)
                {
                    Console.WriteLine("Symlink security test skipped: symbolic-link creation unavailable");
                }
                finally
                {
                    try { if (Directory.Exists(reparseLink)) Directory.Delete(reparseLink); } catch { }
                }

                // End-to-end batch plan: five unmanaged roots + three missing
                // packages must be represented by one combined transaction.
                var batchRoot = Path.Combine(root, "batch-game");
                var batchGameData = Path.Combine(batchRoot, "GameData");
                Directory.CreateDirectory(Path.Combine(batchGameData, "Squad"));
                for (var index = 1; index <= 5; index++)
                {
                    var extraRoot = Path.Combine(batchGameData, "Extra" + index);
                    Directory.CreateDirectory(extraRoot);
                    File.WriteAllText(Path.Combine(extraRoot, "extra.cfg"), "extra-" + index);
                }
                var batchManifest = new ApfPackManifest
                {
                    ServerId = "test",
                    PackId = "batch-pack",
                    PackVersion = "1",
                    ProtectedRoots = { "Squad", "LunaMultiPlayer" }
                };
                for (var index = 1; index <= 3; index++)
                    batchManifest.ManagedPackages.Add(new ApfPackage
                    {
                        Id = "Managed" + index,
                        Version = "1",
                        ArchiveUrl = "https://example.invalid/managed" + index + ".zip",
                        Sha256 = package.Sha256,
                        SizeBytes = package.SizeBytes,
                        Destination = "GameData/Managed" + index,
                        StripPrefix = "ExampleMod"
                    });
                var batchPlan = ApfPackScanner.Scan(batchGameData, batchManifest, new ApfPackState());
                Assert(batchPlan.Install.Count == 3 && batchPlan.Delete.Count == 5 && batchPlan.Operations.Count == 8,
                    "five-extra/three-missing combined APF plan");
                var batchStaging = Path.Combine(batchRoot, "APF", ".staging");
                Directory.CreateDirectory(batchStaging);
                var batchStaged = new List<ApfStagedPackage>();
                foreach (var change in batchPlan.Install)
                {
                    var stagedPath = Path.Combine(batchStaging, change.Package.Id + ".zip");
                    File.Copy(archive, stagedPath);
                    batchStaged.Add(new ApfStagedPackage { Package = change.Package, ArchivePath = stagedPath });
                }
                var batchKsp = Path.Combine(batchRoot, "KSP.exe");
                File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), batchKsp, true);
                var batchTransaction = new ApfSyncTransaction
                {
                    GameRoot = batchRoot,
                    GameDataPath = batchGameData,
                    ProcessId = 0,
                    KspExecutable = batchKsp,
                    KspArguments = new List<string> { "/c", "exit" },
                    StatePath = Path.Combine(batchGameData, "LunaMultiPlayer", "Data", "APFPackState.json"),
                    Manifest = batchManifest,
                    ExistingState = new ApfPackState(),
                    Packages = batchStaged,
                    Deletes = batchPlan.Delete
                };
                ApfArchiveInstaller.ApplyTransaction(batchTransaction);
                Assert(batchManifest.ManagedPackages.All(p => File.Exists(Path.Combine(batchGameData, p.Destination.Substring("GameData/".Length), "Example.dll"))),
                    "batch packages installed");
                Assert(Enumerable.Range(1, 5).All(index => !Directory.Exists(Path.Combine(batchGameData, "Extra" + index))),
                    "batch unmanaged roots deleted");
                Assert(File.Exists(batchTransaction.StatePath), "batch state committed");

                var unsafeArchive = Path.Combine(root, "unsafe.zip");
                using (var zip = ZipFile.Open(unsafeArchive, ZipArchiveMode.Create))
                {
                    var entry = zip.CreateEntry("ExampleMod/../evil.dll");
                    using (var writer = new StreamWriter(entry.Open())) writer.Write("bad");
                }
                var failed = false;
                try { ApfArchiveInstaller.ValidateArchive(unsafeArchive, package, gameData); }
                catch (InvalidDataException) { failed = true; }
                Assert(failed, "unsafe archive rejected");

                var forgedPackage = new ApfPackage
                {
                    Id = batchManifest.ManagedPackages[0].Id,
                    Version = batchManifest.ManagedPackages[0].Version,
                    ArchiveUrl = batchManifest.ManagedPackages[0].ArchiveUrl,
                    Sha256 = batchManifest.ManagedPackages[0].Sha256,
                    SizeBytes = batchManifest.ManagedPackages[0].SizeBytes,
                    Destination = "GameData/ForgedDestination",
                    StripPrefix = batchManifest.ManagedPackages[0].StripPrefix
                };
                var forgedTransaction = new ApfSyncTransaction
                {
                    GameRoot = batchRoot,
                    GameDataPath = batchGameData,
                    ProcessId = 0,
                    KspExecutable = batchKsp,
                    StatePath = batchTransaction.StatePath,
                    Manifest = batchManifest,
                    Packages = { new ApfStagedPackage { Package = forgedPackage, ArchivePath = batchStaged[0].ArchivePath } }
                };
                var forgedRejected = false;
                try { ApfArchiveInstaller.ApplyTransaction(forgedTransaction); }
                catch (InvalidDataException) { forgedRejected = true; }
                Assert(forgedRejected && !Directory.Exists(Path.Combine(batchGameData, "ForgedDestination")),
                    "transaction package metadata cannot be forged");

                var overlappingArchive = Path.Combine(root, "overlapping.zip");
                using (var zip = ZipFile.Open(overlappingArchive, ZipArchiveMode.Create))
                {
                    var file = zip.CreateEntry("ExampleMod/conflict");
                    using (var writer = new StreamWriter(file.Open())) writer.Write("file");
                    var child = zip.CreateEntry("ExampleMod/conflict/child.cfg");
                    using (var writer = new StreamWriter(child.Open())) writer.Write("child");
                }
                var overlappingRejected = false;
                try { ApfArchiveInstaller.ValidateArchive(overlappingArchive, package, gameData); }
                catch (InvalidDataException) { overlappingRejected = true; }
                Assert(overlappingRejected, "archive parent-child collision rejected");

                // A failure after the old package root has been moved must restore
                // both the original files and the pre-existing state-path object.
                var rollbackRoot = Path.Combine(root, "rollback-game");
                var rollbackGameData = Path.Combine(rollbackRoot, "GameData");
                var rollbackOldRoot = Path.Combine(rollbackGameData, "RollbackMod");
                Directory.CreateDirectory(rollbackOldRoot);
                File.WriteAllText(Path.Combine(rollbackOldRoot, "old.cfg"), "old rollback content");
                Directory.CreateDirectory(Path.Combine(rollbackRoot, "APF", ".staging"));
                var rollbackStateParent = Path.Combine(rollbackRoot, "state-parent");
                File.WriteAllText(rollbackStateParent, "not a directory");
                var rollbackPackage = new ApfPackage
                {
                    Id = "rollback",
                    Version = "1",
                    ArchiveUrl = "https://example.invalid/rollback.zip",
                    Sha256 = package.Sha256,
                    SizeBytes = package.SizeBytes,
                    Destination = "GameData/RollbackMod",
                    StripPrefix = "ExampleMod"
                };
                var rollbackManifest = new ApfPackManifest
                {
                    ServerId = "test",
                    PackId = "rollback-pack",
                    PackVersion = "1",
                    ManagedPackages = { rollbackPackage }
                };
                var rollbackStagedArchive = Path.Combine(rollbackRoot, "APF", ".staging", "rollback.zip");
                File.Copy(archive, rollbackStagedArchive);
                var rollbackTransaction = new ApfSyncTransaction
                {
                    GameRoot = rollbackRoot,
                    GameDataPath = rollbackGameData,
                    StatePath = Path.Combine(rollbackStateParent, "state.json"),
                    ProcessId = 0,
                    Manifest = rollbackManifest,
                    Packages = { new ApfStagedPackage { Package = rollbackPackage, ArchivePath = rollbackStagedArchive } }
                };
                var rollbackRejected = false;
                try { ApfArchiveInstaller.ApplyTransaction(rollbackTransaction); }
                catch (IOException) { rollbackRejected = true; }
                Assert(rollbackRejected, "post-mutation state failure rejected");
                Assert(File.ReadAllText(Path.Combine(rollbackOldRoot, "old.cfg")) == "old rollback content", "pack transaction rollback restored old root");
                Assert(!File.Exists(Path.Combine(rollbackOldRoot, "Example.dll")), "pack transaction rollback removed partial extraction");
                Assert(File.ReadAllText(rollbackStateParent) == "not a directory", "pack transaction rollback restored state path object");

                var missingRelaunchRejected = false;
                try
                {
                    ApfArchiveInstaller.ApplyTransaction(new ApfSyncTransaction
                    {
                        GameRoot = rollbackRoot,
                        GameDataPath = rollbackGameData,
                        KspExecutable = Path.Combine(rollbackRoot, "missing-ksp.exe"),
                        Manifest = rollbackManifest,
                        Packages = { new ApfStagedPackage { Package = rollbackPackage, ArchivePath = rollbackStagedArchive } }
                    });
                }
                catch (InvalidDataException) { missingRelaunchRejected = true; }
                Assert(missingRelaunchRejected && File.ReadAllText(Path.Combine(rollbackOldRoot, "old.cfg")) == "old rollback content" &&
                    !File.Exists(Path.Combine(rollbackOldRoot, "Example.dll")),
                    "missing relaunch target is rejected before mutation");

                var wrongProcessRejected = false;
                try
                {
                    ApfArchiveInstaller.ApplyTransaction(new ApfSyncTransaction
                    {
                        GameRoot = rollbackRoot,
                        GameDataPath = rollbackGameData,
                        ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
                        KspExecutable = batchKsp,
                        Manifest = rollbackManifest,
                        Packages = { new ApfStagedPackage { Package = rollbackPackage, ArchivePath = rollbackStagedArchive } }
                    });
                }
                catch (InvalidDataException) { wrongProcessRejected = true; }
                Assert(wrongProcessRejected && File.ReadAllText(Path.Combine(rollbackOldRoot, "old.cfg")) == "old rollback content" &&
                    !File.Exists(Path.Combine(rollbackOldRoot, "Example.dll")),
                    "wrong live process ID is rejected before mutation");

                var helperKeyPath = Path.Combine(root, "GameData", "LunaMultiPlayer", "Data", "APFHelper.key");
                var helperKey = ApfTransactionSecurity.LoadOrCreateKey(helperKeyPath);
                var transaction = new ApfSyncTransaction
                {
                    GameRoot = root,
                    GameDataPath = gameData,
                    Manifest = manifest
                };
                ApfTransactionSecurity.Sign(transaction, helperKey);
                Assert(ApfTransactionSecurity.Verify(transaction, helperKey), "helper transaction authorization");
                transaction.Authorization = new string('0', 64);
                Assert(!ApfTransactionSecurity.Verify(transaction, helperKey), "forged helper transaction rejected");
                Assert(ApfProcessArguments.Join(new[] { "--path", "C:\\KSP Clone\\GameData" }) == "--path \"C:\\KSP Clone\\GameData\"", "Windows argument quoting preserves backslashes");
                Assert(ApfProcessArguments.Quote("a\"b") == "\"a\\\"b\"", "Windows argument quote escaping");

                var readOnlyFile = Path.Combine(gameData, "ReadOnlyMod", "mod.cfg");
                Directory.CreateDirectory(Path.GetDirectoryName(readOnlyFile));
                File.WriteAllText(readOnlyFile, "must remain");
                File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);
                var readOnlyTransaction = new ApfSyncTransaction
                {
                    GameRoot = root,
                    GameDataPath = gameData,
                    Manifest = manifest,
                    Deletes = { new ApfDeleteChange { RelativePath = "GameData/ReadOnlyMod", Reason = "test" } }
                };
                var readOnlyRejected = false;
                try { ApfArchiveInstaller.ApplyTransaction(readOnlyTransaction); }
                catch (IOException) { readOnlyRejected = true; }
                Assert(readOnlyRejected && File.Exists(readOnlyFile), "read-only transaction has no partial mutation");
                File.SetAttributes(readOnlyFile, FileAttributes.Normal);

                var updateRoot = Path.Combine(root, "client-update-game");
                var updateGameData = Path.Combine(updateRoot, "GameData");
                var updatePluginDirectory = Path.Combine(updateGameData, "LunaMultiPlayer", "Plugins");
                Directory.CreateDirectory(updatePluginDirectory);
                var updateFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["GameData/LunaMultiPlayer/Plugins/LmpClient.dll"] = "new-client-binary",
                    ["GameData/LunaMultiPlayer/Plugins/ApfCore.dll"] = "new-apf-core-binary"
                };
                foreach (var updateFile in updateFiles)
                    File.WriteAllText(Path.Combine(updateRoot, updateFile.Key.Replace('/', Path.DirectorySeparatorChar)), "old-" + updateFile.Value);

                var updateManifest = new ApfClientUpdateManifest
                {
                    Version = "apf-client-2",
                    KspVersion = "1.12.5",
                    PackageUrl = "https://updates.example.invalid/APFClient-2.zip",
                    ReleaseNotes = "test update"
                };
                var updateArchive = Path.Combine(updateRoot, "APFClient-2.zip");
                using (var zip = ZipFile.Open(updateArchive, ZipArchiveMode.Create))
                {
                    foreach (var updateFile in updateFiles)
                    {
                        var entry = zip.CreateEntry(updateFile.Key);
                        using (var writer = new StreamWriter(entry.Open())) writer.Write(updateFile.Value);
                        var bytes = System.Text.Encoding.UTF8.GetBytes(updateFile.Value);
                        updateManifest.Files.Add(new ApfClientUpdateFile
                        {
                            Path = updateFile.Key,
                            Sha256 = ApfJson.ToHex(SHA256.Create().ComputeHash(bytes)),
                            SizeBytes = bytes.Length
                        });
                    }
                }
                updateManifest.Sha256 = ApfJson.Sha256File(updateArchive);
                updateManifest.SizeBytes = new FileInfo(updateArchive).Length;
                using (var rsa = RSA.Create(2048))
                {
                    ApfClientUpdateSecurity.Sign(updateManifest, rsa);
                    var publicKey = ApfClientUpdateSecurity.LoadXmlPublicKey(ApfClientUpdateSecurity.ExportXml(rsa, false));
                    Assert(ApfClientUpdateValidator.Validate(updateManifest).IsValid, "client update manifest validation");
                    Assert(ApfClientUpdateSecurity.Verify(updateManifest, publicKey), "client update signature verification");
                    updateManifest.Version = "apf-client/unsafe";
                    Assert(!ApfClientUpdateValidator.Validate(updateManifest, false).IsValid, "unsafe client update version rejected");
                    updateManifest.Version = "apf-client-2";
                    updateManifest.ReleaseNotes = "tampered";
                    Assert(!ApfClientUpdateSecurity.Verify(updateManifest, publicKey), "client update tamper rejected");
                    updateManifest.ReleaseNotes = "test update";
                }
                ApfArchiveInstaller.ValidateClientUpdateArchive(updateArchive, updateManifest, updateRoot);
                Assert(ApfClientUpdateVersion.Compare("apf-client-2", "apf-client-1") > 0, "client update version comparison");
                Assert(ApfClientUpdateVersion.Compare("apf-client-1", "apf-client-2") < 0, "client update downgrade comparison");

                var updateStage = Path.Combine(updateRoot, "APF", ".client-update-staging", "apf-client-2");
                Directory.CreateDirectory(updateStage);
                var stagedUpdateArchive = Path.Combine(updateStage, "APFClientUpdate.zip");
                File.Copy(updateArchive, stagedUpdateArchive, true);
                var fakeKspExecutable = Path.Combine(updateRoot, "KSP.exe");
                File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), fakeKspExecutable, true);
                var updateTransaction = new ApfClientUpdateTransaction
                {
                    GameRoot = updateRoot,
                    GameDataPath = updateGameData,
                    ProcessId = 0,
                    KspExecutable = fakeKspExecutable,
                    KspArguments = new List<string> { "/c", "exit" },
                    ArchivePath = stagedUpdateArchive,
                    Manifest = updateManifest
                };
                var missingUpdateRelaunchRejected = false;
                try
                {
                    ApfArchiveInstaller.ApplyClientUpdate(new ApfClientUpdateTransaction
                    {
                        GameRoot = updateRoot,
                        GameDataPath = updateGameData,
                        KspExecutable = Path.Combine(updateRoot, "missing-ksp.exe"),
                        ArchivePath = stagedUpdateArchive,
                        Manifest = updateManifest
                    });
                }
                catch (InvalidDataException) { missingUpdateRelaunchRejected = true; }
                Assert(missingUpdateRelaunchRejected &&
                    File.ReadAllText(Path.Combine(updateRoot, updateFiles.Keys.First().Replace('/', Path.DirectorySeparatorChar))) == "old-new-client-binary",
                    "missing client-update relaunch target is rejected before mutation");
                ApfArchiveInstaller.ApplyClientUpdate(updateTransaction);
                Assert(File.ReadAllText(Path.Combine(updateRoot, updateFiles.Keys.First().Replace('/', Path.DirectorySeparatorChar))) == updateFiles.Values.First(), "client update replaces LmpClient atomically");
                Assert(File.ReadAllText(Path.Combine(updateRoot, updateFiles.Keys.Last().Replace('/', Path.DirectorySeparatorChar))) == updateFiles.Values.Last(), "client update replaces ApfCore atomically");

                var readOnlyUpdateTarget = Path.Combine(updateRoot, updateFiles.Keys.Last().Replace('/', Path.DirectorySeparatorChar));
                File.SetAttributes(readOnlyUpdateTarget, FileAttributes.ReadOnly);
                var updateRollbackRejected = false;
                try { ApfArchiveInstaller.ApplyClientUpdate(updateTransaction); }
                catch (IOException) { updateRollbackRejected = true; }
                Assert(updateRollbackRejected, "read-only client update rejected");
                File.SetAttributes(readOnlyUpdateTarget, FileAttributes.Normal);
                Assert(File.ReadAllText(Path.Combine(updateRoot, updateFiles.Keys.First().Replace('/', Path.DirectorySeparatorChar))) == updateFiles.Values.First(), "failed client update rolled back earlier replacement");

                var unexpectedArchive = Path.Combine(updateRoot, "unexpected.zip");
                using (var zip = ZipFile.Open(unexpectedArchive, ZipArchiveMode.Create))
                {
                    foreach (var updateFile in updateFiles)
                    {
                        var entry = zip.CreateEntry(updateFile.Key);
                        using (var writer = new StreamWriter(entry.Open())) writer.Write(updateFile.Value);
                    }
                    var extra = zip.CreateEntry("GameData/LunaMultiPlayer/Plugins/evil.dll");
                    using (var writer = new StreamWriter(extra.Open())) writer.Write("evil");
                }
                var unexpectedRejected = false;
                try { ApfArchiveInstaller.ValidateClientUpdateArchive(unexpectedArchive, updateManifest, updateRoot); }
                catch (InvalidDataException) { unexpectedRejected = true; }
                Assert(unexpectedRejected, "unexpected client update file rejected");

                var careerProfile = new ApfCareerProfile
                {
                    ProfileId = "apf-main-career",
                    DisplayName = "APF Main Career",
                    SaveNamespace = "APF/apf-main/Career",
                    StartingFunds = 25000,
                    StartingScience = 0,
                    StartingReputation = 0
                };
                Assert(ApfCareerProfileValidator.Validate(careerProfile).IsValid, "career profile contract validation");
                careerProfile.SaveNamespace = "../outside";
                Assert(!ApfCareerProfileValidator.Validate(careerProfile).IsValid, "career namespace traversal rejected");

                var diagnosticRoot = Path.Combine(root, "diagnostic-game");
                var diagnosticData = Path.Combine(diagnosticRoot, "GameData", "LunaMultiplayer", "Data");
                Directory.CreateDirectory(diagnosticData);
                Directory.CreateDirectory(Path.Combine(diagnosticRoot, "GameData", "ExampleMod", "Plugins"));
                File.WriteAllText(Path.Combine(diagnosticRoot, "KSP.log"),
                    "Loading " + diagnosticRoot + " for person@example.com token=very-secret-value");
                File.WriteAllText(Path.Combine(diagnosticData, "settings.xml"),
                    "<settings><Password>5293</Password><TimeoutSeconds>60</TimeoutSeconds></settings>");
                File.WriteAllText(Path.Combine(diagnosticRoot, "GameData", "ExampleMod", "Plugins", "Example.dll"), "test dll");
                var diagnosticResult = ApfDiagnosticBundleBuilder.Build(diagnosticRoot,
                    Path.Combine(diagnosticRoot, "APF", "Diagnostics", "Outbox"), new ApfDiagnosticContext
                    {
                        ClientVersion = "apf-client-test",
                        KspVersion = "1.12.5",
                        SessionId = "session-test",
                        Description = "Reproduction details"
                    });
                Assert(File.Exists(diagnosticResult.ArchivePath) && diagnosticResult.SizeBytes > 0, "diagnostic bundle created");
                using (var diagnosticZip = ZipFile.OpenRead(diagnosticResult.ArchivePath))
                {
                    Assert(diagnosticZip.GetEntry("diagnostic.json") != null, "diagnostic manifest included");
                    Assert(diagnosticZip.GetEntry("inventory/mod-inventory.json") != null, "diagnostic mod inventory included");
                    using (var reader = new StreamReader(diagnosticZip.GetEntry("logs/KSP.log").Open()))
                    {
                        var diagnosticLog = reader.ReadToEnd();
                        Assert(diagnosticLog.Contains("%KSP_ROOT%") && !diagnosticLog.Contains(diagnosticRoot), "diagnostic KSP path redacted");
                        Assert(diagnosticLog.Contains("[REDACTED_EMAIL]") && !diagnosticLog.Contains("person@example.com"), "diagnostic email redacted");
                        Assert(diagnosticLog.Contains("token=[REDACTED]") && !diagnosticLog.Contains("very-secret-value"), "diagnostic token redacted");
                    }
                    using (var reader = new StreamReader(diagnosticZip.GetEntry("config/settings.redacted.xml").Open()))
                    {
                        var settingsText = reader.ReadToEnd();
                        Assert(!settingsText.Contains("5293") && settingsText.Contains("[REDACTED]"), "diagnostic XML password redacted");
                    }
                }
                var escapedDiagnosticRejected = false;
                try
                {
                    ApfDiagnosticBundleBuilder.Build(diagnosticRoot, Path.Combine(root, "outside-diagnostic"), new ApfDiagnosticContext());
                }
                catch (InvalidDataException) { escapedDiagnosticRejected = true; }
                Assert(escapedDiagnosticRejected, "diagnostic output outside KSP root rejected");

                Console.WriteLine("APF core tests passed");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch { }
            }
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Failed: " + name);
        }
    }
}
