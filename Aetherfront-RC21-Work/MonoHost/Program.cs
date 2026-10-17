using System;
using System.IO;
using System.Runtime.InteropServices;

internal static class Program
{
    [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool SetDllDirectory(string path);
    [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr LoadLibrary(string path);
    [DllImport("mono-2.0-bdwgc.dll",CallingConvention=CallingConvention.Cdecl)]private static extern void mono_set_dirs(string assembly,string config);
    [DllImport("mono-2.0-bdwgc.dll",CallingConvention=CallingConvention.Cdecl)]private static extern void mono_set_assemblies_path(string path);
    [DllImport("mono-2.0-bdwgc.dll",CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr mono_jit_init_version(string name,string version);
    [DllImport("mono-2.0-bdwgc.dll",CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr mono_domain_assembly_open(IntPtr domain,string path);
    [DllImport("mono-2.0-bdwgc.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int mono_jit_exec(IntPtr domain,IntPtr assembly,int argc,IntPtr argv);
    private static void Main(string[] args)
    {
        try {
            string game=Path.GetFullPath("Aetherfront-KSP-Smoke");string managed=Path.Combine(game,"KSP_x64_Data/Managed");
            string runtime=Path.Combine(game,"MonoBleedingEdge/EmbedRuntime");
            SetDllDirectory(runtime);if(LoadLibrary(Path.Combine(runtime,"mono-2.0-bdwgc.dll"))==IntPtr.Zero)throw new Exception("Mono load error "+Marshal.GetLastWin32Error());
            mono_set_dirs(managed,Path.Combine(game,"MonoBleedingEdge/etc"));
            mono_set_assemblies_path(managed+";"+Path.GetDirectoryName(Path.GetFullPath(args[0])));
            var domain=mono_jit_init_version("Aetherfront tests","v4.0.30319");if(domain==IntPtr.Zero)throw new Exception("Mono initialization failed");
            string target=Path.GetFullPath(args[0]);var assembly=mono_domain_assembly_open(domain,target);if(assembly==IntPtr.Zero)throw new Exception("Mono assembly load failed");
            var argument=Marshal.StringToHGlobalAnsi(target);var argv=Marshal.AllocHGlobal(IntPtr.Size);Marshal.WriteIntPtr(argv,argument);
            Environment.ExitCode=mono_jit_exec(domain,assembly,1,argv);
            Marshal.FreeHGlobal(argv);Marshal.FreeHGlobal(argument);
        }catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}
    }
}
