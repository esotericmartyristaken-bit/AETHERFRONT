using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using KesasInstall;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    private const uint FileWriteAttributes = 0x100;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint OpenDirectoryItself = 0x02200000;
    private const uint SetReparsePoint = 0x000900a4;
    private const uint MountPointTag = 0xa0000003;
    private const uint ReparseAttribute = 0x400;
    private const string Work = @".\Aetherfront-RC25-Work";
    private static readonly MethodInfo PlainDirectory = typeof(PreparedPack).GetMethod("PlainDirectory", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly FieldInfo DirectoryLocks = typeof(PreparedPack).GetField("directoryLocks", BindingFlags.Static | BindingFlags.NonPublic);
    private static string fixture;
    private static int checks;
    private static int failures;

    private static int Main()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows-only lease probe.");
            if (PlainDirectory == null || DirectoryLocks == null) throw new MissingMemberException("Actual PreparedPack lease members missing.");
            CheckPlainAncestors(Work);
            fixture = Path.Combine(Work, "LeaseProbeFixture-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(fixture) || File.Exists(fixture)) throw new IOException("Fresh fixture already exists.");
            Directory.CreateDirectory(fixture);
            Console.WriteLine("FIXTURE " + fixture);
            Console.WriteLine("RUNTIME " + RuntimeInformation.FrameworkDescription);
            string engine = typeof(PreparedPack).Assembly.Location;
            using (var input = File.OpenRead(engine))
                Console.WriteLine("ENGINE sha256=" + Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant() + " mvid=" + typeof(PreparedPack).Module.ModuleVersionId + " path=" + engine);
            string destination = Owned("junction-target");
            Directory.CreateDirectory(destination);
            byte[] buffer = MountPointBuffer(destination);
            RunCase("empty", false, buffer);
            RunCase("nonempty-parent", true, buffer);
            Console.WriteLine("RESULT checks=" + checks + " failures=" + failures + " fixture-retained=" + fixture);
            return failures == 0 ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 2;
        }
        finally
        {
            PreparedPack.Close();
        }
    }

    private static void RunCase(string name, bool nonempty, byte[] buffer)
    {
        string directory = Owned(name);
        Directory.CreateDirectory(directory);
        string child = Path.Combine(directory, "retained-child.txt");
        if (nonempty) File.WriteAllText(child, "Owned probe sentinel.\n");
        Console.WriteLine("CASE " + name);
        bool set = false;
        try
        {
            PlainDirectory.Invoke(null, new object[] { fixture, directory });
            var leases = (Dictionary<string, SafeFileHandle>)DirectoryLocks.GetValue(null);
            SafeFileHandle held = leases[directory];
            uint before = ReadAttributes(held);
            Console.WriteLine("HELD before=0x" + before.ToString("x") + " reparse=" + ((before & ReparseAttribute) != 0));
            Check((before & ReparseAttribute) == 0, name + " initially plain");

            using (var writer = CreateFile(directory, FileWriteAttributes, ShareAll, IntPtr.Zero, OpenExisting, OpenDirectoryItself, IntPtr.Zero))
            {
                int openError = writer.IsInvalid ? Marshal.GetLastWin32Error() : 0;
                Console.WriteLine("ATTRIBUTE_OPEN desiredAccess=0x100 share=7 success=" + !writer.IsInvalid + " error=" + openError);
                if (!writer.IsInvalid)
                {
                    set = DeviceIoControl(writer, SetReparsePoint, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
                    int setError = set ? 0 : Marshal.GetLastWin32Error();
                    Console.WriteLine("FSCTL_SET_REPARSE_POINT leased success=" + set + " error=" + setError);
                }
                else Console.WriteLine("FSCTL_SET_REPARSE_POINT leased not attempted: attribute-only open rejected");
            }

            uint after = ReadAttributes(held);
            Console.WriteLine("HELD after=0x" + after.ToString("x") + " reparse=" + ((after & ReparseAttribute) != 0));
            if (set) Check((after & ReparseAttribute) != 0, name + " held handle reflects successful retag");
            else Check((after & ReparseAttribute) == 0, name + " held handle remains plain after failed retag");
            if (nonempty) Check(!set && (after & ReparseAttribute) == 0, "nonempty parent rejects retag");

            bool rejected = false;
            try { PlainDirectory.Invoke(null, new object[] { fixture, directory }); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException)
            {
                rejected = true;
                Console.WriteLine("CACHE_LOOKUP rejected=" + error.InnerException.Message);
            }
            if (!rejected) Console.WriteLine("CACHE_LOOKUP accepted");
            if (set) Check(rejected, name + " cache rejects successful in-place retag");
            else Check(!rejected, name + " unchanged cache remains usable");
        }
        finally
        {
            PreparedPack.Close();
        }

        // Preserve the owned sentinel elsewhere in the same fixture, making the
        // previously nonempty directory a valid post-close positive control.
        if (nonempty && !set)
        {
            CheckPlainAncestors(directory);
            string retained = Owned(name + "-retained-child.txt");
            File.Move(child, retained);
            Console.WriteLine("SENTINEL retained=" + retained);
        }
        using (var writer = CreateFile(directory, FileWriteAttributes, ShareAll, IntPtr.Zero, OpenExisting, OpenDirectoryItself, IntPtr.Zero))
        {
            int openError = writer.IsInvalid ? Marshal.GetLastWin32Error() : 0;
            Console.WriteLine("AFTER_CLOSE_ATTRIBUTE_OPEN success=" + !writer.IsInvalid + " error=" + openError);
            Check(!writer.IsInvalid, name + " after-close attribute-only handle opens");
            if (!writer.IsInvalid)
            {
                bool control = DeviceIoControl(writer, SetReparsePoint, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
                int controlError = control ? 0 : Marshal.GetLastWin32Error();
                Console.WriteLine("FSCTL_SET_REPARSE_POINT after-close success=" + control + " error=" + controlError);
                Check(control, name + " valid positive control after Close");
                if (control) Check((ReadAttributes(writer) & ReparseAttribute) != 0, name + " positive-control handle reflects tag");
            }
        }
    }

    private static string Owned(string name)
    {
        string path = Path.GetFullPath(Path.Combine(fixture, name));
        if (!path.StartsWith(fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Probe path escapes fixture.");
        return path;
    }

    private static void CheckPlainAncestors(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current != null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Probe path has a reparse ancestor: " + current.FullName);
    }

    private static byte[] MountPointBuffer(string target)
    {
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        byte[] display = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        byte[] result = new byte[16 + substitute.Length + 2 + display.Length + 2];
        Buffer.BlockCopy(BitConverter.GetBytes(MountPointTag), 0, result, 0, 4);
        PutU16(result, 4, result.Length - 8);
        PutU16(result, 8, 0);
        PutU16(result, 10, substitute.Length);
        PutU16(result, 12, substitute.Length + 2);
        PutU16(result, 14, display.Length);
        Buffer.BlockCopy(substitute, 0, result, 16, substitute.Length);
        Buffer.BlockCopy(display, 0, result, 16 + substitute.Length + 2, display.Length);
        return result;
    }

    private static void PutU16(byte[] buffer, int offset, int value)
    {
        Buffer.BlockCopy(BitConverter.GetBytes(checked((ushort)value)), 0, buffer, offset, 2);
    }

    private static uint ReadAttributes(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return information.Attributes;
    }

    private static void Check(bool condition, string message)
    {
        checks++;
        Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
        if (!condition) failures++;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
