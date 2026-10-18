using System;
using System.IO;
using System.Threading;

namespace KesasInstall
{
    internal static class AtomicFiles
    {
        // A short-lived reader (including file scanners) can deny Windows
        // ReplaceFile deletion. Retry the same atomic operation, never delete
        // the destination or fall back to a non-atomic copy.
        internal static void Replace(string source, string destination, string backup)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { File.Replace(source, destination, backup); return; }
                catch (IOException error) when (attempt < 6 && Transient(error) &&
                    File.Exists(source) && File.Exists(destination))
                { Thread.Sleep(25 * (attempt + 1)); }
            }
        }
        private static bool Transient(IOException error)
        {
            int code = error.HResult & 0xffff;
            return code == 32 || code == 33 || code == 1175;
        }
    }
}
