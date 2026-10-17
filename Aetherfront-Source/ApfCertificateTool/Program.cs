using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ApfCertificateTool
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length != 3) throw new ArgumentException("Usage: ApfCertificateTool <dns-name> <certificate.pem> <private-key.pem>");
                var dnsName = args[0];
                var certificatePath = Path.GetFullPath(args[1]);
                var privateKeyPath = Path.GetFullPath(args[2]);
                Directory.CreateDirectory(Path.GetDirectoryName(certificatePath));
                Directory.CreateDirectory(Path.GetDirectoryName(privateKeyPath));
                if (File.Exists(certificatePath) || File.Exists(privateKeyPath)) throw new IOException("Refusing to replace an existing TLS keypair");

                using (var key = RSA.Create(3072))
                {
                    var request = new CertificateRequest("CN=" + dnsName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                    request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
                    var san = new SubjectAlternativeNameBuilder();
                    san.AddDnsName(dnsName);
                    if (IPAddress.TryParse(dnsName, out var address)) san.AddIpAddress(address);
                    request.CertificateExtensions.Add(san.Build());
                    using (var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5)))
                    {
                        File.WriteAllText(certificatePath, new string(PemEncoding.Write("CERTIFICATE", certificate.RawData)));
                        File.WriteAllText(privateKeyPath, new string(PemEncoding.Write("PRIVATE KEY", key.ExportPkcs8PrivateKey())));
                        Console.WriteLine(certificate.Thumbprint);
                    }
                }
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        }
    }
}
