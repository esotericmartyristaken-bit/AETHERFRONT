using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Aetherfront.Core;
using LmpClient;
using LmpClient.Aetherfront;
using UnityEngine;

namespace Aetherfront.Personal.Client
{
    public static class ResearchSync
    {
        private static readonly ResearchQueue queue = new ResearchQueue();
        private static string displayedWarning;
        sealed class SavedContext
        {
            public string ServerId { get; set; }
            public string AccountId { get; set; }
            public ResearchQueue.Intent[] Intents { get; set; }
        }
        sealed class Journal
        {
            public int Version { get; set; }=1;
            public List<SavedContext> Contexts { get; set; }=new List<SavedContext>();
        }
        static Journal journal;
        static string loadedServer,loadedAccount,lastSaved;
        static bool storageFailed;
        static long savedRevision=-1;
        static string savedServer,savedAccount;
        static double retryStorageAt;
        static double Now => (double)System.Diagnostics.Stopwatch.GetTimestamp()/System.Diagnostics.Stopwatch.Frequency;
        static string JournalPath => Path.Combine(KSPUtil.ApplicationRootPath,"APF","PendingResearch-v1.json");
        public static string Status()
        {
            return storageFailed?"Research waiting for local save recovery; pending purchases retained, automatic retry enabled.":queue.Warning ?? (queue.Count > 0 ? "Research sync: " + queue.Count + " purchase(s) awaiting server confirmation." : string.Empty);
        }
        private static void Context()
        {
            string server=AetherfrontClientSystem.State.ServerId,account=MainSystem.UniqueIdentifier;
            bool active=PersonalClient.Active;
            queue.Context(server,account,active);
            if(storageFailed&&Now<retryStorageAt)return;
            if(!active||loadedServer==server&&loadedAccount==account)return;
            try
            {
                if(journal==null)
                {
                    journal=new Journal();string path=JournalPath;CheckPath(path);
                    if(File.Exists(path))
                    {
                        if(new FileInfo(path).Length>4*1024*1024)throw new InvalidDataException("Saved research intents exceed the safety limit.");
                        journal=AetherfrontJson.Deserialize<Journal>(File.ReadAllText(path));
                        if(journal==null||journal.Version!=1||journal.Contexts==null||journal.Contexts.Count>16||journal.Contexts.Any(x=>x==null||string.IsNullOrWhiteSpace(x.ServerId)||string.IsNullOrWhiteSpace(x.AccountId))||
                            journal.Contexts.Select(x=>x.ServerId+"\n"+x.AccountId).Distinct(StringComparer.Ordinal).Count()!=journal.Contexts.Count)throw new InvalidDataException("Saved research context is invalid.");
                    }
                }
                var saved=journal.Contexts.SingleOrDefault(x=>x.ServerId==server&&x.AccountId==account);
                if(saved!=null)queue.Restore(saved.Intents);
                loadedServer=server;loadedAccount=account;storageFailed=false;
            }
            catch(Exception error){journal=null;storageFailed=true;retryStorageAt=Now+10;Debug.LogError("[Aetherfront] Pending research could not be restored; original file retained: "+error);}
        }
        static void CheckPath(string path)
        {
            string root=Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!Path.GetFullPath(path).StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Research journal escapes game root.");
            for(string item=path;!string.IsNullOrEmpty(item);item=Path.GetDirectoryName(item))
            {if((File.Exists(item)||Directory.Exists(item))&&(File.GetAttributes(item)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Research journal path must not contain links.");if(item.TrimEnd(Path.DirectorySeparatorChar)==root.TrimEnd(Path.DirectorySeparatorChar))break;}
        }
        static bool Save()
        {
            if(journal==null||loadedServer==null)return false;
            if(storageFailed&&Now<retryStorageAt)return false;
            if(!storageFailed&&savedRevision==queue.Revision&&savedServer==loadedServer&&savedAccount==loadedAccount)return true;
            string temporary=null;
            try
            {
                var saved=journal.Contexts.SingleOrDefault(x=>x.ServerId==loadedServer&&x.AccountId==loadedAccount);
                if(saved==null){if(journal.Contexts.Count==16)throw new InvalidDataException("Research journal context limit reached; existing records retained.");saved=new SavedContext{ServerId=loadedServer,AccountId=loadedAccount};journal.Contexts.Add(saved);}
                saved.Intents=queue.Export();string text=AetherfrontJson.Serialize(journal);
                if(text==lastSaved){savedRevision=queue.Revision;savedServer=loadedServer;savedAccount=loadedAccount;storageFailed=false;return true;}
                var bytes=new UTF8Encoding(false).GetBytes(text);if(bytes.Length>4*1024*1024)throw new InvalidDataException("Research journal exceeds safety limit.");
                string path=JournalPath;CheckPath(path);Directory.CreateDirectory(Path.GetDirectoryName(path));
                temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
                if(File.Exists(path))File.Replace(temporary,path,path+".previous");else File.Move(temporary,path);
                lastSaved=text;savedRevision=queue.Revision;savedServer=loadedServer;savedAccount=loadedAccount;storageFailed=false;return true;
            }
            catch(Exception error){storageFailed=true;retryStorageAt=Now+10;Debug.LogError("[Aetherfront] Research intent not durably saved; no new request sent: "+error);return false;}
            finally{if(temporary!=null&&File.Exists(temporary))try{File.Delete(temporary);}catch{}}
        }
        public static HashSet<string> Project(AetherfrontState state, PlayerProgression player, out decimal science)
        {
            Context(); return queue.Project(state, player, out science);
        }
        public static void Request(string technology)
        {
            Context();
            if(storageFailed){ScreenMessages.PostScreenMessage(Status(),8,ScreenMessageStyle.UPPER_CENTER);PersonalClient.Apply();return;}
            if (!PersonalClient.CanRequest(technology)) return;
            var definition=AetherfrontClientSystem.State.FindTechnology(technology);
            if(definition==null)return;
            queue.Enqueue(technology,definition.ScienceCost);
            if(!Save()){ScreenMessages.PostScreenMessage("Research pending list could not be saved. No new purchase sent; see KSP.log.",8,ScreenMessageStyle.UPPER_CENTER);return;}
            Tick(); PersonalClient.Apply();
        }
        public static void Tick()
        {
            Context();
            if (!PersonalClient.Active) { displayedWarning = null; return; }
            if(storageFailed){if(journal!=null&&Now>=retryStorageAt)Save();if(storageFailed)return;}
            int before = queue.Count;
            string previousWarning = queue.Warning;
            var request = queue.Step(AetherfrontClientSystem.State, MainSystem.UniqueIdentifier,
                Time.realtimeSinceStartup, AetherfrontClientSystem.LastSnapshotUtc.Ticks);
            if (queue.SnapshotNeeded) AetherfrontClientSystem.RequestSnapshot();
            if(!Save())return;
            if (request != null) AetherfrontClientSystem.SendCommand(request);
            if (before != queue.Count || previousWarning != queue.Warning) PersonalClient.Apply();
            if (queue.Warning != displayedWarning)
            {
                displayedWarning = queue.Warning;
                if (!string.IsNullOrEmpty(displayedWarning))
                {
                    Debug.LogWarning("[Aetherfront] " + displayedWarning);
                    ScreenMessages.PostScreenMessage(displayedWarning, 8, ScreenMessageStyle.UPPER_CENTER);
                }
            }
        }
        public static void DrawStatus()
        {
            string status = Status(); if (!string.IsNullOrEmpty(status)) GUILayout.Label(status);
        }
    }
}
