using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
var evidence = Path.GetFullPath(args[0]);
if (!evidence.EndsWith("rc50-input\\rc55-stability", StringComparison.OrdinalIgnoreCase)) throw new Exception("Owned evidence only");
using var rsa = RSA.Create(2048);
var request = new CertificateRequest("CN=RC55 disposable loopback test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(san.Build());
request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
// Windows Schannel server credentials require a PKCS#12-imported key handle.
using var serverCert = new X509Certificate2(cert.Export(X509ContentType.Pfx));
string pin = Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant();
File.WriteAllText(Path.Combine(evidence, "loopback-public-pin.txt"), pin);
foreach (string name in new[] { "apf-test", "apf-peer" })
{
 string path = Path.Combine(Directory.GetParent(Directory.GetParent(evidence)!.FullName)!.FullName, name, "GameData/LunaMultiplayer/Data/APFTlsCertificatePins.txt");
 string previous = File.ReadAllText(path); if (!previous.Contains(pin)) { File.WriteAllText(path + ".new", previous.TrimEnd() + "\n" + pin + "\n"); File.Move(path + ".new", path, true); }
}
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel", LogLevel.Debug);
builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 28866, endpoint => endpoint.UseHttps(serverCert, options => options.SslProtocols = System.Security.Authentication.SslProtocols.Tls12)));
var app = builder.Build();
app.MapGet("/updates/APFPack.json", () => Results.File(Path.Combine(evidence, "staging/PersonalServer/Config/APFPack.json"), "application/json"));
app.MapGet("/health", () => Results.Json(new { scope = "owned loopback test only", release = "kesas-rc55" }));
app.Run();
