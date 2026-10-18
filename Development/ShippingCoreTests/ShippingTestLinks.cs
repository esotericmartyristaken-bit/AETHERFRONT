using System;
using System.IO;
using System.Diagnostics;
using System.Text;
// Test-only junction adapter for a .NET 6 API unavailable on KSP's .NET Framework.
internal static class ShippingTestLinks {
 public static void Create(string link,string target) {
  string script="$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:APF_TEST_LINK -Value $env:APF_TEST_TARGET | Out-Null";
  var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe"),"-NoProfile -NonInteractive -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(script))) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardError=true, RedirectStandardOutput=true };
  start.EnvironmentVariables["APF_TEST_LINK"]=link;
  start.EnvironmentVariables["APF_TEST_TARGET"]=target;
  using(var process=Process.Start(start)) {
   string error=process.StandardError.ReadToEnd();
   if(!process.WaitForExit(10000)||process.ExitCode!=0)throw new IOException("Test junction creation unavailable: "+error);
  }
 }
}
