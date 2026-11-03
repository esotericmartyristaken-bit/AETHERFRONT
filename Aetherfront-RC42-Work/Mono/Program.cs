using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

internal static class Program
{
    public static int Main()
    {
        try
        {
            string binary=Environment.GetEnvironmentVariable("AF_TRANSPORT_PROBE_ASSEMBLY");
            bool patched=Boolean.Parse(Environment.GetEnvironmentVariable("AF_TRANSPORT_PROBE_PATCHED"));
            string expected=patched?"b8fa4194d427843007cfc40c8e64fa9e5c084910a4bfa9e55d16315faeba40b3":"4b57bce899c7ab814165cc9d46737122bb953bfc6f2bd353a03013458dc22bf3";
            using(var hash=SHA256.Create())using(var input=File.OpenRead(binary))if(BitConverter.ToString(hash.ComputeHash(input)).Replace("-","").ToLowerInvariant()!=expected)throw new InvalidDataException("Unexpected client transport hash");
            var run=ReflectionSuite.Run(Assembly.LoadFrom(Path.GetFullPath(binary)),patched,true);
            Console.WriteLine("SUMMARY mono "+(patched?"patched":"original")+": "+run.Passed+"/"+(run.Passed+run.Failed)+" cases; "+run.Assertions+" assertions; sockets=0");
            return run.Failed==0?0:1;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
