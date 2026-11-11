using System;
using System.IO;
using System.Runtime.InteropServices;
class Program
{
 [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string path);
 [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string path);
 [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)] static extern void mono_set_dirs(string assemblies, string config);
 [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)] static extern void mono_set_assemblies_path(string path);
 [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mono_jit_init_version(string name, string version);
 [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mono_domain_assembly_open(IntPtr domain, string path);
 [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)] static extern int mono_jit_exec(IntPtr domain, IntPtr assembly, int count, IntPtr args);
 static void Main(string[] args)
 {
  string game = Path.GetFullPath(Environment.GetEnvironmentVariable("RC56_KSP_REFERENCE") ?? "");
  string fixture = Path.GetFullPath(Environment.GetEnvironmentVariable("RC56_MONO_ROOT") ?? "");
  if (game != @"C:\Users\Administrator\Desktop\APF\Aetherfront-KSP-Smoke" ||
   !fixture.StartsWith(@"C:\Users\Administrator\Desktop\APF\UpdateBuilds\RC56-Sync-Fixture\", StringComparison.OrdinalIgnoreCase)) throw new Exception("Isolated fixture and read-only KSP reference required.");
  string managed = Path.Combine(game, "KSP_x64_Data/Managed"), runtime = Path.Combine(game, "MonoBleedingEdge/EmbedRuntime"), target = Path.GetFullPath(args[0]);
  if (!target.StartsWith(fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Test executable must stay in its fixture.");
  SetDllDirectory(runtime); if (LoadLibrary(Path.Combine(runtime, "mono-2.0-bdwgc.dll")) == IntPtr.Zero) throw new Exception("Mono load failed.");
  mono_set_dirs(managed, Path.Combine(game, "MonoBleedingEdge/etc")); mono_set_assemblies_path(managed + ";" + fixture);
  var domain = mono_jit_init_version("RC56 isolated synchronization checks", "v4.0.30319"); var assembly = mono_domain_assembly_open(domain, target);
  if (domain == IntPtr.Zero || assembly == IntPtr.Zero) throw new Exception("Mono initialization failed.");
  var text = Marshal.StringToHGlobalAnsi(target); var argv = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(argv, text);
  Environment.ExitCode = mono_jit_exec(domain, assembly, 1, argv);
 }
}
