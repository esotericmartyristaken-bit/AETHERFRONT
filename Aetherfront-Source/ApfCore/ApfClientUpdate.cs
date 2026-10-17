using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace ApfCore
{
    public sealed class ApfClientUpdateManifest
    {
        public int SchemaVersion { get; set; } = 1;
        public string Product { get; set; } = "apf-lmp-client";
        public string Version { get; set; } = string.Empty;
        public string KspVersion { get; set; } = string.Empty;
        public string PackageUrl { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public List<ApfClientUpdateFile> Files { get; set; } = new List<ApfClientUpdateFile>();
        public string ReleaseNotes { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;

        public string ToJson() => ApfJson.Serialize(this);
        public static ApfClientUpdateManifest FromJson(string json) => ApfJson.Deserialize<ApfClientUpdateManifest>(json);
    }
    public sealed class ApfClientUpdateFile
    {
        public string Path { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
    }

    public static class ApfClientUpdateValidator
    {
        private static readonly HashSet<string> AllowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gamedata/lunamultiplayer/plugins/lmpclient.dll",
            "gamedata/lunamultiplayer/plugins/apfcore.dll",
            "gamedata/lunamultiplayer/plugins/aetherfrontcore.dll",
            "gamedata/lunamultiplayer/plugins/lidgren.network.dll",
            "gamedata/lunamultiplayer/plugins/cachedquicklz.dll",
            "gamedata/lunamultiplayer/plugins/jsonfx.dll",
            "gamedata/lunamultiplayer/plugins/system.buffers.dll",
            "gamedata/lunamultiplayer/plugins/system.memory.dll",
            "gamedata/lunamultiplayer/plugins/system.numerics.vectors.dll",
            "gamedata/lunamultiplayer/plugins/system.runtime.compilerservices.unsafe.dll",
            "gamedata/lunamultiplayer/data/apftlscertificatepins.txt"
        };

        public static ApfValidationResult Validate(ApfClientUpdateManifest manifest, bool requireSignature = true)
        {
            var result = new ApfValidationResult();
            if (manifest == null)
            {
                result.Add("Client update manifest is null");
                return result;
            }

            if (manifest.SchemaVersion != 1) result.Add("Unsupported client update schemaVersion");
            if (!string.Equals(manifest.Product, "apf-lmp-client", StringComparison.Ordinal)) result.Add("Invalid client update product");
            Require(manifest.Version, "client update version", result);
            if (!string.IsNullOrWhiteSpace(manifest.Version) && !IsSafeVersion(manifest.Version))
                result.Add("Invalid client update version");
            if (!string.IsNullOrWhiteSpace(manifest.KspVersion) && !IsSafeVersion(manifest.KspVersion))
                result.Add("Invalid client update KSP version");
            if (!Uri.TryCreate(manifest.PackageUrl, UriKind.Absolute, out var packageUri) || packageUri.Scheme != Uri.UriSchemeHttps)
                result.Add("Client update packageUrl must use HTTPS");
            if (!IsSha256(manifest.Sha256)) result.Add("Client update sha256 must be 64 hex characters");
            if (manifest.SizeBytes <= 0) result.Add("Client update sizeBytes must be positive");
            if (requireSignature && string.IsNullOrWhiteSpace(manifest.Signature)) result.Add("Client update signature is required");
            if (manifest.Files == null || manifest.Files.Count == 0)
            {
                result.Add("Client update files are required");
                return result;
            }

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                if (file == null)
                {
                    result.Add("Client update files contains null");
                    continue;
                }

                var path = ApfPaths.Normalize(file.Path);
                if (!ApfPaths.IsSafeRelative(file.Path) || !AllowedFiles.Contains(path))
                    result.Add("Client update file is not an allowed APF binary: " + file.Path);
                if (!paths.Add(path)) result.Add("Duplicate client update file: " + file.Path);
                if (!IsSha256(file.Sha256)) result.Add("Client update file sha256 must be 64 hex characters: " + file.Path);
                if (file.SizeBytes <= 0) result.Add("Client update file sizeBytes must be positive: " + file.Path);
            }

            return result;
        }

        public static bool IsAllowedFile(string path)
        {
            return AllowedFiles.Contains(ApfPaths.Normalize(path));
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            return value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
        }

        private static bool IsSafeVersion(string value)
        {
            return value.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' || c == '+');
        }

        private static void Require(string value, string name, ApfValidationResult result)
        {
            if (string.IsNullOrWhiteSpace(value)) result.Add(name + " is required");
        }
    }

    public static class ApfClientUpdateSecurity
    {
        public static void Sign(ApfClientUpdateManifest manifest, RSA privateKey)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (privateKey == null) throw new ArgumentNullException(nameof(privateKey));
            manifest.Signature = Convert.ToBase64String(privateKey.SignData(SignedBytes(manifest), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }

        public static bool Verify(ApfClientUpdateManifest manifest, RSA publicKey)
        {
            if (manifest == null || publicKey == null || string.IsNullOrWhiteSpace(manifest.Signature)) return false;
            try
            {
                return publicKey.VerifyData(SignedBytes(manifest), Convert.FromBase64String(manifest.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch (FormatException) { return false; }
            catch (CryptographicException) { return false; }
        }

        public static byte[] SignedBytes(ApfClientUpdateManifest manifest)
        {
            var signature = manifest.Signature;
            manifest.Signature = string.Empty;
            try { return Encoding.UTF8.GetBytes(ApfJson.Serialize(manifest)); }
            finally { manifest.Signature = signature; }
        }

        public static RSA LoadXmlPublicKey(string xml)
        {
            return LoadXmlKey(xml);
        }

        public static RSA LoadXmlKey(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) throw new InvalidDataException("APF client update public key is empty");
            var document = new XmlDocument { XmlResolver = null };
            document.LoadXml(xml);
            var root = document.DocumentElement;
            if (root == null || !string.Equals(root.Name, "RSAKeyValue", StringComparison.Ordinal))
                throw new InvalidDataException("APF client update public key must be RSA XML");

            var parameters = new RSAParameters
            {
                Modulus = ReadRequired(root, "Modulus"),
                Exponent = ReadRequired(root, "Exponent")
            };
            var privateNames = new[] { "P", "Q", "DP", "DQ", "InverseQ", "D" };
            if (privateNames.All(name => root.SelectSingleNode(name) != null))
            {
                parameters.P = ReadRequired(root, "P");
                parameters.Q = ReadRequired(root, "Q");
                parameters.DP = ReadRequired(root, "DP");
                parameters.DQ = ReadRequired(root, "DQ");
                parameters.InverseQ = ReadRequired(root, "InverseQ");
                parameters.D = ReadRequired(root, "D");
            }
            var rsa = RSA.Create();
            rsa.ImportParameters(parameters);
            return rsa;
        }

        public static string ExportXml(RSA key, bool includePrivateParameters)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            var parameters = key.ExportParameters(includePrivateParameters);
            var builder = new StringBuilder("<RSAKeyValue>");
            AppendXml(builder, "Modulus", parameters.Modulus);
            AppendXml(builder, "Exponent", parameters.Exponent);
            if (includePrivateParameters)
            {
                AppendXml(builder, "P", parameters.P);
                AppendXml(builder, "Q", parameters.Q);
                AppendXml(builder, "DP", parameters.DP);
                AppendXml(builder, "DQ", parameters.DQ);
                AppendXml(builder, "InverseQ", parameters.InverseQ);
                AppendXml(builder, "D", parameters.D);
            }
            builder.Append("</RSAKeyValue>");
            return builder.ToString();
        }

        private static byte[] ReadRequired(XmlElement root, string name)
        {
            var node = root.SelectSingleNode(name);
            if (node == null || string.IsNullOrWhiteSpace(node.InnerText)) throw new InvalidDataException("APF RSA key is missing " + name);
            try { return Convert.FromBase64String(node.InnerText); }
            catch (FormatException exception) { throw new InvalidDataException("APF RSA key contains invalid " + name, exception); }
        }

        private static void AppendXml(StringBuilder builder, string name, byte[] value)
        {
            if (value == null || value.Length == 0) throw new InvalidDataException("RSA key is missing " + name);
            builder.Append('<').Append(name).Append('>')
                .Append(Convert.ToBase64String(value))
                .Append("</").Append(name).Append('>');
        }
    }

    public static class ApfClientUpdateVersion
    {
        public static int Compare(string left, string right)
        {
            if (TryNumeric(left, out var leftNumber) && TryNumeric(right, out var rightNumber))
                return leftNumber.CompareTo(rightNumber);
            if (Version.TryParse(left, out var leftVersion) && Version.TryParse(right, out var rightVersion))
                return leftVersion.CompareTo(rightVersion);
            return string.CompareOrdinal(left ?? string.Empty, right ?? string.Empty);
        }

        private static bool TryNumeric(string value, out long number)
        {
            number = 0;
            const string prefix = "apf-client-";
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return long.TryParse(value.Substring(prefix.Length), out number);
        }
    }
}
