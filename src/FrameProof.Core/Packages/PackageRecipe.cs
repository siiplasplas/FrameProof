namespace FrameProof.Core.Packages;

/// <summary>A trusted catalog recipe. A SHA256 checks bytes, not publisher authenticity.</summary>
public sealed record PackageRecipe(string PackageId, string Version, string GameVersion, string Sha256)
{
    public long MaximumExpandedBytes { get; init; } = 20L * 1024 * 1024 * 1024;
    public int MaximumFiles { get; init; } = 100_000;
}

public sealed record PackageProgress(string Stage, int CompletedFiles, int TotalFiles);
public sealed record PackageInstallResult(string TransactionId, string PackageId, string Version, int FileCount);

public sealed class PackageConflictException : IOException
{
    public PackageConflictException(string message) : base(message) { }
}
