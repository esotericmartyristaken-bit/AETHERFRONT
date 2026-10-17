using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using JsonFx.Json;
using JsonFx.Serialization;
using JsonFx.Serialization.Resolvers;
#if !NET472
using Newtonsoft.Json;
#endif

namespace ApfCore
{
    public sealed class ApfPackManifest
    {
        public int SchemaVersion { get; set; } = 1;
        public string ServerId { get; set; } = string.Empty;
        public string PackId { get; set; } = string.Empty;
        public string PackVersion { get; set; } = string.Empty;
        public string KspVersion { get; set; } = string.Empty;
        public string RequiredClientVersion { get; set; } = string.Empty;
        public List<string> RequiredDlc { get; set; } = new List<string>();
        public List<string> ProtectedRoots { get; set; } = new List<string>();
        public List<ApfPackage> ManagedPackages { get; set; } = new List<ApfPackage>();
        public List<ApfClientOnlyRule> ClientOnly { get; set; } = new List<ApfClientOnlyRule>();

        public string ToJson()
        {
            return ApfJson.Serialize(this);
        }

        public static ApfPackManifest FromJson(string json)
        {
            return ApfJson.Deserialize<ApfPackManifest>(json);
        }
    }
    public sealed class ApfPackage
    {
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string ArchiveUrl { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string Destination { get; set; } = "GameData";
        public string StripPrefix { get; set; } = string.Empty;
    }
    public sealed class ApfClientOnlyRule
    {
        public string Id { get; set; } = string.Empty;
        public List<string> PathRoots { get; set; } = new List<string>();
        public List<ApfClientOnlyDll> Dlls { get; set; } = new List<ApfClientOnlyDll>();
    }
    public sealed class ApfClientOnlyDll
    {
        public string Path { get; set; } = string.Empty;
        public string Sha256 { get; set; }
    }
    public sealed class ApfPackState
    {
        public string PackId { get; set; } = string.Empty;
        public string PackVersion { get; set; } = string.Empty;
        public List<ApfPackageState> Packages { get; set; } = new List<ApfPackageState>();
    }
    public sealed class ApfPackageState
    {
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string ArchiveSha256 { get; set; } = string.Empty;
        public string Root { get; set; } = string.Empty;
        public List<ApfFileState> Files { get; set; } = new List<ApfFileState>();
    }
    public sealed class ApfFileState
    {
        public string Path { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
    }

    public static class ApfJson
    {
        public static string Serialize<T>(T value)
        {
            var resolver = new ConventionResolverStrategy(ConventionResolverStrategy.WordCasing.CamelCase);
            return new JsonFx.Json.JsonWriter(new DataWriterSettings(resolver)).Write(value);
        }

        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("APF JSON is empty", nameof(json));

            // HTTP/file callers may provide a UTF-8 BOM.
            json = json.TrimStart('\uFEFF');
#if NET472
            var resolver = new ConventionResolverStrategy(ConventionResolverStrategy.WordCasing.CamelCase);
            return new JsonReader(new DataReaderSettings(resolver)).Read<T>(json);
#else
            return JsonConvert.DeserializeObject<T>(json);
#endif
        }

        public static string Sha256File(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(stream));
            }
        }

        public static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes)
                builder.Append(value.ToString("x2"));
            return builder.ToString();
        }
    }
}
