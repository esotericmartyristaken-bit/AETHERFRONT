using Aetherfront.Core;
using System;
using System.IO;
using System.Security.Cryptography;

namespace Server.Aetherfront
{
    internal sealed class StagedFlagAsset
    {
        public string AssetId { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string TemporaryPath { get; set; } = string.Empty;
    }

    internal static class AetherfrontFlagAssetStore
    {
        private const int MaxBytes = 512 * 1024;
        private const uint MaxDimension = 2048;
        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        public static StagedFlagAsset Stage(AetherfrontWireRequest request, string rootPath)
        {
            if (request == null || (request.CommandType != AetherfrontCommandType.CreateFaction &&
                request.CommandType != AetherfrontCommandType.UpdateFactionProfile) ||
                string.IsNullOrWhiteSpace(request.FlagDataBase64)) return null;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(request.FlagDataBase64); }
            catch (FormatException) { throw new InvalidDataException("FlagDataBase64 is invalid"); }
            if (bytes.Length == 0 || bytes.Length > MaxBytes) throw new InvalidDataException("Flag asset exceeds 512 KB");
            if (!IsPng(bytes)) throw new InvalidDataException("Flag asset must be a PNG");

            var hash = Convert.ToHexString(SHA256.Create().ComputeHash(bytes)).ToLowerInvariant();
            var assetId = "flag-" + hash;
            var pendingRoot = Path.Combine(rootPath, "flags", "pending");
            Directory.CreateDirectory(pendingRoot);
            var temporaryPath = Path.Combine(pendingRoot, hash + "-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(temporaryPath, bytes);
            request.FlagAssetId = assetId;
            request.FlagSha256 = hash;
            return new StagedFlagAsset { AssetId = assetId, Sha256 = hash, TemporaryPath = temporaryPath };
        }

        public static void Commit(StagedFlagAsset staged, string rootPath)
        {
            if (staged == null || string.IsNullOrWhiteSpace(staged.TemporaryPath)) return;
            if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(staged.Sha256)) throw new InvalidDataException("Invalid staged flag");
            var flagRoot = Path.Combine(rootPath, "flags");
            Directory.CreateDirectory(flagRoot);
            var finalPath = Path.Combine(flagRoot, staged.Sha256 + ".png");
            if (File.Exists(finalPath))
            {
                File.Delete(staged.TemporaryPath);
                return;
            }
            File.Move(staged.TemporaryPath, finalPath);
        }

        public static void Discard(StagedFlagAsset staged)
        {
            if (staged != null && !string.IsNullOrWhiteSpace(staged.TemporaryPath))
                File.Delete(staged.TemporaryPath);
        }

        public static bool TryRead(string rootPath, string assetId, out string sha256, out string dataBase64)
        {
            sha256 = string.Empty;
            dataBase64 = string.Empty;
            if (string.IsNullOrWhiteSpace(assetId) || !assetId.StartsWith("flag-", StringComparison.OrdinalIgnoreCase)) return false;
            var hash = assetId.Substring(5);
            if (hash.Length != 64) return false;
            foreach (var character in hash)
                if (!Uri.IsHexDigit(character)) return false;
            var path = Path.Combine(rootPath, "flags", hash.ToLowerInvariant() + ".png");
            if (!File.Exists(path)) return false;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaxBytes || !IsPng(bytes)) return false;
            using (var sha = SHA256.Create())
                sha256 = Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
            if (!string.Equals(sha256, hash, StringComparison.OrdinalIgnoreCase)) return false;
            dataBase64 = Convert.ToBase64String(bytes);
            return true;
        }

        private static bool IsPng(byte[] bytes)
        {
            if (bytes == null || bytes.Length < PngSignature.Length) return false;
            for (var index = 0; index < PngSignature.Length; index++)
                if (bytes[index] != PngSignature[index]) return false;

            var offset = PngSignature.Length;
            var sawHeader = false;
            var sawEnd = false;
            while (offset + 12 <= bytes.Length)
            {
                var length = ReadUInt32(bytes, offset);
                offset += 4;
                if (offset + 4 > bytes.Length) return false;
                var type = global::System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
                offset += 4;
                if (length > (uint)(bytes.Length - offset - 4)) return false;
                if (type == "IHDR")
                {
                    if (sawHeader || length != 13 || offset + 13 > bytes.Length) return false;
                    var width = ReadUInt32(bytes, offset);
                    var height = ReadUInt32(bytes, offset + 4);
                    var bitDepth = bytes[offset + 8];
                    var colorType = bytes[offset + 9];
                    if (width == 0 || height == 0 || width > MaxDimension || height > MaxDimension ||
                        (bitDepth != 8 && bitDepth != 16) ||
                        (colorType != 0 && colorType != 2 && colorType != 3 && colorType != 4 && colorType != 6)) return false;
                    sawHeader = true;
                }
                offset += checked((int)length);
                offset += 4; // CRC; hash validation covers content identity.
                if (type == "IEND")
                {
                    if (length != 0 || !sawHeader) return false;
                    sawEnd = true;
                    break;
                }
            }
            return sawHeader && sawEnd;
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) |
                ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
        }
    }
}
