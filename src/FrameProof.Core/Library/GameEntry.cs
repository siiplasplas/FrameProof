using System.Text.Json.Serialization;

namespace FrameProof.Core.Library;

public sealed record GameLaunchProfile
{
    public string ExecutablePath { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string[] Arguments { get; init; } = Array.Empty<string>();
}

/// <summary>A local installation. CatalogId identifies the same game across friends; Id stays local.</summary>
public sealed record GameEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string? CatalogId { get; init; }
    public string Name { get; init; } = "";
    public string InstallDirectory { get; init; } = "";
    public string Source { get; init; } = "Manual";
    public string? CoverPath { get; init; }
    public GameLaunchProfile? Launch { get; init; }
    public string? InstalledGameVersion { get; init; }
    public string? InstalledFixVersion { get; init; }
    public bool InstallationPending { get; init; }
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; init; }
    [JsonIgnore] public bool IsInstalled => Directory.Exists(InstallDirectory);
    [JsonIgnore] public bool CanLaunch => !InstallationPending && IsInstalled && Launch is not null && File.Exists(Launch.ExecutablePath);
}

internal static class LibraryPaths
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Tam dosya veya klasör yolu gerekli.", nameof(path));
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("Aygıt yolu yerine normal dosya yolunu seçin.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool Equal(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    public static bool Contains(string directory, string path) => Equal(directory, path) ||
        path.StartsWith(Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
