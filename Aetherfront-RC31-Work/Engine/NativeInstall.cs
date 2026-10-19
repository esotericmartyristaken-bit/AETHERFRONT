using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using ApfCore;

namespace KesasInstall
{
    // Used only by the post-exit helper. In-game callers keep the original core behavior.
    public static class NativeInstall
    {
        private static volatile bool enabled;
        private static string root;
        private static string status="Waiting for KSP to close...";
        private static readonly List<FileStream> executionLocks=new List<FileStream>();
        private static readonly HashSet<string> removals=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int packageIndex,packageCount,busy;
        public static string Status { get { return Volatile.Read(ref status); } }
        public static void Report(string value) { Volatile.Write(ref status,value); }
        public static bool BlockAutomaticRestart() { return enabled; }

        public static Completion Run(string helperRoot,string transactionPath,string kind)
        {
            if(Interlocked.CompareExchange(ref busy,1,0)!=0)throw new InvalidOperationException("An installation is already active.");
            Completion completion=null;bool succeeded=false;
            try {
                helperRoot=Path.GetFullPath(helperRoot);
                root=Directory.GetParent(Directory.GetParent(helperRoot).FullName).FullName;
                if(!string.Equals(helperRoot,Path.Combine(root,"GameData","LunaMultiplayer"),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Invalid helper location.");
                ApfPaths.EnsureNoReparsePoints(root,helperRoot);
                ApfPaths.EnsureNoReparsePoints(root,transactionPath);
                enabled=true;removals.Clear();packageIndex=packageCount=0;
                WriteResult("applying",kind);
                Report("Waiting for KSP to close. Do not start another KSP instance.");
                if(kind=="rollback") {
                    // The core restore entry point refuses all live KSP instances.
                    AcquireExecutionLocks(Path.Combine(root,"KSP_x64.exe"));
                    Report("Restoring the previous installation...");
                    ApfPostExitRunner.Restore(helperRoot,transactionPath);
                    completion=new Completion(null,root);
                } else {
                    string expected=Path.Combine(root,"APF",kind=="client"?".client-update-staging":".staging");
                    if((kind!="pack"&&kind!="client")||!ApfPaths.IsUnder(transactionPath,expected)||new FileInfo(transactionPath).Length>16L*1024*1024)throw new InvalidDataException("Invalid installation transaction.");
                    // The core independently authenticates, consumes and validates this request.
                    object transaction=kind=="client"?(object)ApfJson.Deserialize<ApfClientUpdateTransaction>(File.ReadAllText(transactionPath)):ApfJson.Deserialize<ApfSyncTransaction>(File.ReadAllText(transactionPath));
                    byte[] key=ApfTransactionSecurity.LoadKey(Path.Combine(helperRoot,"Data","APFHelper.key"));
                    bool authenticated=transaction is ApfClientUpdateTransaction client?ApfTransactionSecurity.Verify(client,key):ApfTransactionSecurity.Verify((ApfSyncTransaction)transaction,key);
                    if(!authenticated)throw new InvalidDataException("Installation authorization failed.");
                    completion=new Completion(transaction,root);
                    if(transaction is ApfSyncTransaction prepared){PreparedPack.Open(prepared,transactionPath);PreparedPack.ReleaseDirectories();}
                    if(kind=="client")ApfPostExitRunner.Apply(helperRoot,transactionPath,true);
                    else PostExit.Apply(helperRoot,transactionPath);
                }
                if(kind!="pack"||!File.Exists(Path.Combine(helperRoot,"Launcher","install-catalog.json"))||!File.Exists(Path.Combine(root,"APF",".client-update-staging","connect","feed.json")))WriteResult("complete",kind);
                succeeded=true;
                Report(kind=="rollback"?"Restoration complete. KSP remains closed.":"Installation verified.");
                return completion;
            } catch(Exception error) {
                Report("Installation failed. KSP was not restarted. See APF/APFUpdater.log.");
                if(root!=null)try{WriteResult("failed",kind);string log=Path.Combine(root,"APF","APFUpdater.log");ApfPaths.EnsureNoReparsePoints(root,log);File.AppendAllText(log,DateTime.UtcNow.ToString("o")+" "+error+Environment.NewLine);}catch{}
                ReleaseExecutionLocks();throw;
            } finally {PreparedPack.Close();enabled=false;removals.Clear();if(!succeeded)Interlocked.Exchange(ref busy,0);}
        }
        private static void WriteResult(string value,string kind)
        {
            string path=Path.Combine(root,"APF","LastInstallResult.json");ApfPaths.EnsureNoReparsePoints(root,path);
            Installer.AtomicText(path,ApfJson.Serialize(new Dictionary<string,string>{{"status",value},{"kind",kind},{"updatedUtc",DateTime.UtcNow.ToString("o")}}));
        }

        // Called only after the authenticated runner has waited for the original process.
        public static void AcquireExecutionLocks(string executable)
        {
            if(!enabled)return;
            string expected=Path.Combine(root,"KSP_x64.exe");
            if(!string.Equals(Path.GetFullPath(executable),expected,StringComparison.OrdinalIgnoreCase)&&!string.Equals(Path.GetFullPath(executable),Path.Combine(root,"KSP.exe"),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unexpected KSP executable.");
            if(executionLocks.Count>0)return;
            EnsureNoRunningGame();
            try {
                foreach(string name in new[]{"KSP_x64.exe","KSP.exe"}) {
                    string path=Path.Combine(root,name);ApfPaths.EnsureNoReparsePoints(root,path);
                    if(File.Exists(path))executionLocks.Add(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None));
                }
                // Close the enumeration/open race: a process started just before
                // the executable lock must also be rejected before any mutation.
                EnsureNoRunningGame();
            } catch {ReleaseExecutionLocks();throw;}
            Report("KSP is closed. Preparing and validating the installation...");
        }
        private static void EnsureNoRunningGame()
        {
            foreach(var process in Process.GetProcessesByName("KSP_x64").Concat(Process.GetProcessesByName("KSP")))using(process) {
                try {
                    if(process.HasExited)continue;
                    var module=process.MainModule;
                    if(module==null) {
                        if(ProcessExited(process))continue;
                        throw new IOException("Could not verify a running KSP process. Close it before installing.");
                    }
                    string path=module.FileName;
                    if(string.IsNullOrEmpty(path)) {
                        if(ProcessExited(process))continue;
                        throw new IOException("Could not verify a running KSP process. Close it before installing.");
                    }
                    if(ApfPaths.IsUnder(path,root))throw new IOException("Close every KSP instance from this game folder before installing.");
                }catch(Exception error) when(error is InvalidOperationException || error is System.ComponentModel.Win32Exception || error is NullReferenceException) {
                    // A different KSP process may exit between enumeration and
                    // MainModule lookup (including Mono's null module race).
                    // An unreadable LIVE process still blocks installation.
                    if(ProcessExited(process))continue;
                    throw new IOException("Could not verify a running KSP process. Close it before installing.",error);
                }
            }
        }
        private static bool ProcessExited(Process process)
        {
            try{process.Refresh();return process.HasExited;}
            catch(InvalidOperationException){return true;}
            catch(System.ComponentModel.Win32Exception){return false;}
        }
        public static void BeginPack(ApfSyncTransaction transaction)
        {
            if(!enabled)return;
            if(!string.Equals(Path.GetFullPath(transaction.GameRoot),root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Pack root differs from the helper root.");
            removals.Clear();packageCount=transaction.Packages.Count;packageIndex=0;
            foreach(var deletion in transaction.Deletes) {
                string target=Path.GetFullPath(Path.Combine(root,"GameData",ApfPaths.NormalizeGameDataRelative(deletion.RelativePath).Replace('/',Path.DirectorySeparatorChar)));
                if(transaction.Packages.Any(p=>string.Equals(target,Path.GetFullPath(Path.Combine(root,"GameData",ApfPaths.PackageRoot(p.Package).Replace('/',Path.DirectorySeparatorChar))),StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("A package cannot be replaced and permanently removed in the same operation.");
                ValidateRemoval(target,Path.Combine(root,"GameData"));removals.Add(target);
            }
            if(executionLocks.Count==0)throw new InvalidOperationException("KSP must be closed and locked before preserving settings.");
            PreparedPack.RefreshUserSettings(transaction);
            PreparedPack.CommitClient();
            PreparedPack.DisableDuplicate();
            string obsolete=Path.Combine(root,"GameData","LunaMultiplayer","Launcher","AetherfrontInstallUI.bin");
            ApfPaths.EnsureNoReparsePoints(root,obsolete);
            if(File.Exists(obsolete)) {
                string destination=Path.Combine(root,"APF",".obsolete-client-files",Guid.NewGuid().ToString("N"),"AetherfrontInstallUI.bin");
                ApfPaths.EnsureNoReparsePoints(root,destination);Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Move(obsolete,destination);
            }
        }
        public static ApfRecoveryJournal CreateRecovery(string gameRoot,string directory,IEnumerable<string> targets,string kind)
        {
            // Removed mods are deliberately not recoverable. Replacement files retain recovery.
            var selected=enabled&&kind=="pack"?targets.Where(p=>string.IsNullOrWhiteSpace(p)||!removals.Contains(Path.GetFullPath(p))):targets;
            return ApfRecoveryStore.Create(gameRoot,directory,selected,kind);
        }
        public static bool RemoveApproved(string target,string gameData)
        {
            if(!enabled||!removals.Contains(Path.GetFullPath(target)))return false;
            ValidateRemoval(target,gameData);
            if(Directory.Exists(target)){ValidateTree(target);Directory.Delete(target,true);}
            else if(File.Exists(target))File.Delete(target);
            return true;
        }
        private static void ValidateRemoval(string target,string gameData)
        {
            string expected=Path.Combine(root,"GameData");
            if(!string.Equals(Path.GetFullPath(gameData),expected,StringComparison.OrdinalIgnoreCase)||!ApfPaths.IsUnder(target,expected)||string.Equals(target,expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Removal escapes GameData.");
            string relative=target.Substring(expected.Length+1).Replace('\\','/');
            if(ApfPaths.IsBuiltInProtectedTopLevel(relative.Split('/')[0].ToLowerInvariant()))throw new InvalidDataException("Protected removal target.");
            ApfPaths.EnsureNoReparsePoints(root,target);
        }
        private static void ValidateTree(string target)
        {
            ApfPaths.EnsureNoReparsePoints(root,target);
            foreach(string entry in Directory.GetFileSystemEntries(target)) {
                ApfPaths.EnsureNoReparsePoints(root,entry);
                if(Directory.Exists(entry))ValidateTree(entry);
            }
        }
        public static void Extracting(ApfPackage package)
        {
            if(enabled)Report("Installing package "+(++packageIndex)+" / "+packageCount+": "+ApfPaths.PackageRoot(package));
        }
        public static void Verifying() {if(enabled)Report("Verifying all installed package files. Keep KSP closed...");}
        private static void ReleaseExecutionLocks() {foreach(var input in executionLocks)input.Dispose();executionLocks.Clear();}

        public sealed class Completion:IDisposable
        {
            private readonly object transaction;
            private readonly string gameRoot;
            private bool disposed,restarted;
            public int RestartedProcessId {get;private set;}
            internal Completion(object value,string path){transaction=value;gameRoot=path;}
            public void Restart()
            {
                if(disposed||restarted||transaction==null)throw new InvalidOperationException("This installation cannot restart KSP.");
                // This object is returned only after core verification and graphics/profile commit.
                string executable;List<string> arguments;
                if(transaction is ApfClientUpdateTransaction client){executable=client.KspExecutable;arguments=client.KspArguments;}
                else {var pack=(ApfSyncTransaction)transaction;executable=pack.KspExecutable;arguments=pack.KspArguments;}
                executable=Path.GetFullPath(executable);
                if(!string.Equals(Path.GetDirectoryName(executable),gameRoot,StringComparison.OrdinalIgnoreCase)||!(string.Equals(Path.GetFileName(executable),"KSP_x64.exe",StringComparison.OrdinalIgnoreCase)||string.Equals(Path.GetFileName(executable),"KSP.exe",StringComparison.OrdinalIgnoreCase))||!File.Exists(executable))throw new InvalidDataException("Invalid KSP restart target.");
                ApfPaths.EnsureNoReparsePoints(gameRoot,executable);
                ReleaseExecutionLocks();
                try{using(var child=Process.Start(new ProcessStartInfo{FileName=executable,WorkingDirectory=gameRoot,UseShellExecute=false,Arguments=ApfProcessArguments.Join(arguments??new List<string>())})){if(child==null)throw new IOException("KSP did not start.");RestartedProcessId=child.Id;}restarted=true;}
                catch(Exception error){string log=Path.Combine(gameRoot,"APF","APFUpdater.log");ApfPaths.EnsureNoReparsePoints(gameRoot,log);File.AppendAllText(log,DateTime.UtcNow.ToString("o")+" Restart failed: "+error+Environment.NewLine);throw;}
            }
            public void Dispose(){if(disposed)return;ReleaseExecutionLocks();disposed=true;Interlocked.Exchange(ref busy,0);}
        }
    }
}
