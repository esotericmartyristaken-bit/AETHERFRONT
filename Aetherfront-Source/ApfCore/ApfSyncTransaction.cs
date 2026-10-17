using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace ApfCore
{
    public sealed class ApfReconnectInfo
    {
        public string Address { get; set; } = string.Empty;
        public int Port { get; set; }
        public string Password { get; set; } = string.Empty;
    }
    public sealed class ApfSyncTransaction
    {
        public string GameRoot { get; set; } = string.Empty;
        public string GameDataPath { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public string KspExecutable { get; set; } = string.Empty;
        public List<string> KspArguments { get; set; } = new List<string>();
        public string StatePath { get; set; } = string.Empty;
        public ApfPackManifest Manifest { get; set; }
        public ApfPackState ExistingState { get; set; } = new ApfPackState();
        public List<ApfStagedPackage> Packages { get; set; } = new List<ApfStagedPackage>();
        public List<ApfDeleteChange> Deletes { get; set; } = new List<ApfDeleteChange>();
        public string Authorization { get; set; } = string.Empty;
    }
    public sealed class ApfStagedPackage
    {
        public ApfPackage Package { get; set; }
        public string ArchivePath { get; set; } = string.Empty;
    }
    public sealed class ApfClientUpdateTransaction
    {
        public string GameRoot { get; set; } = string.Empty;
        public string GameDataPath { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public string KspExecutable { get; set; } = string.Empty;
        public List<string> KspArguments { get; set; } = new List<string>();
        public string ArchivePath { get; set; } = string.Empty;
        public ApfClientUpdateManifest Manifest { get; set; }
        public string Authorization { get; set; } = string.Empty;
    }

    public static class ApfTransactionSecurity
    {
        private const int KeyBytes = 32;

        public static byte[] LoadOrCreateKey(string keyPath)
        {
            if (File.Exists(keyPath)) return LoadKey(keyPath);
            var directory = Path.GetDirectoryName(keyPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var key = new byte[KeyBytes];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(key);
            try
            {
                using (var stream = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream)) writer.Write(Convert.ToBase64String(key));
                return key;
            }
            catch (IOException)
            {
                return LoadKey(keyPath);
            }
        }

        public static byte[] LoadKey(string keyPath)
        {
            var key = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
            if (key.Length != KeyBytes) throw new InvalidDataException("APF helper key must be 256 bits");
            return key;
        }

        public static void Sign(ApfSyncTransaction transaction, byte[] key)
        {
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (key == null || key.Length == 0) throw new ArgumentException("APF helper key is empty", nameof(key));
            var previous = transaction.Authorization;
            transaction.Authorization = string.Empty;
            try
            {
                using (var hmac = new HMACSHA256(key))
                    transaction.Authorization = ApfJson.ToHex(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(ApfJson.Serialize(transaction))));
            }
            finally
            {
                if (string.IsNullOrEmpty(transaction.Authorization)) transaction.Authorization = previous;
            }
        }

        public static bool Verify(ApfSyncTransaction transaction, byte[] key)
        {
            if (transaction == null || key == null || key.Length == 0 || string.IsNullOrWhiteSpace(transaction.Authorization)) return false;
            var supplied = transaction.Authorization;
            transaction.Authorization = string.Empty;
            try
            {
                using (var hmac = new HMACSHA256(key))
                {
                    var expected = ApfJson.ToHex(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(ApfJson.Serialize(transaction))));
                    return string.Equals(expected, supplied, StringComparison.OrdinalIgnoreCase);
                }
            }
            finally { transaction.Authorization = supplied; }
        }

        public static void Sign(ApfClientUpdateTransaction transaction, byte[] key)
        {
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (key == null || key.Length == 0) throw new ArgumentException("APF helper key is empty", nameof(key));
            var previous = transaction.Authorization;
            transaction.Authorization = string.Empty;
            try
            {
                using (var hmac = new HMACSHA256(key))
                    transaction.Authorization = ApfJson.ToHex(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(ApfJson.Serialize(transaction))));
            }
            finally
            {
                if (string.IsNullOrEmpty(transaction.Authorization)) transaction.Authorization = previous;
            }
        }

        public static bool Verify(ApfClientUpdateTransaction transaction, byte[] key)
        {
            if (transaction == null || key == null || key.Length == 0 || string.IsNullOrWhiteSpace(transaction.Authorization)) return false;
            var supplied = transaction.Authorization;
            transaction.Authorization = string.Empty;
            try
            {
                using (var hmac = new HMACSHA256(key))
                {
                    var expected = ApfJson.ToHex(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(ApfJson.Serialize(transaction))));
                    return string.Equals(expected, supplied, StringComparison.OrdinalIgnoreCase);
                }
            }
            finally { transaction.Authorization = supplied; }
        }
    }
}
