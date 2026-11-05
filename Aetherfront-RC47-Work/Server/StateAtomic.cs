using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Server.Aetherfront
{
    // Called under the existing store transaction lock. A failed replacement
    // never advances the in-memory ledger or acknowledges a science purchase.
    internal static class StateAtomic
    {
        internal static void Write(string path, string json)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            byte[] replacement = new UTF8Encoding(false).GetBytes(json);
            byte[] original = File.Exists(path) ? Read(path) : null;
            var watch = Stopwatch.StartNew();
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(replacement, 0, replacement.Length); stream.Flush(true); }
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (original == null) File.Move(temporary, path);
                        else { Check(path); File.Replace(temporary, path, null); }
                        return;
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    {
                        int code = error.HResult & 65535;
                        if (code != 5 && code != 32 && code != 33 && code != 1175) throw;
                        if (!File.Exists(temporary))
                        {
                            if (Same(Read(path), replacement)) return;
                            throw;
                        }
                        // A retry is safe only while both pre-operation images
                        // are unchanged. Permissions are never modified.
                        if (!Same(Read(temporary), replacement) ||
                            (original == null ? File.Exists(path) : !Same(Read(path), original))) throw;
                        if (attempt >= 12 || watch.ElapsedMilliseconds >= 1000) throw;
                        Thread.Sleep((int)Math.Max(1, Math.Min(100, 1000 - watch.ElapsedMilliseconds)));
                    }
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static void Check(string path)
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.ReadOnly)) != 0)
                throw new InvalidDataException("State replacement requires a writable regular file");
        }
        private static byte[] Read(string path)
        {
            Check(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("State image exceeds replacement bound");
                byte[] bytes = new byte[(int)stream.Length]; int offset = 0;
                while (offset < bytes.Length)
                { int count = stream.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
                if (stream.ReadByte() != -1) throw new IOException("State changed while reading");
                return bytes;
            }
        }
        private static bool Same(byte[] a, byte[] b)
        { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    }
}
