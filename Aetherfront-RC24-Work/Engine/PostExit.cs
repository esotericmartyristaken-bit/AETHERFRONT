using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
            bool previous=KesasInstallPolicy.SuppressRelaunch;
            KesasInstallPolicy.SuppressRelaunch=true;
            try {ApfPostExitRunner.Apply(helperRoot,path,false);}
            finally {KesasInstallPolicy.SuppressRelaunch=previous;}
            string data=Path.Combine(helperRoot,"Data");
            string endpoint=File.ReadAllText(Path.Combine(data,"APFServer.txt")).Trim();
            int split=endpoint.LastIndexOf(':');
            var selected=new IPEndPoint(IPAddress.Parse(endpoint.Substring(0,split).Trim('[',']')),int.Parse(endpoint.Substring(split+1)));
            NativeInstall.Report("Applying the selected graphics settings...");
            new Installer(root,selected,transaction.Manifest.RequiredClientVersion).SetGraphics(mode);
            string profile=Path.Combine(data,"KesasInstallProfile.json");
            ApfPaths.EnsureNoReparsePoints(root,profile);
            Installer.AtomicText(profile,ApfJson.Serialize(new Dictionary<string,string>{{"mode",mode},{"catalogVersion",transaction.Manifest.PackVersion}}));
            // Relaunch is deliberately owned by the completion window, never automatic.
        }
    }
}
