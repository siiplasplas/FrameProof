using System.Text.Json;
using FrameProof.Core.Games;
using FrameProof.Core.Util;

namespace FrameProof.Core.Library;

/// <summary>Versioned local library. All read/modify/write operations share a cross-process file lock.</summary>
public sealed class GameLibraryStore
{
    private sealed class Document
    {
        public int SchemaVersion { get; set; } = 1;
        public List<GameEntry> Games { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string FilePath { get; }

    public GameLibraryStore(string? filePath = null) =>
        FilePath = LibraryPaths.Normalize(filePath ?? Path.Combine(AppPaths.Data, "games.json"));

    public IReadOnlyList<GameEntry> Load()
    {
        using var gate = Lock();
        return Read().Games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public GameEntry AddManual(string executablePath, string? name = null, string? installDirectory = null)
    {
        var launch = GameLauncher.Validate(new GameLaunchProfile { ExecutablePath = executablePath });
        var folder = LibraryPaths.Normalize(installDirectory ?? Path.GetDirectoryName(launch.ExecutablePath)!);
        if (!Directory.Exists(folder) || !LibraryPaths.Contains(folder, launch.ExecutablePath))
            throw new ArgumentException("Başlatma dosyası seçilen oyun klasörünün içinde olmalı.", nameof(installDirectory));
        using var gate = Lock();
        var document = Read();
        var existing = document.Games.FirstOrDefault(g => LibraryPaths.Equal(g.InstallDirectory, folder)
            || (g.Launch is not null && LibraryPaths.Equal(g.Launch.ExecutablePath, launch.ExecutablePath)))
            ?? document.Games.Where(g => LibraryPaths.Contains(g.InstallDirectory, launch.ExecutablePath))
                .OrderByDescending(g => g.InstallDirectory.Length).FirstOrDefault();
        var entry = existing is null
            ? new GameEntry { Name = CleanName(name ?? Path.GetFileNameWithoutExtension(launch.ExecutablePath)), InstallDirectory = folder, Launch = launch, LastSeenAt = DateTimeOffset.UtcNow }
            : existing with { Name = name is null ? existing.Name : CleanName(name), Launch = launch, LastSeenAt = DateTimeOffset.UtcNow };
        Replace(document, entry);
        Save(document);
        return entry;
    }

    /// <summary>Discovery never removes missing installations or overwrites user names/launch settings.</summary>
    public IReadOnlyList<GameEntry> Discover() => MergeDiscovered(GameLibrary.List());

    public IReadOnlyList<GameEntry> MergeDiscovered(IEnumerable<InstalledGame> installations)
    {
        ArgumentNullException.ThrowIfNull(installations);
        var found = installations.ToArray();
        using var gate = Lock();
        var document = Read();
        foreach (var game in found)
        {
            var folder = LibraryPaths.Normalize(game.Folder);
            if (!Directory.Exists(folder)) continue;
            var existing = document.Games.FirstOrDefault(g => LibraryPaths.Equal(g.InstallDirectory, folder))
                ?? document.Games.FirstOrDefault(g => g.Source == "Manual" && g.Launch is not null
                    && LibraryPaths.Contains(folder, g.Launch.ExecutablePath));
            var entry = existing is null
                ? new GameEntry { Name = CleanName(game.Name), InstallDirectory = folder, Source = game.Store, LastSeenAt = DateTimeOffset.UtcNow }
                : existing with { InstallDirectory = folder, Source = game.Store, LastSeenAt = DateTimeOffset.UtcNow };
            Replace(document, entry);
        }
        Save(document);
        return document.Games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public GameEntry Rename(string id, string name) => Update(id, g => g with { Name = CleanName(name) });

    /// <summary>Reconnect a moved installation without changing its identity or claiming its package versions were verified.</summary>
    public GameEntry Relocate(string id, string executablePath, string? installDirectory = null)
    {
        var launch = GameLauncher.Validate(new GameLaunchProfile { ExecutablePath = executablePath });
        var folder = LibraryPaths.Normalize(installDirectory ?? Path.GetDirectoryName(launch.ExecutablePath)!);
        if (!Directory.Exists(folder) || !LibraryPaths.Contains(folder, launch.ExecutablePath))
            throw new ArgumentException("Başlatma dosyası seçilen oyun klasörünün içinde olmalı.", nameof(installDirectory));
        using var gate = Lock();
        var document = Read();
        var current = document.Games.SingleOrDefault(g => g.Id == id)
            ?? throw new KeyNotFoundException("Oyun kaydı bulunamadı.");
        if (document.Games.Any(g => g.Id != id && (LibraryPaths.Equal(g.InstallDirectory, folder)
            || LibraryPaths.Contains(g.InstallDirectory, launch.ExecutablePath)
            || (g.Launch is not null && LibraryPaths.Equal(g.Launch.ExecutablePath, launch.ExecutablePath)))))
            throw new InvalidOperationException("Seçilen oyun klasörü veya başlatma dosyası başka bir oyun kaydına ait.");
        launch = GameLauncher.Validate(launch with { Arguments = current.Launch?.Arguments ?? Array.Empty<string>() });
        var updated = current with
        {
            InstallDirectory = folder,
            Launch = launch,
            InstalledGameVersion = null,
            InstalledFixVersion = null,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        Replace(document, updated);
        Save(document);
        return updated;
    }

    public GameEntry SetLaunchProfile(string id, GameLaunchProfile profile)
    {
        var valid = GameLauncher.Validate(profile);
        return Update(id, g => LibraryPaths.Contains(g.InstallDirectory, valid.ExecutablePath)
            ? g with { Launch = valid }
            : throw new ArgumentException("Başlatma dosyası oyun klasörünün içinde olmalı.", nameof(profile)));
    }

    public GameEntry SetPackageVersions(string id, string? catalogId, string? gameVersion, string? fixVersion) =>
        Update(id, g => g with { CatalogId = catalogId, InstalledGameVersion = gameVersion, InstalledFixVersion = fixVersion });

    public GameEntry SetInstallationPending(string id, bool pending) => Update(id, g => g with { InstallationPending = pending });

    /// <summary>Commit readiness only after the coordinator has verified the complete game/fix installation.</summary>
    public GameEntry RegisterInstalled(string catalogId, string name, string installDirectory,
        GameLaunchProfile launch, string gameVersion, string? fixVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);
        name = CleanName(name);
        var valid = GameLauncher.Validate(launch);
        var folder = LibraryPaths.Normalize(installDirectory);
        if (!Directory.Exists(folder) || !LibraryPaths.Contains(folder, valid.ExecutablePath))
            throw new ArgumentException("Başlatma dosyası seçilen oyun klasörünün içinde olmalı.", nameof(installDirectory));
        using var gate = Lock();
        var document = Read();
        var existing = document.Games.SingleOrDefault(g => LibraryPaths.Equal(g.InstallDirectory, folder));
        if (existing?.CatalogId is string previousCatalog && previousCatalog != catalogId)
            throw new InvalidOperationException("Bu klasör farklı bir katalog oyununa ait.");
        if (document.Games.Any(g => g.Id != existing?.Id && g.Launch is not null
            && LibraryPaths.Equal(g.Launch.ExecutablePath, valid.ExecutablePath)))
            throw new InvalidOperationException("Başlatma dosyası başka bir oyun kaydına ait.");
        var entry = (existing ?? new GameEntry { Name = name, Source = "Catalog" }) with
        {
            CatalogId = catalogId,
            InstallDirectory = folder,
            Launch = valid,
            InstalledGameVersion = gameVersion,
            InstalledFixVersion = fixVersion,
            InstallationPending = false,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        Replace(document, entry);
        Save(document);
        return entry;
    }

    /// <summary>Forget only the library entry; never delete game files.</summary>
    public bool Remove(string id)
    {
        using var gate = Lock();
        var document = Read();
        if (document.Games.RemoveAll(g => g.Id == id) == 0) return false;
        Save(document);
        return true;
    }

    private GameEntry Update(string id, Func<GameEntry, GameEntry> change)
    {
        using var gate = Lock();
        var document = Read();
        var current = document.Games.SingleOrDefault(g => g.Id == id) ?? throw new KeyNotFoundException("Oyun kaydı bulunamadı.");
        var updated = change(current);
        Replace(document, updated);
        Save(document);
        return updated;
    }

    private FileStream Lock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            try { return new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(25); }
        }
    }

    private Document Read()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(FilePath), Json)
                ?? throw new InvalidDataException("Oyun kütüphanesi boş veya geçersiz.");
            if (document.SchemaVersion != 1) throw new InvalidDataException("Oyun kütüphanesinin sürümü desteklenmiyor.");
            if (document.Games is null || document.Games.Any(g => g is null || string.IsNullOrWhiteSpace(g.Id)
                || string.IsNullOrWhiteSpace(g.Name) || string.IsNullOrWhiteSpace(g.InstallDirectory)))
                throw new InvalidDataException("Oyun kütüphanesi geçersiz kayıt içeriyor.");
            document.Games = document.Games.Select(g => g with { InstallDirectory = LibraryPaths.Normalize(g.InstallDirectory) }).ToList();
            if (document.Games.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count() != document.Games.Count
                || document.Games.Select(g => g.InstallDirectory).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Games.Count)
                throw new InvalidDataException("Oyun kütüphanesinde yinelenen kayıt var.");
            return document;
        }
        catch (JsonException ex) { throw new InvalidDataException("Oyun kütüphanesi okunamadı; mevcut dosya korunuyor.", ex); }
    }

    private void Save(Document document)
    {
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Replace(Document document, GameEntry entry)
    {
        var index = document.Games.FindIndex(g => g.Id == entry.Id);
        if (index < 0) document.Games.Add(entry); else document.Games[index] = entry;
    }

    private static string CleanName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim();
    }
}
