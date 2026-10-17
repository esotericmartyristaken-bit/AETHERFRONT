using ApfCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ApfDiagnosticsHost
{
    internal static class Program
    {
        private const long MaximumUploadBytes = ApfDiagnosticBundleBuilder.MaximumArchiveBytes;
        private const long MaximumUncompressedBytes = 64L * 1024 * 1024;
        private const int MaximumEntries = 32;
        private static readonly Regex ReportIdPattern = new Regex(@"^AF-[A-Za-z0-9_-]{8,76}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly ConcurrentDictionary<string, List<DateTime>> UploadsByAddress = new ConcurrentDictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);

        private static int Main(string[] args)
        {
            try
            {
                Run(args).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 2;
            }
        }

        private static async Task Run(string[] args)
        {
            var root = Path.GetFullPath(ReadRequired(args, "--root")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var listenUrl = new Uri(ReadValue(args, "--listen-url", "https://127.0.0.1:18444"));
            var certificatePath = ReadValue(args, "--certificate-path", Environment.GetEnvironmentVariable("APF_DIAGNOSTICS_CERT_PATH") ?? string.Empty);
            var certificatePassword = ReadValue(args, "--certificate-password", Environment.GetEnvironmentVariable("APF_DIAGNOSTICS_CERT_PASSWORD") ?? string.Empty);
            var uploadTokenPath = ReadValue(args, "--upload-token-file", string.Empty);
            var uploadToken = !string.IsNullOrWhiteSpace(uploadTokenPath)
                ? File.ReadAllText(Path.GetFullPath(uploadTokenPath)).Trim()
                : ReadValue(args, "--upload-token", Environment.GetEnvironmentVariable("APF_DIAGNOSTICS_UPLOAD_TOKEN") ?? string.Empty);
            var retentionDays = ReadInt(args, "--retention-days", 30, 1, 365);
            var allowHttpLoopback = HasFlag(args, "--allow-http-loopback");

            var useHttps = string.Equals(listenUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            var loopbackHttp = !useHttps && allowHttpLoopback &&
                (string.Equals(listenUrl.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                 (IPAddress.TryParse(listenUrl.Host, out var loopbackAddress) && IPAddress.IsLoopback(loopbackAddress)));
            if (!useHttps && !loopbackHttp)
                throw new InvalidOperationException("Diagnostics host requires an HTTPS listen URL");
            if (useHttps && (string.IsNullOrWhiteSpace(certificatePath) || !File.Exists(certificatePath)))
                throw new InvalidOperationException("An HTTPS certificate PFX is required");
            if (Encoding.UTF8.GetByteCount(uploadToken) < 24)
                throw new InvalidOperationException("Diagnostics upload token must contain at least 24 UTF-8 bytes");

            Directory.CreateDirectory(root);
            if ((new DirectoryInfo(root).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Diagnostics root cannot be a reparse point");
            PruneExpired(root, retentionDays);

            var certificate = useHttps
                ? new X509Certificate2(certificatePath, certificatePassword, X509KeyStorageFlags.EphemeralKeySet)
                : null;
            var builder = WebApplication.CreateBuilder(args);
            builder.WebHost.ConfigureKestrel(options => ConfigureHttps(options, listenUrl, certificate));
            var app = builder.Build();
            app.MapGet("/health", () => Results.Json(new { ok = true, service = "aetherfront-diagnostics" }));
            // Force Minimal API route-handler binding. Passing the lambda directly
            // can bind to RequestDelegate (Task), which executes the upload but
            // silently discards the returned IResult and emits an empty 200.
            Func<HttpContext, Task<IResult>> uploadHandler =
                context => Upload(context, root, uploadToken, retentionDays);
            app.MapPost("/v1/diagnostics", uploadHandler);
            Console.WriteLine("Aetherfront diagnostics host listening at " + listenUrl + (loopbackHttp ? " behind an HTTPS reverse proxy" : string.Empty));
            Console.WriteLine("Retention: " + retentionDays + " days");
            await app.RunAsync().ConfigureAwait(false);
        }

        private static void ConfigureHttps(KestrelServerOptions options, Uri listenUrl, X509Certificate2 certificate)
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = MaximumUploadBytes;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            var address = IPAddress.TryParse(listenUrl.Host, out var parsed) ? parsed : IPAddress.Any;
            options.Listen(address, listenUrl.Port, listen =>
            {
                if (certificate != null) listen.UseHttps(certificate);
            });
        }

        private static async Task<IResult> Upload(HttpContext context, string root, string expectedToken, int retentionDays)
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Cache-Control"] = "no-store";
            var suppliedToken = context.Request.Headers["X-Aetherfront-Upload-Token"].ToString();
            if (!FixedEquals(suppliedToken, expectedToken)) return Results.Unauthorized();
            if (!string.Equals(context.Request.ContentType, "application/zip", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Content-Type must be application/zip" });
            if (!context.Request.ContentLength.HasValue || context.Request.ContentLength <= 0 || context.Request.ContentLength > MaximumUploadBytes)
                return Results.BadRequest(new { error = "Upload size is invalid" });

            var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!TakeRateLimit(remoteAddress)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            var reportId = context.Request.Headers["X-Aetherfront-Report-Id"].ToString();
            if (!ReportIdPattern.IsMatch(reportId)) return Results.BadRequest(new { error = "Report ID is invalid" });

            var dateDirectory = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd"));
            Directory.CreateDirectory(dateDirectory);
            var partialPath = Path.Combine(dateDirectory, "." + Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                long received;
                using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.SequentialScan | FileOptions.WriteThrough))
                    received = await CopyBounded(context.Request.Body, output, MaximumUploadBytes).ConfigureAwait(false);
                if (received != context.Request.ContentLength.Value) throw new InvalidDataException("Upload is truncated");

                var manifest = ValidateArchive(partialPath, reportId);
                var caseId = "AFD-" + DateTime.UtcNow.ToString("yyyyMMdd") + "-" + RandomHex(4).ToUpperInvariant();
                var finalPath = Path.Combine(dateDirectory, caseId + ".zip");
                File.Move(partialPath, finalPath);
                var metadata = new ApfDiagnosticStoredMetadata
                {
                    CaseId = caseId,
                    ReportId = reportId,
                    ReceivedUtc = DateTime.UtcNow.ToString("O"),
                    ReceivedBytes = received,
                    Sha256 = ApfJson.Sha256File(finalPath),
                    ClientVersion = manifest.Context?.ClientVersion ?? string.Empty,
                    SessionId = manifest.Context?.SessionId ?? string.Empty
                };
                File.WriteAllText(Path.Combine(dateDirectory, caseId + ".json"), ApfJson.Serialize(metadata), new UTF8Encoding(false));
                PruneExpired(root, retentionDays);
                return Results.Json(new ApfDiagnosticUploadReply { CaseId = caseId, ReceivedBytes = received });
            }
            catch (InvalidDataException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            finally
            {
                try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { }
            }
        }

        private static ApfDiagnosticManifest ValidateArchive(string path, string reportId)
        {
            using (var archive = ZipFile.OpenRead(path))
            {
                if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumEntries)
                    throw new InvalidDataException("Diagnostic archive entry count is invalid");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long expanded = 0;
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    if (!ApfPaths.IsSafeRelative(name) || name.EndsWith("/", StringComparison.Ordinal) || !names.Add(name))
                        throw new InvalidDataException("Diagnostic archive contains an unsafe or duplicate path");
                    var extension = Path.GetExtension(name);
                    if (!(extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
                          extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                          extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
                          extension.Equals(".json", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Diagnostic archive contains a disallowed file type");
                    expanded += entry.Length;
                    if (expanded > MaximumUncompressedBytes) throw new InvalidDataException("Diagnostic archive expands beyond its limit");
                }
                var manifestEntry = archive.GetEntry("diagnostic.json");
                if (manifestEntry == null || manifestEntry.Length <= 0 || manifestEntry.Length > 1024 * 1024)
                    throw new InvalidDataException("Diagnostic archive manifest is missing or invalid");
                using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8, true, 8192, false))
                {
                    var manifest = ApfJson.Deserialize<ApfDiagnosticManifest>(reader.ReadToEnd());
                    if (manifest?.Context == null || !string.Equals(manifest.Context.ReportId, reportId, StringComparison.Ordinal))
                        throw new InvalidDataException("Diagnostic manifest report ID does not match the upload");
                    return manifest;
                }
            }
        }

        private static async Task<long> CopyBounded(Stream input, Stream output, long maximum)
        {
            var buffer = new byte[1024 * 1024];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maximum) throw new InvalidDataException("Upload exceeds its limit");
                await output.WriteAsync(buffer, 0, read).ConfigureAwait(false);
            }
            return total;
        }

        private static bool TakeRateLimit(string address)
        {
            var now = DateTime.UtcNow;
            var entries = UploadsByAddress.GetOrAdd(address, _ => new List<DateTime>());
            lock (entries)
            {
                entries.RemoveAll(value => now - value > TimeSpan.FromMinutes(10));
                if (entries.Count >= 5) return false;
                entries.Add(now);
                return true;
            }
        }

        private static bool FixedEquals(string supplied, string expected)
        {
            var left = Encoding.UTF8.GetBytes(supplied ?? string.Empty);
            var right = Encoding.UTF8.GetBytes(expected ?? string.Empty);
            return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
        }

        private static string RandomHex(int bytes)
        {
            var data = new byte[bytes];
            RandomNumberGenerator.Fill(data);
            return ApfJson.ToHex(data);
        }

        private static void PruneExpired(string root, int retentionDays)
        {
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    if ((info.Attributes & FileAttributes.ReparsePoint) == 0 && info.LastWriteTimeUtc < cutoff)
                        info.Delete();
                }
                catch { }
            }
        }

        private static string ReadRequired(string[] args, string name)
        {
            var value = ReadValue(args, name, string.Empty);
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Missing " + name);
            return value;
        }

        private static string ReadValue(string[] args, string name, string fallback)
        {
            for (var index = 0; index + 1 < (args?.Length ?? 0); index++)
                if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1] ?? fallback;
            return fallback;
        }

        private static int ReadInt(string[] args, string name, int fallback, int minimum, int maximum)
        {
            var text = ReadValue(args, name, fallback.ToString());
            if (!int.TryParse(text, out var value) || value < minimum || value > maximum)
                throw new ArgumentException(name + " must be between " + minimum + " and " + maximum);
            return value;
        }

        private static bool HasFlag(string[] args, string name)
        {
            return (args ?? Array.Empty<string>()).Any(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
