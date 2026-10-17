using System;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;

namespace ApfPackHost
{
    internal static class Program
    {
        private const string ManifestName = "APFPack.json";

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
            var root = Path.GetFullPath(ReadRequired(args, "--root"));
            var listenUrl = new Uri(ReadValue(args, "--listen-url", "https://127.0.0.1:18443"));
            var certificatePath = ReadValue(args, "--certificate-path", Environment.GetEnvironmentVariable("APF_PACK_HOST_CERT_PATH") ?? string.Empty);
            var certificatePassword = ReadValue(args, "--certificate-password", Environment.GetEnvironmentVariable("APF_PACK_HOST_CERT_PASSWORD") ?? string.Empty);

            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Pack root does not exist: " + root);
            if (!string.Equals(listenUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Pack host requires an HTTPS listen URL");
            if (string.IsNullOrWhiteSpace(certificatePath) || !File.Exists(certificatePath))
                throw new InvalidOperationException("An HTTPS certificate PFX is required; use --certificate-path or APF_PACK_HOST_CERT_PATH");

            var certificate = new X509Certificate2(certificatePath, certificatePassword, X509KeyStorageFlags.EphemeralKeySet);
            var builder = WebApplication.CreateBuilder(args);
            builder.WebHost.ConfigureKestrel(options => ConfigureHttps(options, listenUrl, certificate));
            var app = builder.Build();

            app.MapGet("/health", () => Results.Json(new { ok = true, service = "apf-pack-host" }));
            app.MapGet("/", () => Results.Json(new { ok = true, manifest = ManifestName }));
            app.MapGet("/{**path}", (HttpContext context, string path) => ServeArtifact(context, root, path));

            Console.WriteLine("APF pack host serving " + root);
            Console.WriteLine("HTTPS endpoint: " + listenUrl);
            await app.RunAsync().ConfigureAwait(false);
        }

        private static void ConfigureHttps(KestrelServerOptions options, Uri listenUrl, X509Certificate2 certificate)
        {
            var address = IPAddress.TryParse(listenUrl.Host, out var parsed) ? parsed : IPAddress.Any;
            options.Listen(address, listenUrl.Port, listen => listen.UseHttps(certificate));
        }

        private static IResult ServeArtifact(HttpContext context, string root, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return Results.NotFound();
            var relative = Uri.UnescapeDataString(path).Replace('/', Path.DirectorySeparatorChar);
            if (relative.IndexOf('\0') >= 0 || relative.Contains("..") || Path.IsPathRooted(relative) || relative.Contains(Path.DirectorySeparatorChar))
                return Results.BadRequest("Only a root-level APFPack.json or ZIP artifact may be requested");

            if (!string.Equals(relative, ManifestName, StringComparison.OrdinalIgnoreCase) &&
                !relative.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return Results.NotFound();

            var fullPath = Path.GetFullPath(Path.Combine(root, relative));
            var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
                return Results.NotFound();

            var fileInfo = new FileInfo(fullPath);
            if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                return Results.BadRequest("Reparse-point artifacts are not served");

            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Cache-Control"] = string.Equals(relative, ManifestName, StringComparison.OrdinalIgnoreCase)
                ? "no-store" : "public, max-age=31536000, immutable";
            var contentType = string.Equals(relative, ManifestName, StringComparison.OrdinalIgnoreCase)
                ? "application/json" : "application/zip";
            return Results.File(fullPath, contentType, enableRangeProcessing: true);
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
    }
}
