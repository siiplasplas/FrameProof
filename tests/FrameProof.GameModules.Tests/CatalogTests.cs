using System.Net;
using System.Text;
using System.Text.Json;
using FrameProof.Core.Catalog;

internal static class CatalogTests
{
    public static void Run(Suite suite)
    {
        suite.Run("Catalog accepts explicit HTTPS game and fix recipe", _ =>
        {
            var catalog = GameCatalogClient.Parse(Encoding.UTF8.GetBytes(Json()));
            Suite.Check(catalog.Games.Count == 1 && catalog.Games[0].CatalogId == "sample-game" && catalog.Games[0].FixVersion == "fix-1", "Catalog fields lost");
        });
        suite.Run("Catalog rejects unknown schema and duplicate game identities", _ =>
        {
            Reject(Json().Replace("\"schemaVersion\":1", "\"schemaVersion\":2"));
            Reject(Json(duplicate: true));
            Reject(Json().Replace("\"schemaVersion\":1,", ""));
        });
        suite.Run("Catalog rejects unsafe launch paths, hashes and package URLs", _ =>
        {
            foreach (var path in new[] { "../game.exe", "C:/game.exe", "bin/CON.exe", "bin/game.exe:stream", "bin//game.exe" }) Reject(Json(launch: path));
            Reject(Json(url: "http://example.test/game.zip"));
            Reject(Json(url: "https://user:secret@example.test/game.zip"));
            Reject(Json(hash: "not-a-hash"));
            Reject(Json(size: 0));
        });
        suite.Run("Catalog enforces declared and streamed body limits", _ =>
        {
            using var client = new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[GameCatalogClient.MaximumCatalogBytes + 1]),
            }));
            Suite.Throws<InvalidDataException>(() => new GameCatalogClient(client).LoadAsync(new Uri("https://example.test/catalog.json")).GetAwaiter().GetResult());
            using var streamed = new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnknownLengthContent(new byte[GameCatalogClient.MaximumCatalogBytes + 1]),
            }));
            Suite.Throws<InvalidDataException>(() => new GameCatalogClient(streamed).LoadAsync(new Uri("https://example.test/catalog.json")).GetAwaiter().GetResult());
        });
        suite.Run("Catalog refuses insecure config without making a request", _ =>
        {
            var calls = 0;
            using var client = new HttpClient(new Stub(_ => { calls++; return new(HttpStatusCode.OK) { Content = new StringContent(Json()) }; }));
            Suite.Throws<InvalidDataException>(() => new GameCatalogClient(client).LoadAsync(new Uri("http://example.test/catalog.json")).GetAwaiter().GetResult());
            Suite.Check(calls == 0, "Insecure request was sent");
            var result = new GameCatalogClient(client).LoadAsync(new Uri("https://example.test/catalog.json")).GetAwaiter().GetResult();
            Suite.Check(calls == 1 && result.Games.Count == 1, "Configured source did not load");
        });
    }

    private static void Reject(string json) => Suite.Throws<InvalidDataException>(() => GameCatalogClient.Parse(Encoding.UTF8.GetBytes(json)));

    private static string Json(string launch = "bin/game.exe", string url = "https://example.test/game.zip", string? hash = null, long size = 100, bool duplicate = false)
    {
        var recipe = new
        {
            catalogId = "sample-game", name = "Sample game", gameVersion = "1.0", fixVersion = "fix-1",
            gamePackage = new { id = "game-1", url, sha256 = hash ?? new string('a', 64), sizeBytes = size },
            fixPackage = new { id = "fix-1", url = "https://example.test/fix.zip", sha256 = new string('b', 64), sizeBytes = 50 },
            launchRelativePath = launch, arguments = Array.Empty<string>(), maximumGameExpandedBytes = 1000, maximumFixExpandedBytes = 500,
        };
        return JsonSerializer.Serialize(new { schemaVersion = 1, games = duplicate ? new[] { recipe, recipe } : new[] { recipe } });
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
}
