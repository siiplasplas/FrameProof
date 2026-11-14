using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using FrameProof.Core.Catalog;
using FrameProof.Core.Downloads;
using FrameProof.Core.Installation;
using FrameProof.Core.Library;
using FrameProof.Core.Packages;

internal static class LiveTransportTests
{
    public static void Run(Suite suite)
    {
        suite.Run("Real local TLS transport downloads and verifies package", root =>
        {
            var bytes = Encoding.UTF8.GetBytes("FrameProof local HTTPS integration fixture");
            using var server = new FixtureServer(new Dictionary<string, byte[]> { ["/game.zip"] = bytes });
            using var client = server.CreateClient();
            var request = new DownloadRequest("tls-game", new Uri(server.BaseUri, "game.zip"), Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
            var path = new HttpPackageDownloader(client, Path.Combine(root, "cache")).DownloadAsync(request).GetAwaiter().GetResult();
            Suite.Check(File.ReadAllBytes(path).SequenceEqual(bytes), "TLS payload changed");
            Suite.Check(server.RequestCount == 1, "Unexpected network requests");
        });
        suite.Run("Real HTTPS catalog to game + fix to ready library end-to-end", root =>
        {
            var game = Archive(("game.exe", "test fixture never executed"), ("config.txt", "base"));
            var fix = Archive(("config.txt", "patched"));
            var files = new Dictionary<string, byte[]> { ["/game.zip"] = game, ["/fix.zip"] = fix };
            using var server = new FixtureServer(files);
            var recipe = new GameInstallRecipe
            {
                CatalogId = "tls-fixture", Name = "Local HTTPS fixture", GameVersion = "1", FixVersion = "1-fix",
                GamePackage = new("game", new Uri(server.BaseUri, "game.zip"), Convert.ToHexString(SHA256.HashData(game)), game.Length),
                FixPackage = new("fix", new Uri(server.BaseUri, "fix.zip"), Convert.ToHexString(SHA256.HashData(fix)), fix.Length),
                LaunchRelativePath = "game.exe", MaximumGameExpandedBytes = 1024, MaximumFixExpandedBytes = 1024
            };
            files["/catalog.json"] = JsonSerializer.SerializeToUtf8Bytes(new GameCatalog { SchemaVersion = 1, Games = new[] { recipe } }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            using var client = server.CreateClient();
            var catalog = new GameCatalogClient(client).LoadAsync(new Uri(server.BaseUri, "catalog.json")).GetAwaiter().GetResult();
            var library = new GameLibraryStore(Path.Combine(root, "library.json"));
            var coordinator = new GameInstallCoordinator(new(client, Path.Combine(root, "cache")),
                new LocalPackageInstaller(Path.Combine(root, "transactions"), _ => { }), library, Path.Combine(root, "jobs"));
            var target = Path.Combine(root, "installed-game");
            var result = coordinator.InstallAsync(catalog.Games.Single(), target).GetAwaiter().GetResult();
            Suite.Check(result.CanLaunch && result.InstalledFixVersion == "1-fix", "Not ready after pair installation");
            Suite.Check(File.ReadAllText(Path.Combine(target, "config.txt")) == "patched", "Fix not applied");
            Suite.Check(server.RequestCount == 3 && library.Load().Count == 1, "Unexpected downloads or duplicate registration");
        });
    }

    private static byte[] Archive(params (string Name, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in files)
            { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(content); }
        return stream.ToArray();
    }

    internal sealed class FixtureServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2 _certificate;
        private readonly IReadOnlyDictionary<string, byte[]> _files;
        private readonly Task _loop;
        private int _requests;
        public int RequestCount => Volatile.Read(ref _requests);
        public Uri BaseUri { get; }
        public FixtureServer(IReadOnlyDictionary<string, byte[]> files)
        {
            _files = files;
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            _certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx));
            _listener.Start();
            BaseUri = new Uri($"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _loop = Serve();
        }
        public HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                // Trust ONLY this ephemeral fixture certificate, never disable production TLS validation.
                ServerCertificateCustomValidationCallback = (message, cert, _, _) =>
                    message.RequestUri?.Host == "127.0.0.1" && message.RequestUri.Port == BaseUri.Port && cert?.Thumbprint == _certificate.Thumbprint
            };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        }
        private async Task Serve()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var tls = new SslStream(connection.GetStream());
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    { ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, _stop.Token);
                    using var reader = new StreamReader(tls, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var first = await reader.ReadLineAsync(_stop.Token);
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                    Interlocked.Increment(ref _requests);
                    var path = first?.Split(' ').ElementAtOrDefault(1) ?? "";
                    var found = _files.TryGetValue(path, out var body);
                    body ??= Array.Empty<byte>();
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Length: {body.Length}\r\nContent-Type: application/octet-stream\r\nETag: \"fixture-v1\"\r\nConnection: close\r\n\r\n");
                    await tls.WriteAsync(header, _stop.Token);
                    await tls.WriteAsync(body, _stop.Token);
                    await tls.FlushAsync(_stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }
        public void Dispose()
        {
            _stop.Cancel(); _listener.Stop();
            _loop.GetAwaiter().GetResult();
            _certificate.Dispose(); _stop.Dispose();
        }
    }
}
