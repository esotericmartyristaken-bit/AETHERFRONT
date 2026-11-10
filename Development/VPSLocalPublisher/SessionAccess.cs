using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

public static class AetherfrontPublisherSessionAccess
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Session { public int Id; public IntPtr Name; public int State; }
    [DllImport("wtsapi32.dll", SetLastError=true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
    [DllImport("wtsapi32.dll", SetLastError=true)]
    private static extern bool WTSQueryUserToken(uint session, out SafeAccessTokenHandle token);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [StructLayout(LayoutKind.Sequential, Pack=1)] private struct Privilege { public uint Count; public long Luid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError=true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool LookupPrivilegeValueW(string system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError=true)] private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref Privilege value, uint length, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError=true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, int length, out int returned);

    private static string LogonSid(SafeAccessTokenHandle token)
    {
        int length;
        GetTokenInformation(token, 28, IntPtr.Zero, 0, out length);
        if (length <= 0 || length > 65536) throw new Win32Exception(Marshal.GetLastWin32Error());
        var memory = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, 28, memory, length, out length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (Marshal.ReadInt32(memory) != 1) throw new InvalidDataException("Interactive logon SID is unavailable");
            var sid = new SecurityIdentifier(Marshal.ReadIntPtr(memory, IntPtr.Size == 8 ? 8 : 4)).Value;
            if (!sid.StartsWith("S-1-5-5-", StringComparison.Ordinal)) throw new InvalidDataException("Unexpected Windows logon SID");
            return sid;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    public static string CurrentLogonSid()
    {
        SafeAccessTokenHandle token;
        if (!OpenProcessToken(GetCurrentProcess(), 8, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token) return LogonSid(token);
    }

    // Grant approval-key read access to real interactive logon SIDs, rather than
    // the user SID also present in the limited S4U bot token.
    public static string[] InteractiveLogons(string operatorSid)
    {
        if (WindowsIdentity.GetCurrent().User.Value != "S-1-5-18") throw new UnauthorizedAccessException("Session discovery requires SYSTEM");
        SafeAccessTokenHandle processToken;
        if (!OpenProcessToken(GetCurrentProcess(), 0x28, out processToken)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (processToken)
        {
            long luid;
            if (!LookupPrivilegeValueW(null, "SeTcbPrivilege", out luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var privilege = new Privilege { Count=1, Luid=luid, Attributes=2 };
            if (!AdjustTokenPrivileges(processToken, false, ref privilege, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error()!=0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        IntPtr entries; int count;
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out entries, out count)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (count < 0 || count > 1024) throw new InvalidDataException("Unexpected Windows session count");
            int size = Marshal.SizeOf<Session>();
            for (int i=0;i<count;i++)
            {
                var session = Marshal.PtrToStructure<Session>(IntPtr.Add(entries, i*size));
                if (session.Id <= 0 || (session.State != 0 && session.State != 4)) continue;
                SafeAccessTokenHandle token;
                if (!WTSQueryUserToken((uint)session.Id, out token)) continue;
                using (token)
                using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                    if (identity.User.Value == operatorSid)
                        result.Add(LogonSid(token));
            }
        }
        finally { WTSFreeMemory(entries); }
        var values = new string[result.Count]; result.CopyTo(values); return values;
    }

    // Excluding FILE_SHARE_DELETE pins directory names during a privileged job;
    // normal writes to unrelated files and game state remain possible.
    public static IDisposable PinAncestors(string[] paths)
    {
        var held = new Handles(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string path in paths)
                for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory != null; directory = directory.Parent)
                {
                    if (!seen.Add(directory.FullName)) continue;
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Pinned path is a reparse point");
                    var handle = CreateFileW(directory.FullName, 0x80, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                    if (handle.IsInvalid) { int error=Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
                    held.Values.Add(handle);
                }
            return held;
        }
        catch { held.Dispose(); throw; }
    }
    private sealed class Handles : IDisposable
    {
        public readonly List<SafeFileHandle> Values = new List<SafeFileHandle>();
        public void Dispose() { foreach (var value in Values) value.Dispose(); Values.Clear(); }
    }
}
