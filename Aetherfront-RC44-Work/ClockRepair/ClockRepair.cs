using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Aetherfront.Continuity
{
    // The release graft imports only these private methods into the shipping
    // ClockStore. Its identity, record format, validation and session guard stay intact.
    public sealed class ClockStore
    {
        private static void ReplaceClockCheckpoint(string source, string destination, string backup)
        {
            if (backup != null) throw new ArgumentException("Clock replacement does not use a backup path");
            byte[] replacement = ReplaceClockReadImage(source);
            byte[] original = ReplaceClockReadImage(destination);
            long started = Stopwatch.GetTimestamp();
            int attempts = 0, delay = 10;
            for (;;)
            {
                ReplaceClockCheckPath(source);
                ReplaceClockCheckPath(destination);
                try
                {
                    File.Replace(source, destination, null);
                    return;
                }
                catch (IOException error)
                {
                    // These errors precede replacement. Do not retry ambiguous
                    // partial-move errors, corruption, permission or disk failures.
                    if (!ReplaceClockTransient(error)) throw;
                    if (!File.Exists(source))
                    {
                        // An uncertain result is success only with exact proof
                        // that this already-flushed record became authoritative.
                        if (ReplaceClockSame(ReplaceClockReadImage(destination), replacement)) return;
                        throw;
                    }
                    if (!ReplaceClockSame(ReplaceClockReadImage(source), replacement) ||
                        !ReplaceClockSame(ReplaceClockReadImage(destination), original)) throw;
                    long elapsed = (Stopwatch.GetTimestamp() - started) * 1000L / Stopwatch.Frequency;
                    if (++attempts >= 16 || elapsed >= 2000) throw;
                    Thread.Sleep((int)Math.Min(delay, 2000L - elapsed));
                    delay = Math.Min(200, delay * 2);
                }
            }
        }

        private static bool ReplaceClockTransient(IOException error)
        {
            return error.HResult == unchecked((int)0x80070020) || // Sharing violation.
                   error.HResult == unchecked((int)0x80070021) || // Lock violation.
                   error.HResult == unchecked((int)0x80070497);   // Unable to remove replaced file.
        }

        private static void ReplaceClockCheckPath(string path)
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidDataException("Clock replacement requires a regular file");
        }

        private static byte[] ReplaceClockReadImage(string path)
        {
            ReplaceClockCheckPath(path);
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length < 1 || stream.Length > 512) throw new InvalidDataException("Invalid clock replacement size");
                byte[] image = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < image.Length)
                {
                    int count = stream.Read(image, offset, image.Length - offset);
                    if (count == 0) throw new EndOfStreamException("Clock replacement changed while reading");
                    offset += count;
                }
                if (stream.ReadByte() != -1) throw new InvalidDataException("Clock replacement grew while reading");
                return image;
            }
        }

        private static bool ReplaceClockSame(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int index = 0; index < left.Length; index++) if (left[index] != right[index]) return false;
            return true;
        }
    }
}
