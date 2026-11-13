using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using ApfCore;
using KesasInstall;

class Program {
 static int checks;
 static void Check(bool value,string text){if(!value)throw new Exception(text);checks++;Console.WriteLine("PASS "+text);}
 static object Receipt(string root){return typeof(Installer).GetMethod("ReadCompletedReceipt",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{root});}
 static void Main(string[] args){
  if(args.Length==0 && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RC63_STARTUP_ROOT")))args=new[]{Environment.GetEnvironmentVariable("RC63_STARTUP_ROOT")};
  if(args.Length!=1)throw new ArgumentException("Expected isolated fixture root");
  string root=Path.GetFullPath(args[0]);if(Directory.Exists(root))throw new IOException("Preserve prior test fixture");Directory.CreateDirectory(root);
  Func<string,string,string> put=(p,s)=>{p=Path.Combine(root,p);Directory.CreateDirectory(Path.GetDirectoryName(p));File.WriteAllText(p,s);return p;};
  var engine=typeof(Installer).Assembly;
  Check(engine.GetType("KesasInstall.RuntimeAssemblyRepair",true)!=null,"RuntimeAssemblyRepair required by shipping continuity exists");
  Check(UserSettings.Paths.Length==16,"All sixteen preserved settings retained");
  using(var rsa=new RSACryptoServiceProvider(2048)){
   var manifest=new ApfPackManifest{PackVersion="kesas-rc63",RequiredClientVersion="kesas-rc63"};
   var catalog=new InstallCatalog{Version="kesas-rc63",Full=manifest,Potato=manifest};
   string catalogPath=put("GameData/LunaMultiplayer/Launcher/install-catalog.json",ApfJson.Serialize(catalog));
   var files=new List<ApfClientUpdateFile>();
   foreach(string name in new[]{"KesasInstall.dll","ApfCore.dll","LmpClient.dll"}){
    string path=Path.Combine(root,"GameData/LunaMultiplayer/Plugins",name);Directory.CreateDirectory(Path.GetDirectoryName(path));
    string source=name=="KesasInstall.dll"?engine.Location:Path.Combine(AppDomain.CurrentDomain.BaseDirectory,name);
    File.Copy(source,path);files.Add(new ApfClientUpdateFile{Path="GameData/LunaMultiplayer/Plugins/"+name,SizeBytes=new FileInfo(path).Length,Sha256=ApfJson.Sha256File(path)});
   }
   files.Add(new ApfClientUpdateFile{Path="GameData/LunaMultiplayer/Launcher/install-catalog.json",SizeBytes=new FileInfo(catalogPath).Length,Sha256=ApfJson.Sha256File(catalogPath)});
   var feed=new ApfClientUpdateManifest{Version="kesas-rc63",Files=files};ApfClientUpdateSecurity.Sign(feed,rsa);
   put("GameData/LunaMultiplayer/Data/APFClientUpdatePublicKey.xml",rsa.ToXmlString(false));
   put("APF/.client-update-staging/connect/feed.json",feed.ToJson());
   put("GameData/LunaMultiplayer/Data/KesasInstallProfile.json",ApfJson.Serialize(new Dictionary<string,string>{{"mode","full"},{"catalogVersion","kesas-rc63"}}));
   byte[] key=ApfTransactionSecurity.LoadOrCreateKey(Path.Combine(root,"GameData/LunaMultiplayer/Data/APFHelper.key"));
   var receipt=new Dictionary<string,string>{{"status","complete"},{"kind","pack"},{"schemaVersion","1"},{"mode","full"},{"clientVersion","kesas-rc63"},{"packVersion","kesas-rc63"},{"catalogVersion","kesas-rc63"},{"settingsPreservation","prepared30"}};
   string pathReceipt=Path.Combine(root,"APF/LastInstallResult.json");
   Action save=()=>{receipt.Remove("authorization");receipt["authorization"]=(string)typeof(Installer).GetMethod("ReceiptAuthorization",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{receipt,key});File.WriteAllText(pathReceipt,ApfJson.Serialize(receipt));};
   save();Check(Receipt(root)!=null,"Fresh test-only HMAC authenticates prepared30 receipt");
   string sentinel=put("GameData/BonVoyage/Plugins/PluginData/BonVoyage/config.xml","settings sentinel");
   PackageCasing.RestoreAtStartup(root,"kesas-rc63");
   Check(File.ReadAllText(sentinel)=="settings sentinel","Actual signed-catalog startup accepts existing prepared30 and preserves settings");
   LegacySettingsRecovery.Apply(root,manifest,receipt);
   Check(true,"Exact former Unknown settings preservation protocol path now succeeds");
   receipt["settingsPreservation"]="unknown-future";save();bool refused=false;
   try{LegacySettingsRecovery.Apply(root,manifest,receipt);}catch(InvalidDataException){refused=true;}
   Check(refused,"Unknown preservation protocols still refused, not blindly accepted");
   receipt["settingsPreservation"]="prepared30";save();
   receipt["authorization"]="tampered";File.WriteAllText(pathReceipt,ApfJson.Serialize(receipt));
   Check(Receipt(root)==null,"Tampered receipt MAC still refused");
  }
  Console.WriteLine("PASS "+checks+" installer startup regression cases. Ephemeral fixture keys only; no production state.");
 }
}
