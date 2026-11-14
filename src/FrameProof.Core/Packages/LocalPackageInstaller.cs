using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FrameProof.Core.Packages;

/// <summary>
/// Local ZIP installer. Call from a worker thread. Use one persistent transaction root per app.
/// The required guard must throw when the game is running or its state cannot be established.
/// Backups are deliberately retained after undo. No downloaded code or scripts are executed.
/// </summary>
public sealed class LocalPackageInstaller
{
    private readonly string _root;
    public string TransactionRoot => _root;
    private readonly Action<string> _ensureGameStopped;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public LocalPackageInstaller(string transactionRoot, Action<string> ensureGameStopped)
    {
        _root = Full(transactionRoot);
        _ensureGameStopped = ensureGameStopped ?? throw new ArgumentNullException(nameof(ensureGameStopped));
        NoLinks(_root);
        Directory.CreateDirectory(_root);
    }

    public PackageInstallResult Install(string zipPath, string targetDirectory, PackageRecipe recipe,
        string actualGameVersion, CancellationToken cancellationToken = default, IProgress<PackageProgress>? progress = null,
        string? transactionId = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (string.IsNullOrWhiteSpace(recipe.PackageId) || string.IsNullOrWhiteSpace(recipe.Version) ||
            string.IsNullOrWhiteSpace(recipe.GameVersion) || recipe.GameVersion != actualGameVersion)
            throw new InvalidDataException("Paket ve oyun sürümü eşleşmiyor.");
        if (!Regex.IsMatch(recipe.Sha256 ?? "", "\\A[0-9a-fA-F]{64}\\z") || recipe.MaximumExpandedBytes <= 0 || recipe.MaximumFiles <= 0)
            throw new InvalidDataException("Paket doğrulama bilgileri geçersiz.");
        var target = ValidateTarget(targetDirectory);
        using var lease = Acquire(target);
        foreach (var manifest in Manifests())
        {
            var prior = Read(manifest);
            if (prior.Target.Equals(target, StringComparison.OrdinalIgnoreCase) && prior.State is "Installing" or "RollingBack")
                throw new InvalidOperationException($"Önce yarım kalan işlemi kurtarın: {prior.Id}");
        }
        EnsureStopped(target);
        cancellationToken.ThrowIfCancellationRequested();
        // Keep the source locked while hashing and reading; never reopen mutable bytes after validation.
        using var source = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!Hash(source, cancellationToken).Equals(recipe.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Paket SHA256 doğrulaması başarısız.");
        source.Position = 0;
        var tx = new Transaction { Id = transactionId ?? Guid.NewGuid().ToString("N"), Target = target, PackageId = recipe.PackageId, Version = recipe.Version };
        var home = Home(tx.Id);
        if (Directory.Exists(home)) throw new InvalidOperationException("İşlem kimliği daha önce kullanılmış.");
        Directory.CreateDirectory(home);
        Save(tx);
        try
        {
            using (var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true))
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long expanded = 0;
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (archive.Entries.Count > recipe.MaximumFiles) throw new InvalidDataException("Paket çok fazla giriş içeriyor.");
                    var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                    if (unixType != 0 && unixType != 0x8000 && unixType != 0x4000 ||
                        (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Paket bağlantı veya özel dosya içeriyor.");
                    var relative = SafeRelative(entry.FullName.TrimEnd('/', '\\'));
                    if (!names.Add(relative)) throw new InvalidDataException("Paket yinelenen dosya yolları içeriyor.");
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                    expanded = checked(expanded + entry.Length);
                    if (expanded > recipe.MaximumExpandedBytes) throw new InvalidDataException("Açılmış paket boyutu sınırı aşıldı.");
                    var destination = Resolve(target, relative);
                    NoLinks(destination);
                    if (Directory.Exists(destination)) throw new IOException("Dosya hedefinde klasör bulunuyor.");
                    var index = tx.Files.Count;
                    var staged = Path.Combine(home, $"stage-{index}");
                    EnsureFreeSpace(home, entry.Length);
                    using (var input = entry.Open())
                    using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        long copied = 0;
                        int count;
                        while ((count = input.Read(buffer)) != 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            copied += count;
                            if (copied > entry.Length) throw new InvalidDataException("Paket giriş boyutu tutarsız.");
                            output.Write(buffer, 0, count);
                        }
                        if (copied != entry.Length) throw new InvalidDataException("Paket girişi eksik.");
                        output.Flush(true);
                    }
                    tx.Files.Add(new FileChange { Relative = relative, NewHash = HashFile(staged, cancellationToken), Index = index });
                    progress?.Report(new("Doğrulanıyor", tx.Files.Count, archive.Entries.Count));
                }
            }
            if (tx.Files.Count == 0) throw new InvalidDataException("Paket boş.");
            // Detect file/directory collisions before touching the target.
            var filePaths = tx.Files.Select(x => x.Relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in tx.Files)
                for (var parent = Path.GetDirectoryName(file.Relative); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                    if (filePaths.Contains(parent)) throw new InvalidDataException("Paket dosya ve klasör yolları çakışıyor.");
            Save(tx);
            EnsureStopped(target);
            foreach (var file in tx.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Resolve(target, file.Relative);
                NoLinks(destination);
                EnsureDirectories(tx, Path.GetDirectoryName(destination)!);
                file.Existed = File.Exists(destination);
                var originalBytes = file.Existed ? new FileInfo(destination).Length : 0;
                var replacementBytes = new FileInfo(Path.Combine(home, $"stage-{file.Index}")).Length;
                if (string.Equals(Path.GetPathRoot(home), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
                    EnsureFreeSpace(home, checked(originalBytes + replacementBytes));
                else { EnsureFreeSpace(home, originalBytes); EnsureFreeSpace(destination, replacementBytes); }
                if (file.Existed)
                {
                    var backup = Path.Combine(home, $"backup-{file.Index}");
                    CopyDurable(destination, backup, cancellationToken);
                    file.OldHash = HashFile(backup, cancellationToken);
                    if (HashFile(destination, cancellationToken) != file.OldHash) throw new PackageConflictException("Dosya yedekleme sırasında değişti.");
                }
                file.Pending = true;
                Save(tx); // Write-ahead log: recovery accepts either the original or replacement hash.
                Replace(Path.Combine(home, $"stage-{file.Index}"), destination, tx.Id, cancellationToken);
                progress?.Report(new("Kuruluyor", file.Index + 1, tx.Files.Count));
            }
            cancellationToken.ThrowIfCancellationRequested();
            tx.State = "Installed";
            Save(tx);
            CleanupStages(tx);
            return new(tx.Id, tx.PackageId, tx.Version, tx.Files.Count);
        }
        catch (Exception original)
        {
            try { Rollback(tx); }
            catch (Exception rollback) { throw new AggregateException($"Kurulum durdu; kurtarma gerekiyor: {tx.Id}", original, rollback); }
            throw;
        }
    }

    public void Undo(string transactionId)
    {
        var tx = Read(Path.Combine(Home(transactionId), "transaction.json"));
        ValidateTransaction(tx, transactionId);
        using var lease = Acquire(tx.Target);
        tx = Read(Path.Combine(Home(transactionId), "transaction.json"));
        ValidateTransaction(tx, transactionId);
        if (tx.State == "RolledBack") { CleanupStages(tx); return; }
        var touched = tx.Files.Select(x => Resolve(tx.Target, x.Relative)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in Manifests())
        {
            var other = Read(manifest);
            if (other.Id == tx.Id || other.State == "RolledBack" || other.CreatedAt < tx.CreatedAt) continue;
            if (other.Files.Any(x => x.Pending && touched.Contains(Resolve(other.Target, x.Relative))))
                throw new PackageConflictException($"Önce sonraki paketi geri alın: {other.Id}");
        }
        EnsureStopped(tx.Target);
        Rollback(tx);
    }

    /// <summary>Also works after a process crash during installation or undo. Conflicts never overwrite external edits.</summary>
    public void Recover(string transactionId) => Undo(transactionId);

    public Transaction? GetTransaction(string transactionId)
    {
        var path = Path.Combine(Home(transactionId), "transaction.json");
        if (!File.Exists(path)) return null;
        var tx = Read(path);
        ValidateTransaction(tx, transactionId);
        return tx;
    }

    private void EnsureStopped(string target)
    {
        // Native process inventory is also checked even if a caller accidentally supplies a no-op guard.
        var running = Games.GameCompress.RunningFrom(target);
        if (running != null) throw new IOException($"Oyun klasöründen çalışan uygulamayı kapatın: {running}");
        _ensureGameStopped(target);
    }

    private IEnumerable<string> Manifests() => Directory.EnumerateDirectories(_root)
        .Where(x => Regex.IsMatch(Path.GetFileName(x), "\\A[0-9a-f]{32}\\z"))
        .Where(x => File.Exists(Path.Combine(x, "transaction.json")))
        .Select(x => Path.Combine(x, "transaction.json"));

    public IReadOnlyList<string> FindInterruptedTransactions() => Manifests().Select(Read)
        .Where(x => x.State is "Installing" or "RollingBack").Select(x => x.Id).ToArray();

    private void Rollback(Transaction tx)
    {
        // Refuse before any rollback mutation when any file has been changed by another app/user.
        foreach (var file in tx.Files.Where(x => x.Pending))
        {
            var path = Resolve(tx.Target, file.Relative);
            NoLinks(path);
            var stale = path + ".frameproof-" + tx.Id + ".tmp";
            NoLinks(stale);
            if (File.Exists(stale)) File.Delete(stale); // Reserved by this persisted pending operation.
            var current = File.Exists(path) ? HashFile(path) : null;
            if (current != file.NewHash && current != file.OldHash)
                throw new PackageConflictException($"Dosya sonradan değişti; üzerine yazılmadı: {file.Relative}");
            if (file.Existed && HashFile(Path.Combine(Home(tx.Id), $"backup-{file.Index}")) != file.OldHash)
                throw new InvalidDataException("Geri alma yedeği doğrulanamadı.");
        }
        tx.State = "RollingBack";
        Save(tx);
        foreach (var file in tx.Files.Where(x => x.Pending).Reverse())
        {
            var path = Resolve(tx.Target, file.Relative);
            NoLinks(path);
            var current = File.Exists(path) ? HashFile(path) : null;
            if (current != file.NewHash && current != file.OldHash) throw new PackageConflictException($"Dosya geri alma sırasında değişti: {file.Relative}");
            if (current != file.OldHash)
            {
                if (file.Existed) Replace(Path.Combine(Home(tx.Id), $"backup-{file.Index}"), path, tx.Id);
                else File.Delete(path);
            }
            file.Pending = false;
            Save(tx);
        }
        foreach (var path in tx.CreatedDirectories.AsEnumerable().Reverse())
        {
            if (path != tx.Target && !path.StartsWith(tx.Target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Geçersiz işlem klasörü.");
            NoLinks(path);
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
        tx.State = "RolledBack";
        Save(tx);
        CleanupStages(tx);
    }

    private void CleanupStages(Transaction tx)
    {
        // Completion is already durable. A cleanup error must never undo a successful install.
        try
        {
            foreach (var file in Directory.EnumerateFiles(Home(tx.Id), "stage-*"))
            {
                try { NoLinks(file); File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void EnsureDirectories(Transaction tx, string path)
    {
        NoLinks(path);
        if (Directory.Exists(path)) return;
        if (path != tx.Target) EnsureDirectories(tx, Path.GetDirectoryName(path)!);
        tx.CreatedDirectories.Add(path);
        Save(tx);
        Directory.CreateDirectory(path);
    }

    private void ValidateTransaction(Transaction tx, string id)
    {
        if (tx.Id != id || ValidateTarget(tx.Target) != tx.Target || tx.State is not ("Installed" or "Installing" or "RollingBack" or "RolledBack"))
            throw new InvalidDataException("İşlem kaydı geçersiz.");
        if (tx.Files.Select(x => x.Index).Distinct().Count() != tx.Files.Count || tx.Files.Any(x => x.Index < 0))
            throw new InvalidDataException("İşlem dosya kayıtları geçersiz.");
        foreach (var file in tx.Files) _ = Resolve(tx.Target, file.Relative);
    }

    private string ValidateTarget(string path)
    {
        var full = Full(path);
        NoLinks(full);
        if (full == Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar) ||
            full.Equals(_root, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            _root.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Oyun klasörü ve işlem yedekleri ayrı konumlarda olmalı.");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows) && (full.Equals(windows, StringComparison.OrdinalIgnoreCase) || full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Windows klasörüne paket kurulamaz.");
        return full;
    }

    private FileStream Acquire(string target)
    {
        NoLinks(_root);
        // One writer per app transaction root also protects nested/overlapping game folders.
        var path = Path.Combine(_root, "installer.lock");
        NoLinks(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private string Home(string id)
    {
        if (!Regex.IsMatch(id, "\\A[0-9a-f]{32}\\z")) throw new ArgumentException("Geçersiz işlem kimliği.", nameof(id));
        var path = Path.Combine(_root, id);
        NoLinks(path);
        return path;
    }

    private void Save(Transaction tx)
    {
        var path = Path.Combine(Home(tx.Id), "transaction.json");
        NoLinks(path);
        var temp = path + ".tmp";
        NoLinks(temp);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, tx, Json); stream.Flush(true); }
        File.Move(temp, path, true);
    }

    private static Transaction Read(string path)
    {
        NoLinks(path);
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<Transaction>(stream) ?? throw new InvalidDataException("İşlem kaydı okunamadı.");
    }

    private static void Replace(string source, string destination, string id, CancellationToken cancellationToken = default)
    {
        NoLinks(source);
        NoLinks(destination);
        var temp = destination + ".frameproof-" + id + ".tmp";
        NoLinks(temp);
        if (File.Exists(temp)) throw new IOException("Önce yarım kalan paket işlemini kurtarın.");
        try
        {
            CopyDurable(source, temp, cancellationToken);
            NoLinks(destination);
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void CopyDurable(string source, string destination, CancellationToken cancellationToken = default)
    {
        NoLinks(source);
        NoLinks(destination);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer, 0, count);
        }
        output.Flush(true);
    }

    private static void EnsureFreeSpace(string path, long bytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
        if (!drive.IsReady || drive.AvailableFreeSpace < checked(bytes + 1024 * 1024))
            throw new IOException("Paket dosyası veya geri alma yedeği için yeterli boş alan yok.");
    }

    private static string HashFile(string path, CancellationToken cancellationToken = default) { NoLinks(path); using var file = File.OpenRead(path); return Hash(file, cancellationToken); }
    private static string Hash(Stream stream, CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static string Full(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static string Resolve(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, SafeRelative(relative)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Paket yolu oyun klasörünün dışında.");
        return full;
    }

    private static string SafeRelative(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(x => string.IsNullOrWhiteSpace(x) || x is "." or ".." || x.EndsWith('.') || x.EndsWith(' ') ||
            x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || x.Contains(':') ||
            Regex.IsMatch(x.Split('.')[0], "\\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\\z", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("Paket geçersiz veya güvensiz bir dosya yolu içeriyor.");
        return Path.Combine(parts);
    }

    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Bağlantı/junction içeren yollar desteklenmiyor.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public sealed class Transaction
    {
        public string Id { get; set; } = "";
        public string Target { get; set; } = "";
        public string PackageId { get; set; } = "";
        public string Version { get; set; } = "";
        public string State { get; set; } = "Installing";
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public List<FileChange> Files { get; set; } = new();
        public List<string> CreatedDirectories { get; set; } = new();
    }

    public sealed class FileChange
    {
        public int Index { get; set; }
        public string Relative { get; set; } = "";
        public string NewHash { get; set; } = "";
        public string? OldHash { get; set; }
        public bool Existed { get; set; }
        public bool Pending { get; set; }
    }
}
