using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ApfCore;

namespace KesasInstall
{
    // Terrain geometry is shared multiplayer state, independent of graphics mode.
    internal static class TerrainPolicy
    {
        internal static string HighSettings(string text)
        {
            char[] mask=text.ToCharArray(); bool quoted=false,comment=false,escaped=false;
            for(int i=0;i<mask.Length;i++) {
                char c=text[i];
                if(c=='\r'||c=='\n'){comment=false;continue;}
                if(comment){mask[i]=' ';continue;}
                if(quoted){mask[i]=' ';if(escaped){escaped=false;continue;}if(c=='\\'){escaped=true;continue;}if(c=='"')quoted=false;continue;}
                if(c=='"'){quoted=true;mask[i]=' ';continue;}
                if(c=='/'&&i+1<mask.Length&&text[i+1]=='/'){comment=true;mask[i]=' ';}
            }
            string code=new string(mask); int[] depth=new int[mask.Length+1];
            for(int i=0;i<mask.Length;i++)depth[i+1]=depth[i]+(mask[i]=='{'?1:mask[i]=='}'?-1:0);
            var nodes=Regex.Matches(code,@"(?m)^[ \t]*TERRAIN[ \t\r\n]*\{").Cast<Match>().Where(m=>depth[m.Index]==0).ToArray();
            // Headless helpers may see a minimal/missing settings file. KSP creates
            // its complete stock presets; the authenticated startup hook handles it.
            if(nodes.Length==0)return text;
            if(nodes.Length!=1||depth[mask.Length]!=0)throw new InvalidDataException("Ambiguous terrain settings; original file preserved.");
            int start=nodes[0].Index+nodes[0].Length,end=start;
            while(end<mask.Length&&depth[end]>0)end++;
            if(end==mask.Length)throw new InvalidDataException("Unclosed terrain settings.");
            var values=Regex.Matches(code.Substring(start,end-start),@"(?m)^[ \t]*preset[ \t]*=[ \t]*(?<value>[^\r\n]*)").Cast<Match>().Where(m=>depth[start+m.Index]==1).ToArray();
            if(values.Length!=1)throw new InvalidDataException("Terrain preset is missing or duplicated; original file preserved.");
            var value=values[0].Groups["value"];int left=start+value.Index,right=left+value.Value.TrimEnd().Length;
            if(right==left)throw new InvalidDataException("Terrain preset is empty.");
            return text.Substring(0,left)+"High"+text.Substring(right);
        }

        internal static void AtStartup(string root)
        {
            string path=Path.Combine(root,"settings.cfg");ApfPaths.EnsureNoReparsePoints(root,path);
            if(File.Exists(path)&&new FileInfo(path).Length>8*1024*1024)throw new InvalidDataException("Game settings exceed terrain policy limit.");
            string original=File.Exists(path)?File.ReadAllText(path):"",updated=HighSettings(original);
            var game=AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a=>a.GetName().Name=="Assembly-CSharp");
            object list=null;MethodInfo select=null;
            if(game!=null) {
                var cache=game.GetType("PQSCache",true);list=cache.GetField("PresetList",BindingFlags.Public|BindingFlags.Static).GetValue(null);
                if(list==null)throw new InvalidDataException("KSP terrain presets are not initialized.");
                var presets=(IList)list.GetType().GetField("presets").GetValue(list);
                if(presets.Count<=2||(string)presets[2].GetType().GetField("name").GetValue(presets[2])!="High")throw new InvalidDataException("KSP High terrain preset does not match the multiplayer index.");
                // KSP's string overload can retain the old index. Select by the
                // checked index so the name, geometry and handshake all agree.
                select=list.GetType().GetMethod("SetPreset",new[]{typeof(int)});
            }
            if(updated!=original)Installer.AtomicText(path,updated);
            if(list!=null) {
                select.Invoke(list,new object[]{2});
                if((int)list.GetType().GetField("presetIndex").GetValue(list)!=2||(string)list.GetType().GetField("preset").GetValue(list)!="High")throw new InvalidDataException("KSP terrain policy did not apply.");
            }
        }
    }
}
