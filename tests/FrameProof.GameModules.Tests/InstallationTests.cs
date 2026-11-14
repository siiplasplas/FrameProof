using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FrameProof.Core.Downloads;
using FrameProof.Core.Installation;
using FrameProof.Core.Library;
using FrameProof.Core.Packages;

internal static class InstallationTests
{
    public static void Run(Suite suite)
    {
        suite.Run("Automatic game + fix installs and atomically registers paired versions", root =>
        {
            using var f = new Fixture(root);
            var entry = f.Coordinator.InstallAsync(f.Recipe, f.Target).GetAwaiter().GetResult();
            Suite.Check(entry.CanLaunch && entry.InstalledGameVersion == "1" && entry.InstalledFixVersion == "f1", "Ready entry incomplete");
            Suite.Check(File.ReadAllText(Path.Combine(f.Target, "shared.txt")) == "fixed", "Fix missing");
            Suite.Check(f.Coordinator.GetJobs().Single().Phase == "Completed", "Job incomplete");
        });
        suite.Run("Fix failure restores game originals and never registers ready", root =>
        {
            using var f = new Fixture(root, invalidFix: true);
            Directory.CreateDirectory(f.Target);
            File.WriteAllText(Path.Combine(f.Target, "shared.txt"), "original");
            Suite.Throws<InvalidDataException>(() => f.Coordinator.InstallAsync(f.Recipe, f.Target).GetAwaiter().GetResult());
            Suite.Check(File.ReadAllText(Path.Combine(f.Target, "shared.txt")) == "original", "Original lost");
            Suite.Check(!File.Exists(Path.Combine(f.Target, "game.exe")) && f.Library.Load().Count == 0, "Partial game ready");
        });
        suite.Run("Cancellation after game write rolls back and can retry", root =>
        {
            using var f = new Fixture(root);
            using var cts = new CancellationTokenSource();
            var p = new Immediate(p => { if (p.Stage == "Oyun kuruluyor" && p.CompletedFiles > 0) cts.Cancel(); });
            Suite.Throws<OperationCanceledException>(() => f.Coordinator.InstallAsync(f.Recipe, f.Target, cts.Token, p).GetAwaiter().GetResult());
            Suite.Check(f.Library.Load().Count == 0 && !File.Exists(Path.Combine(f.Target, "game.exe")), "Cancelled game ready");
            var job = f.Coordinator.GetJobs().Single();
            var resumed = f.Coordinator.RecoverAsync(job.JobId, true).GetAwaiter().GetResult();
            Suite.Check(resumed?.CanLaunch == true && f.Library.Load().Count == 1, "Retry did not recover");
        });
        suite.Run("Persisted package success before job phase save resumes without reinstall", root =>
        {
            using var f = new Fixture(root);
            var job = new GameInstallCoordinator.Job { Recipe = f.Recipe, Target = f.Target, Phase = "InstallingGame" };
            var zip = Path.Combine(root, "fixture.zip"); File.WriteAllBytes(zip, f.GameBytes);
            f.Installer.Install(zip, f.Target, new("game", "1", "1", f.Recipe.GamePackage.Sha256), "1", transactionId: job.GameTransactionId);
            File.WriteAllText(Path.Combine(root, "jobs", job.Id + ".json"), JsonSerializer.Serialize(job));
            var entry = f.Coordinator.RecoverAsync(job.Id, true).GetAwaiter().GetResult();
            Suite.Check(entry?.InstalledFixVersion == "f1", "Recovery did not complete pair");
            Suite.Check(Directory.EnumerateDirectories(Path.Combine(root, "transactions")).Count() == 2, "Game was installed twice");
        });
        suite.Run("Committing phase recovers idempotently without duplicate library entries", root =>
        {
            using var f = new Fixture(root);
            var first = f.Coordinator.InstallAsync(f.Recipe, f.Target).GetAwaiter().GetResult();
            var id = f.Coordinator.GetJobs().Single().JobId;
            var path = Path.Combine(root, "jobs", id + ".json");
            var job = JsonSerializer.Deserialize<GameInstallCoordinator.Job>(File.ReadAllText(path))!;
            job.Phase = "Committing"; File.WriteAllText(path, JsonSerializer.Serialize(job));
            var second = f.Coordinator.RecoverAsync(id, true).GetAwaiter().GetResult();
            Suite.Check(first.Id == second?.Id && f.Library.Load().Count == 1, "Commit replay duplicate");
        });
        suite.Run("Missing launch EXE rolls back both packages", root =>
        {
            using var f = new Fixture(root);
            Suite.Throws<FileNotFoundException>(() => f.Coordinator.InstallAsync(f.Recipe with { LaunchRelativePath = "missing.exe" }, f.Target).GetAwaiter().GetResult());
            Suite.Check(f.Library.Load().Count == 0 && !File.Exists(Path.Combine(f.Target, "game.exe")), "Missing EXE installation left ready");
        });
        suite.Run("Completed old job recovery preserves newer library version", root =>
        {
            using var f = new Fixture(root);
            var first = f.Coordinator.InstallAsync(f.Recipe, f.Target).GetAwaiter().GetResult();
            var id = f.Coordinator.GetJobs().Single().JobId;
            f.Library.SetPackageVersions(first.Id, "fixture", "newer", "newer-fix");
            var result = f.Coordinator.RecoverAsync(id, true).GetAwaiter().GetResult();
            Suite.Check(result?.InstalledGameVersion == "newer", "Old job downgraded library metadata");
        });
        suite.Run("Committing recovery refuses externally changed installed bytes", root =>
        {
            using var f = new Fixture(root);
            f.Coordinator.InstallAsync(f.Recipe, f.Target).GetAwaiter().GetResult();
            var id = f.Coordinator.GetJobs().Single().JobId;
            var path = Path.Combine(root, "jobs", id + ".json");
            var job = JsonSerializer.Deserialize<GameInstallCoordinator.Job>(File.ReadAllText(path))!;
            job.Phase = "Committing"; File.WriteAllText(path, JsonSerializer.Serialize(job));
            File.WriteAllText(Path.Combine(f.Target, "shared.txt"), "external");
            Suite.Throws<PackageConflictException>(() => f.Coordinator.RecoverAsync(id, true).GetAwaiter().GetResult());
        });
        suite.Run("Update launch interlock clears after complete rollback", root =>
        {
            using var f = new Fixture(root, invalidFix: true);
            var original = f.Library.AddManual(Suite.Exe(root, "target/game.exe"));
            var sawPending = false;
            var p = new Immediate(p => { if (p.Stage == "Oyun kuruluyor") sawPending |= !f.Library.Load().Single().CanLaunch; });
            Suite.Throws<InvalidDataException>(() => f.Coordinator.InstallAsync(f.Recipe, f.Target, progress: p).GetAwaiter().GetResult());
            var restored = f.Library.Load().Single();
            Suite.Check(sawPending && restored.CanLaunch && !restored.InstallationPending && restored.Id == original.Id, "Update pending interlock broken");
        });
        suite.Run("Coordinator refuses targets overlapping cache before downloading", root =>
        {
            using var f = new Fixture(root);
            Suite.Throws<InvalidDataException>(() => f.Coordinator.InstallAsync(f.Recipe, Path.Combine(root, "cache", "game")).GetAwaiter().GetResult());
            Suite.Check(f.Coordinator.GetJobs().Count == 0, "Invalid target created job");
        });
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http;
        public string Target { get; }
        public byte[] GameBytes { get; }
        public GameInstallRecipe Recipe { get; }
        public GameLibraryStore Library { get; }
        public LocalPackageInstaller Installer { get; }
        public GameInstallCoordinator Coordinator { get; }
        public Fixture(string root, bool invalidFix = false)
        {
            Target = Path.Combine(root, "target");
            GameBytes = Archive(("game.exe", "fixture-never-executed"), ("shared.txt", "game"));
            var fix = Archive((invalidFix ? "../escape.txt" : "shared.txt", "fixed"));
            _http = new HttpClient(new Handler(new() { ["/game"] = GameBytes, ["/fix"] = fix }));
            Recipe = new()
            {
                CatalogId = "fixture", Name = "Fixture", GameVersion = "1", FixVersion = "f1",
                GamePackage = new("game", new Uri("https://fixture.invalid/game"), Convert.ToHexString(SHA256.HashData(GameBytes)), GameBytes.Length),
                FixPackage = new("fix", new Uri("https://fixture.invalid/fix"), Convert.ToHexString(SHA256.HashData(fix)), fix.Length),
                LaunchRelativePath = "game.exe", MaximumGameExpandedBytes = 1024, MaximumFixExpandedBytes = 1024
            };
            Library = new(Path.Combine(root, "library.json"));
            Installer = new(Path.Combine(root, "transactions"), _ => { });
            Coordinator = new(new(_http, Path.Combine(root, "cache")), Installer, Library, Path.Combine(root, "jobs"));
        }
        public void Dispose() => _http.Dispose();
    }
    private static byte[] Archive(params (string Path, string Data)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var entry in entries) { using var writer = new StreamWriter(zip.CreateEntry(entry.Path).Open()); writer.Write(entry.Data); }
        return stream.ToArray();
    }
    private sealed class Handler(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(files[request.RequestUri!.AbsolutePath]), RequestMessage = request });
    }
    private sealed class Immediate(Action<GameInstallProgress> callback) : IProgress<GameInstallProgress>
    { public void Report(GameInstallProgress value) => callback(value); }
}
