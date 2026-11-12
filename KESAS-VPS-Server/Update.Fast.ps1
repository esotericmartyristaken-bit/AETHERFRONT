# Loaded only by reviewed PowerShell 7 deployment code. No persistent hash cache.
function Initialize-UpdateFast {
    if('Aetherfront.Deployment.FastChecks' -as [type]){return}
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Aetherfront.Deployment {
 public static class FastChecks {
  static readonly Regex Invalid = new Regex("[<>:\"|?*\\x00-\\x1f]|[. ]$", RegexOptions.Compiled);
  static readonly Regex Device = new Regex(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.Compiled|RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
  public static string Relative(string name) {
   if(string.IsNullOrWhiteSpace(name)||name.Contains('\\')||name.StartsWith('/'))throw new IOException("Unsafe archive/release path: "+name);
   foreach(string segment in name.Split('/'))if(segment.Length==0||segment=="."||segment==".."||Invalid.IsMatch(segment)||Device.IsMatch(segment))throw new IOException("Unsafe archive/release path: "+name);
   return name;
  }
  public static string PathChecked(string path) {
   string full=Path.GetFullPath(path), root=Path.GetPathRoot(full), tail=full.Substring(root.Length).Replace('\\','/').TrimEnd('/');
   if(tail.Length!=0)Relative(tail);
   for(string cursor=full;!string.IsNullOrEmpty(cursor);cursor=Path.GetDirectoryName(cursor)) {
    FileAttributes attributes=0;
    try{attributes=File.GetAttributes(cursor);}catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
    if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Refusing reparse point: "+cursor);
   }
   return full;
  }
  public static void UniqueJson(string text) {
   using(var document=JsonDocument.Parse(text,new JsonDocumentOptions{MaxDepth=1024})) {
    var pending=new Stack<JsonElement>();pending.Push(document.RootElement);
    while(pending.Count!=0) {
     var item=pending.Pop();
     if(item.ValueKind==JsonValueKind.Object) {
      var names=new HashSet<string>(StringComparer.Ordinal);
      foreach(var property in item.EnumerateObject()){if(!names.Add(property.Name))throw new IOException("Duplicate release JSON property");pending.Push(property.Value);}
     } else if(item.ValueKind==JsonValueKind.Array)foreach(var value in item.EnumerateArray())pending.Push(value);
    }
   }
  }
  public static string HashShared(string path) {
   using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,1048576,FileOptions.SequentialScan))
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
  }
 }
}
'@
}
