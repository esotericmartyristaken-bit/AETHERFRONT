using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using ApfCore;

namespace KesasInstall
{
    public static class PostExit
    {
        public static void Apply(string helperRoot,string path)
        {
            string root=Directory.GetParent(Directory.GetParent(Path.GetFullPath(helperRoot)).FullName).FullName;
            ApfPaths.EnsureNoReparsePoints(root,path);
            if(!ApfPaths.IsUnder(path,Path.Combine(root,"APF",".staging")) || new FileInfo(path).Length>16L*1024*1024)throw new InvalidDataException("Invalid pack transaction location or size.");
            var transaction=ApfJson.Deserialize<ApfSyncTransaction>(File.ReadAllText(path));
            if(transaction==null || !ApfTransactionSecurity.Verify(transaction,ApfTransactionSecurity.LoadKey(Path.Combine(helperRoot,"Data","APFHelper.key"))))throw new InvalidDataException("Pack authorization failed.");
            string mode=transaction.Manifest.ManagedPackages.Any(p=>string.Equals(ApfPaths.PackageRoot(p),"ZZZ_KESASPotato",StringComparison.OrdinalIgnoreCase))?"potato":"full";
            KesasInstallPolicy.SuppressRelaunch=true;
            try {ApfPostExitRunner.Apply(helperRoot,path,false);}
            finally {KesasInstallPolicy.SuppressRelaunch=false;}
            string data=Path.Combine(helperRoot,"Data");
            string endpoint=File.ReadAllText(Path.Combine(data,"APFServer.txt")).Trim();
            int split=endpoint.LastIndexOf(':');
            var selected=new IPEndPoint(IPAddress.Parse(endpoint.Substring(0,split).Trim('[',']')),int.Parse(endpoint.Substring(split+1)));
            new Installer(root,selected,transaction.Manifest.RequiredClientVersion).SetGraphics(mode);
            string profile=Path.Combine(data,"KesasInstallProfile.json");
            ApfPaths.EnsureNoReparsePoints(root,profile);
            Installer.AtomicText(profile,ApfJson.Serialize(new Dictionary<string,string>{{"mode",mode},{"catalogVersion",transaction.Manifest.PackVersion}}));
            // Reuse the original validated restart target and argument quoting.
            var restart=typeof(ApfArchiveInstaller).GetMethod("Relaunch",BindingFlags.Static|BindingFlags.NonPublic,null,new[]{typeof(ApfSyncTransaction)},null);
            if(restart==null)throw new InvalidDataException("The installed updater cannot restart this KSP copy.");
            restart.Invoke(null,new object[]{transaction});
        }
    }
}
