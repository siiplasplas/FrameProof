using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using FrameProof.Core.Downloads;
using FrameProof.Core.Library;
using FrameProof.Core.Packages;

namespace FrameProof.Core.Installation;

/// <summary>Persists intent before every external mutation. All callers in an app must share jobsRoot.</summary>
public sealed class GameInstallCoordinator
{
    private readonly HttpPackageDownloader _downloader;
    private readonly LocalPackageInstaller _installer;
    private readonly GameLibraryStore _library;
    private readonly string _root;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public GameInstallCoordinator(HttpPackageDownloader downloader, LocalPackageInstaller installer,
        GameLibraryStore library, string jobsRoot)
    {
        _downloader = downloader;
        _installer = installer;
        _library = library;
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(jobsRoot));
        NoLinks(_root);
        Directory.CreateDirectory(_root);
    }

    public async Task<GameEntry> InstallAsync(GameInstallRecipe recipe, string targetDirectory,
        CancellationToken cancellationToken = default, IProgress<GameInstallProgress>? progress = null)
    {
        Validate(recipe);
        var target = Target(targetDirectory);
        using var lease = Lock();
        if (ReadJobs().Any(x => x.Phase is not ("Completed" or "Failed") && Overlaps(x.Target, target)))
            throw new InvalidOperationException("Bu oyun için yarım kalan işlem var. Önce devam edin veya geri alın.");
        var job = new Job { Recipe = recipe, Target = target };
        Save(job);
        return await ExecuteAsync(job, cancellationToken, progress).ConfigureAwait(false);
    }

    /// <summary>Resume reuses verified downloaded bytes. Abandon reverses fix then game; completed jobs cannot be abandoned.</summary>
    public async Task<GameEntry?> RecoverAsync(string jobId, bool resume, CancellationToken cancellationToken = default,
        IProgress<GameInstallProgress>? progress = null)
    {
        using var lease = Lock();
        var job = Read(jobId);
        if (job.Phase == "Completed")
        {
            if (!resume) throw new InvalidOperationException("Tamamlanmış kurulum bu işlemle geri alınamaz.");
            return _library.Load().SingleOrDefault(x => x.CatalogId == job.Recipe.CatalogId && x.InstallDirectory.Equals(job.Target, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Tamamlanan oyunun kütüphane kaydı artık yok.");
        }
        if (job.Phase == "Committing")
        {
            if (!resume) throw new InvalidOperationException("Kaydı tamamlanan kurulumu bu işlemle geri alamazsınız.");
            return Commit(job, progress);
        }
        if (!resume)
        {
            await Task.Run(() => Rollback(job, progress), CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        // Recover an interrupted package write before restarting. Completed packages are reused.
        var game = _installer.GetTransaction(job.GameTransactionId);
        var fix = _installer.GetTransaction(job.FixTransactionId);
        if (job.Phase == "RollingBack" || game?.State is "Installing" or "RollingBack" || fix?.State is "Installing" or "RollingBack")
            await Task.Run(() => Rollback(job, progress), CancellationToken.None).ConfigureAwait(false);
        return await ExecuteAsync(job, cancellationToken, progress).ConfigureAwait(false);
    }

    public IReadOnlyList<GameInstallJobInfo> GetJobs() => ReadJobs()
        .Select(x => new GameInstallJobInfo(x.Id, x.Recipe.CatalogId, x.Recipe.Name, x.Target, x.Phase)).ToArray();

    private async Task<GameEntry> ExecuteAsync(Job job, CancellationToken token, IProgress<GameInstallProgress>? progress)
    {
        Validate(job.Recipe);
        Target(job.Target);
        try
        {
            Preflight(job);
            token.ThrowIfCancellationRequested();
            job.Phase = "Downloading";
            Save(job);
            var gameZip = await _downloader.DownloadAsync(job.Recipe.GamePackage, token,
                new InlineProgress<DownloadProgress>(p => progress?.Report(new("Oyun indiriliyor", p.ReceivedBytes, p.TotalBytes)))).ConfigureAwait(false);
            string? fixZip = null;
            if (job.Recipe.FixPackage is { } fix)
                fixZip = await _downloader.DownloadAsync(fix, token,
                    new InlineProgress<DownloadProgress>(p => progress?.Report(new("Fix indiriliyor", p.ReceivedBytes, p.TotalBytes)))).ConfigureAwait(false);
            var prior = _library.Load().SingleOrDefault(x => x.InstallDirectory.Equals(job.Target, StringComparison.OrdinalIgnoreCase));
            if (prior != null)
            {
                if (prior.CatalogId != null && prior.CatalogId != job.Recipe.CatalogId)
                    throw new InvalidOperationException("Hedef klasör farklı bir katalog oyununa ait.");
                job.LibraryEntryId = prior.Id;
                Save(job);
                _library.SetInstallationPending(prior.Id, true);
            }
            await Task.Run(() =>
            {
                InstallPackage(job, false, gameZip, token, progress);
                if (fixZip != null) InstallPackage(job, true, fixZip, token, progress);
                token.ThrowIfCancellationRequested();
                // Validate executable before entering irreversible library commit phase.
                _ = GameLauncher.Validate(Profile(job));
            }, token).ConfigureAwait(false);
        }
        catch (Exception original)
        {
            try { await Task.Run(() => Rollback(job, progress), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception rollback) { throw new AggregateException($"Kurulum kurtarma gerektiriyor: {job.Id}", original, rollback); }
            throw;
        }
        // After this persisted phase, recovery completes registration, never rolls back files behind a ready entry.
        job.Phase = "Committing";
        Save(job);
        return Commit(job, progress);
    }

    private void InstallPackage(Job job, bool fix, string zip, CancellationToken token, IProgress<GameInstallProgress>? progress)
    {
        var id = fix ? job.FixTransactionId : job.GameTransactionId;
        var existing = _installer.GetTransaction(id);
        if (existing?.State == "Installed") return;
        if (existing != null && existing.State != "RolledBack") throw new InvalidOperationException("Yarım kalan paket önce kurtarılmalı.");
        if (existing != null || Directory.Exists(Path.Combine(_installer.TransactionRoot, id)))
        {
            // A crash between transaction-directory creation and its first journal cannot have touched game files.
            id = Guid.NewGuid().ToString("N");
            if (fix) job.FixTransactionId = id; else job.GameTransactionId = id;
        }
        job.Phase = fix ? "InstallingFix" : "InstallingGame";
        Save(job); // predetermined package transaction ID persisted before installer begins
        var request = fix ? job.Recipe.FixPackage! : job.Recipe.GamePackage;
        var recipe = new PackageRecipe(request.Id, fix ? job.Recipe.FixVersion! : job.Recipe.GameVersion,
            job.Recipe.GameVersion, request.Sha256)
        { MaximumExpandedBytes = fix ? job.Recipe.MaximumFixExpandedBytes : job.Recipe.MaximumGameExpandedBytes };
        _installer.Install(zip, job.Target, recipe, job.Recipe.GameVersion, token,
            new InlineProgress<PackageProgress>(p => progress?.Report(new(fix ? "Fix uygulanıyor" : "Oyun kuruluyor", 0, 0, p.CompletedFiles, p.TotalFiles))), id);
    }

    private GameEntry Commit(Job job, IProgress<GameInstallProgress>? progress)
    {
        if (_installer.GetTransaction(job.GameTransactionId)?.State != "Installed" ||
            job.Recipe.FixPackage != null && _installer.GetTransaction(job.FixTransactionId)?.State != "Installed")
            throw new InvalidDataException("Oyun ve fix kurulumu tamamlanmadan hazır kaydı oluşturulamaz.");
        VerifyInstalledBytes(job);
        progress?.Report(new("Kütüphaneye kaydediliyor"));
        var entry = _library.RegisterInstalled(job.Recipe.CatalogId, job.Recipe.Name, job.Target, Profile(job),
            job.Recipe.GameVersion, job.Recipe.FixVersion);
        job.Phase = "Completed";
        Save(job);
        progress?.Report(new("Hazır"));
        return entry;
    }

    private void VerifyInstalledBytes(Job job)
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in job.Recipe.FixPackage == null ? new[] { job.GameTransactionId } : new[] { job.GameTransactionId, job.FixTransactionId })
        {
            var tx = _installer.GetTransaction(id)!;
            if (!tx.Target.Equals(job.Target, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Paket hedefi kurulum kaydıyla eşleşmiyor.");
            foreach (var file in tx.Files) expected[Path.GetFullPath(Path.Combine(job.Target, file.Relative))] = file.NewHash;
        }
        foreach (var pair in expected)
        {
            NoLinks(pair.Key);
            using var stream = File.OpenRead(pair.Key);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
                throw new PackageConflictException("Kurulan dosya sonradan değişti; oyun hazır olarak kaydedilmedi.");
        }
    }

    private void Rollback(Job job, IProgress<GameInstallProgress>? progress)
    {
        job.Phase = "RollingBack";
        Save(job);
        progress?.Report(new("Geri alınıyor"));
        foreach (var id in new[] { job.FixTransactionId, job.GameTransactionId })
            if (_installer.GetTransaction(id) is { State: not "RolledBack" }) _installer.Recover(id);
        if (job.LibraryEntryId != null && _library.Load().Any(x => x.Id == job.LibraryEntryId))
            _library.SetInstallationPending(job.LibraryEntryId, false);
        job.Phase = "Failed";
        Save(job);
    }

    private void Preflight(Job job)
    {
        // Deliberately conservative: ZIP + staging + replacement + original backup for both packages.
        long needed = checked(job.Recipe.GamePackage.SizeBytes + (job.Recipe.FixPackage?.SizeBytes ?? 0) +
            3 * checked(job.Recipe.MaximumGameExpandedBytes + (job.Recipe.FixPackage == null ? 0 : job.Recipe.MaximumFixExpandedBytes)));
        var roots = new[] { Path.GetPathRoot(job.Target), Path.GetPathRoot(_root), Path.GetPathRoot(_installer.TransactionRoot), Path.GetPathRoot(_downloader.CacheRoot) }
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var disk = new DriveInfo(root!);
            if (!disk.IsReady || disk.AvailableFreeSpace < needed) throw new IOException("Oyun, geçici dosyalar ve geri alma yedeği için yeterli boş alan yok.");
        }
    }

    private static GameLaunchProfile Profile(Job job) => new()
    {
        ExecutablePath = Path.GetFullPath(Path.Combine(job.Target, job.Recipe.LaunchRelativePath)),
        WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(job.Target, job.Recipe.LaunchRelativePath)))!,
        Arguments = job.Recipe.Arguments
    };

    private static void Validate(GameInstallRecipe recipe)
    {
        Catalog.GameCatalogClient.ValidateRecipe(recipe);
    }

    private string Target(string value)
    {
        if (!Path.IsPathFullyQualified(value)) throw new InvalidDataException("Tam kurulum yolu gerekli.");
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        NoLinks(path);
        if (Overlaps(path, _root) || Overlaps(path, Path.TrimEndingDirectorySeparator(_downloader.CacheRoot)) ||
            Overlaps(path, _installer.TransactionRoot) || path.Equals(Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Oyun klasörü işlem kayıtlarından ayrı olmalı.");
        return path;
    }

    private static bool Overlaps(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
        a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private FileStream Lock() { var path = Path.Combine(_root, "jobs.lock"); NoLinks(path); return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    private string JobPath(string id)
    {
        if (!Regex.IsMatch(id, "\\A[0-9a-f]{32}\\z")) throw new InvalidDataException("İşlem kimliği geçersiz.");
        var path = Path.Combine(_root, id + ".json"); NoLinks(path); return path;
    }
    private IEnumerable<Job> ReadJobs() => Directory.EnumerateFiles(_root, "*.json")
        .Where(x => Regex.IsMatch(Path.GetFileNameWithoutExtension(x), "\\A[0-9a-f]{32}\\z"))
        .Select(x => Read(Path.GetFileNameWithoutExtension(x)));
    private Job Read(string id)
    {
        var job = JsonSerializer.Deserialize<Job>(File.ReadAllText(JobPath(id))) ?? throw new InvalidDataException("Kurulum kaydı okunamadı.");
        if (job.Id != id || job.Phase is not ("Downloading" or "InstallingGame" or "InstallingFix" or "Committing" or "Completed" or "RollingBack" or "Failed"))
            throw new InvalidDataException("Kurulum kaydı geçersiz.");
        Validate(job.Recipe); Target(job.Target);
        return job;
    }
    private void Save(Job job)
    {
        var path = JobPath(job.Id); var temp = path + ".tmp"; NoLinks(temp);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, job, Json); stream.Flush(true); }
        File.Move(temp, path, true);
    }
    private static void NoLinks(string value)
    {
        for (string? path = Path.GetFullPath(value); path != null; path = Path.GetDirectoryName(path))
        {
            try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Bağlantı veya junction yolu desteklenmiyor."); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    public sealed class Job
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public GameInstallRecipe Recipe { get; set; } = new();
        public string Target { get; set; } = "";
        public string Phase { get; set; } = "Downloading";
        public string GameTransactionId { get; set; } = Guid.NewGuid().ToString("N");
        public string FixTransactionId { get; set; } = Guid.NewGuid().ToString("N");
        public string? LibraryEntryId { get; set; }
    }
}
