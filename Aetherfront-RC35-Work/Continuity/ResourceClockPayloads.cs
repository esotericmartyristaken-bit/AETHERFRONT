using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Aetherfront.Continuity
{
    // Bounded carrier attached to an exact resource DTO or interpolation object.
    // Keeping metadata with its packet avoids pairing queued resource values
    // with another packet's newer or older survival timestamp.
    public static class ResourceClockPayloads
    {
        public const int MaximumBytes=131072;
        private static readonly object Gate=new object();
        private static readonly ConditionalWeakTable<object,byte[]> Values=new ConditionalWeakTable<object,byte[]>();
        private static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);

        public static void Set(object message,byte[] value)
        {
            if(message==null)throw new ArgumentNullException(nameof(message));
            Validate(value);
            lock(Gate){Values.Remove(message);if(value.Length!=0)Values.Add(message,(byte[])value.Clone());}
        }
        public static byte[] Get(object message)
        {
            lock(Gate){byte[] value;return Values.TryGetValue(message,out value)?(byte[])value.Clone():new byte[0];}
        }
        public static int Size(object message)
        {
            lock(Gate){byte[] value;return 4+(Values.TryGetValue(message,out value)?value.Length:0);}
        }
        public static void Clear(object message){lock(Gate)Values.Remove(message);}

        public static void Validate(byte[] value)
        {
            if(value==null || value.Length>MaximumBytes)throw new InvalidDataException("Resource clock payload size refused");
            if(value.Length==0)return;
            string text;
            try{text=Utf8.GetString(value);}catch(DecoderFallbackException e){throw new InvalidDataException("Resource clock UTF-8 refused",e);}
            int depth=0,nodes=0,fields=0;bool nodePending=false;
            using(var reader=new StringReader(text))
            {
                string raw;
                while((raw=reader.ReadLine())!=null)
                {
                    if(raw.Length>1024)throw new InvalidDataException("Resource clock line too long");
                    foreach(char character in raw)if(char.IsControl(character)&&character!='\t')throw new InvalidDataException("Resource clock control character refused");
                    string line=raw.Trim();if(line.Length==0)continue;
                    if(line=="{")
                    {
                        if(!nodePending || ++depth>6 || ++nodes>4096)throw new InvalidDataException("Resource clock node nesting refused");
                        nodePending=false;continue;
                    }
                    if(line=="}")
                    {
                        if(nodePending || --depth<0)throw new InvalidDataException("Resource clock node balance refused");continue;
                    }
                    if(nodePending || line.IndexOf('{')>=0 || line.IndexOf('}')>=0)throw new InvalidDataException("Resource clock syntax refused");
                    int equal=line.IndexOf('=');
                    string name=equal<0?line:line.Substring(0,equal).Trim();
                    if(name.Length==0 || name.Length>64)throw new InvalidDataException("Resource clock key refused");
                    foreach(char character in name)if(!(character>='A'&&character<='Z'||character>='a'&&character<='z'||character>='0'&&character<='9'||character=='_'))throw new InvalidDataException("Resource clock key refused");
                    if(equal<0)nodePending=true;
                    else if(depth==0 || ++fields>16384 || line.Length-equal-1>512)throw new InvalidDataException("Resource clock fields refused");
                }
            }
            if(depth!=0 || nodePending || nodes==0)throw new InvalidDataException("Incomplete resource clock payload");
        }
    }
}
