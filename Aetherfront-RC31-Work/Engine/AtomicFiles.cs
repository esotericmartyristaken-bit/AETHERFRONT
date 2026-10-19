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
        // Antivirus/indexer handles can briefly deny a prepared directory rename.
        // Retry only that rename and revalidate both paths on every attempt.
        internal static void MoveDirectory(string root, string source, string destination)
        {
            for (int attempt = 0; ; attempt++)
            {
                ApfCore.ApfPaths.EnsureNoReparsePoints(root, source);
                ApfCore.ApfPaths.EnsureNoReparsePoints(root, destination);
                if (Directory.Exists(destination) || File.Exists(destination))
                    throw new IOException("Prepared directory destination already exists.");
                try { Directory.Move(source, destination); return; }
                catch (IOException error) when (attempt < 6 &&
                    ((error.HResult & 0xffff) == 5 || (error.HResult & 0xffff) == 32 || (error.HResult & 0xffff) == 33) &&
                    Directory.Exists(source) && !Directory.Exists(destination) && !File.Exists(destination))
                { Thread.Sleep(25 * (attempt + 1)); }
            }
        }
    }
}
