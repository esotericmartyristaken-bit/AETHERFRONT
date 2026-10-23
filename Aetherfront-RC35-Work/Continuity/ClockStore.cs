using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Aetherfront.Continuity
{
    // Store only paused game time. A server clock offset would advance while offline.
    public sealed class ClockStore
    {
        public const double MaximumTime = 1000000000000d;
        private const int MaximumRecordBytes = 512;
        private readonly string root;
        private readonly object gate = new object();

        public ClockStore(string universeRoot)
        {
            if (string.IsNullOrWhiteSpace(universeRoot)) throw new ArgumentException("Universe root required");
            root = Path.Combine(Path.GetFullPath(universeRoot), "AetherfrontTime");
        }

        public double Resume(string accountId, double firstJoinTime)
        {
            ValidateTime(firstJoinTime);
            string key = AccountKey(accountId);
            lock (gate)
            {
                EnsureSafeRoot();
                double time;
                long sequence;
                if (Read(key, out time, out sequence)) return time;
                Write(key, firstJoinTime, 1);
                return firstJoinTime;
            }
        }

        public void Checkpoint(string accountId, double universeTime)
        {
            ValidateTime(universeTime);
            string key = AccountKey(accountId);
            lock (gate)
            {
                EnsureSafeRoot();
                double previous;
                long sequence;
                bool exists = Read(key, out previous, out sequence);
                if (exists && universeTime < previous - 0.001d)
                    throw new InvalidDataException("Personal clock cannot move backwards");
                // Avoid writing an older value due to floating point clock conversion.
                Write(key, exists ? Math.Max(previous, universeTime) : universeTime, checked(sequence + 1));
            }
        }

        public static void ValidateTime(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d || value > MaximumTime)
                throw new InvalidDataException("Personal clock outside supported range");
        }

        private static string AccountKey(string accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 512)
                throw new ArgumentException("Authenticated account required");
            // Preserve ordinal account identity. Do not merge accounts by display name/case.
            return Digest(Encoding.UTF8.GetBytes(accountId));
        }

        private static string Digest(byte[] value)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(value)).Replace("-", "").ToLowerInvariant();
        }

        private void EnsureSafeRoot()
        {
            Directory.CreateDirectory(root);
            RejectLink(root);
        }

        private static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Clock state must not be a link");
        }

        private bool Read(string key, out double time, out long sequence)
        {
            time = 0;
            sequence = 0;
            string path = Path.Combine(root, key + ".clock");
            if (!File.Exists(path)) return false;
            RejectLink(path);
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length < 1 || stream.Length > MaximumRecordBytes)
                    throw new InvalidDataException("Invalid personal clock record size");
                using (StreamReader reader = new StreamReader(stream, Encoding.ASCII, false))
                {
                    string text = reader.ReadToEnd();
                    string[] fields = text.Split('\n');
                    if (fields.Length != 6 || fields[0] != "APF-CLOCK-1" || fields[1] != key || fields[5] != "")
                        throw new InvalidDataException("Invalid personal clock record");
                    string content = string.Join("\n", fields, 0, 4) + "\n";
                    if (!string.Equals(fields[4], Digest(Encoding.ASCII.GetBytes(content)), StringComparison.Ordinal))
                        throw new InvalidDataException("Personal clock record checksum failed");
                    if (!double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out time) ||
                        !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out sequence) || sequence < 1)
                        throw new InvalidDataException("Invalid personal clock values");
                    ValidateTime(time);
                    return true;
                }
            }
        }

        private void Write(string key, double time, long sequence)
        {
            string content = "APF-CLOCK-1\n" + key + "\n" + time.ToString("R", CultureInfo.InvariantCulture) + "\n" +
                sequence.ToString(CultureInfo.InvariantCulture) + "\n";
            byte[] bytes = Encoding.ASCII.GetBytes(content + Digest(Encoding.ASCII.GetBytes(content)) + "\n");
            string destination = Path.Combine(root, key + ".clock");
            string temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(destination))
                {
                    RejectLink(destination);
                    File.Replace(temporary, destination, null);
                }
                else File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
