using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LmpClient.Apf
{
    public static class ApfPinnedHttps
    {
        public static void Configure(HttpWebRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var pins = LoadPins();
            if (pins.Count == 0) return;
            request.ServerCertificateValidationCallback = (_, certificate, __, errors) =>
                errors == SslPolicyErrors.None || MatchesPin(certificate, pins);
        }

        private static bool MatchesPin(X509Certificate certificate, HashSet<string> pins)
        {
            if (certificate == null) return false;
            try
            {
                using (var parsed = new X509Certificate2(certificate))
                {
                    var now = DateTime.UtcNow;
                    if (now < parsed.NotBefore.ToUniversalTime() || now > parsed.NotAfter.ToUniversalTime()) return false;
                    using (var sha = SHA256.Create())
                    {
                        var hash = string.Concat(sha.ComputeHash(parsed.RawData).Select(value => value.ToString("x2")));
                        return pins.Contains(hash);
                    }
                }
            }
            catch { return false; }
        }

        private static HashSet<string> LoadPins()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(ApfClientSettings.TlsCertificatePinsPath)) return result;
                foreach (var line in File.ReadAllLines(ApfClientSettings.TlsCertificatePinsPath))
                {
                    var pin = (line ?? string.Empty).Trim();
                    if (pin.Length == 64 && pin.All(Uri.IsHexDigit)) result.Add(pin);
                }
            }
            catch { }
            return result;
        }
    }
}
