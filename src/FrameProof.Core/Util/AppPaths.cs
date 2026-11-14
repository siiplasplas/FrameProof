namespace FrameProof.Core.Util;

public static class AppPaths
{
    /// <summary>Test ve taşınabilir kullanım için FRAMEPROOF_DATA ile değiştirilebilir.</summary>
    public static string Data => Ensure(Environment.GetEnvironmentVariable("FRAMEPROOF_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameProof"));

    public static string JournalFile => Path.Combine(Data, "journal.json");
    public static string RunsFile => Path.Combine(Data, "runs.json");
    public static string Captures => Ensure(Path.Combine(Data, "captures"));
    public static string Tools => Ensure(Path.Combine(Data, "tools"));

    private static string Ensure(string p)
    {
        Directory.CreateDirectory(p);
        return p;
    }
}
