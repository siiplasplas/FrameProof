using FrameProof.Core.Downloads;

namespace FrameProof.Core.Installation;

/// <summary>A trusted catalog's explicitly paired game and optional fix package.</summary>
public sealed record GameInstallRecipe
{
    public string CatalogId { get; init; } = "";
    public string Name { get; init; } = "";
    public string GameVersion { get; init; } = "";
    public string? FixVersion { get; init; }
    public DownloadRequest GamePackage { get; init; } = null!;
    public DownloadRequest? FixPackage { get; init; }
    public string LaunchRelativePath { get; init; } = "";
    public string[] Arguments { get; init; } = Array.Empty<string>();
    public long MaximumGameExpandedBytes { get; init; }
    public long MaximumFixExpandedBytes { get; init; }
}

public sealed record GameInstallProgress(string Stage, long ReceivedBytes = 0, long TotalBytes = 0,
    int CompletedFiles = 0, int TotalFiles = 0);
public sealed record GameInstallJobInfo(string JobId, string CatalogId, string Name, string TargetDirectory, string Phase);
