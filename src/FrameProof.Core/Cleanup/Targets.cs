using System.Diagnostics;
using System.Runtime.InteropServices;
using FrameProof.Core.Util;
using Microsoft.Win32;

namespace FrameProof.Core.Cleanup;

public sealed record CleanupAnalysis(long Bytes, int Files, string? Note)
{
    public bool Unknown => Bytes < 0;
}

public sealed record CleanupResult(long FreedBytes, int Files, int Skipped, string? Note);

public abstract class CleanupTarget
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string? Warning { get; init; }
    public bool DefaultOn { get; init; }
    public bool NeedsAdmin { get; init; }
    public bool Slow { get; init; }

    public abstract CleanupAnalysis Analyze();
    public abstract Task<CleanupResult> CleanAsync(CancellationToken ct);

    protected static string? AdminNote(bool needsAdmin, bool denied) =>
        denied && !Admin.IsElevated ? "Tamamını görmek için yönetici izni gerekiyor" : null;
}

public sealed record FolderSpec(string Path, string Pattern = "*", bool Recursive = true, int MinAgeDays = 0);

/// <summary>Uygulama açıkken atlanacak klasör grubu (örn. Chrome açıksa Chrome önbelleği).</summary>
public sealed record SpecGroup(string Label, string[] Processes, Func<IEnumerable<FolderSpec>> Specs);

public sealed class FolderTarget : CleanupTarget
{
    public required SpecGroup[] Groups { get; init; }
    public Action? Before { get; init; }
    public Action? After { get; init; }

    private static bool Running(string[] names) =>
        names.Any(n => { try { return Process.GetProcessesByName(n).Length > 0; } catch { return false; } });

    public override CleanupAnalysis Analyze()
    {
        var s = new CleanStats();
        var notes = new List<string>();
        foreach (var g in Groups)
        {
            if (Running(g.Processes)) { notes.Add($"{g.Label} açık, kapatınca temizlenir"); continue; }
            foreach (var spec in Safe(g.Specs))
                SafeDelete.Measure(spec.Path, spec.Pattern, spec.Recursive, Cutoff(spec), s);
        }
        var an = AdminNote(NeedsAdmin, s.AccessDenied);
        if (an != null) notes.Add(an);
        return new CleanupAnalysis(s.Bytes, s.Files, notes.Count > 0 ? string.Join(" · ", notes) : null);
    }

    public override Task<CleanupResult> CleanAsync(CancellationToken ct) => Task.Run(() =>
    {
        var s = new CleanStats();
        var notes = new List<string>();
        try
        {
            Before?.Invoke();
            foreach (var g in Groups)
            {
                if (Running(g.Processes)) { notes.Add($"{g.Label} açık olduğu için atlandı"); continue; }
                foreach (var spec in Safe(g.Specs))
                    SafeDelete.Delete(spec.Path, spec.Pattern, spec.Recursive, Cutoff(spec), s, ct);
            }
        }
        finally
        {
            try { After?.Invoke(); } catch { }
        }
        if (s.Skipped > 0) notes.Add($"{s.Skipped} dosya kullanımda olduğu için atlandı");
        return new CleanupResult(s.Bytes, s.Files, s.Skipped, notes.Count > 0 ? string.Join(" · ", notes) : null);
    }, ct);

    private static DateTime Cutoff(FolderSpec s) => DateTime.UtcNow.AddDays(-s.MinAgeDays);

    private static IEnumerable<FolderSpec> Safe(Func<IEnumerable<FolderSpec>> f)
    {
        try { return f().ToList(); } catch { return Array.Empty<FolderSpec>(); }
    }
}

public sealed class RecycleBinTarget : CleanupTarget
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    public override CleanupAnalysis Analyze()
    {
        var i = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref i) == 0
            ? new CleanupAnalysis(i.i64Size, (int)i.i64NumItems, null)
            : new CleanupAnalysis(-1, 0, "Okunamadı");
    }

    public override Task<CleanupResult> CleanAsync(CancellationToken ct) => Task.Run(() =>
    {
        var a = Analyze();
        int hr = SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4);
        // Kutu zaten boşsa da hata kodu dönebilir.
        return hr == 0 || a.Files == 0
            ? new CleanupResult(Math.Max(0, a.Bytes), a.Files, 0, null)
            : new CleanupResult(0, 0, 0, $"Geri dönüşüm kutusu boşaltılamadı (0x{hr:X8})");
    }, ct);
}

/// <summary>Windows.old ve yükseltme artıkları: Windows'un kendi Disk Temizleme aracı (cleanmgr) ile silinir.</summary>
public sealed class PreviousWindowsTarget : CleanupTarget
{
    private const string Vc = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\";
    private static readonly string[] Handlers = { "Previous Installations", "Temporary Setup Files", "Windows Upgrade Log Files" };
    private const string Flag = "StateFlags0042";

    private static IEnumerable<string> Dirs()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory)!;
        yield return Path.Combine(root, "Windows.old");
        yield return Path.Combine(root, "$Windows.~BT");
        yield return Path.Combine(root, "$Windows.~WS");
    }

    public override CleanupAnalysis Analyze()
    {
        var s = new CleanStats();
        var existing = Dirs().Where(Directory.Exists).ToList();
        if (existing.Count == 0) return new CleanupAnalysis(0, 0, "Eski Windows kurulumu yok");
        foreach (var d in existing)
        {
            try { SafeDelete.Measure(d, "*", true, DateTime.UtcNow, s); } catch { }
        }
        return new CleanupAnalysis(s.Bytes, s.Files, s.AccessDenied ? "Boyutun tamamı için yönetici izni gerekiyor" : null);
    }

    public override async Task<CleanupResult> CleanAsync(CancellationToken ct)
    {
        var before = Analyze().Bytes;
        try
        {
            foreach (var h in Handlers)
            {
                using var k = Registry.LocalMachine.OpenSubKey(Vc + h, true);
                k?.SetValue(Flag, 2, RegistryValueKind.DWord);
            }
            await Shell.RunAsync(Shell.Sys("cleanmgr.exe"), "/sagerun:42", ct);
        }
        finally
        {
            foreach (var h in Handlers)
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(Vc + h, true);
                    k?.DeleteValue(Flag, false);
                }
                catch { }
            }
        }
        var after = Analyze().Bytes;
        return new CleanupResult(Math.Max(0, before - after), 0, 0, after > 0 ? "Bir kısmı silinemedi" : null);
    }
}

public sealed class ComponentStoreTarget : CleanupTarget
{
    public override CleanupAnalysis Analyze() => new(-1, 0, "Boyut önceden bilinmiyor; genelde 0,5–5 GB");

    public override async Task<CleanupResult> CleanAsync(CancellationToken ct)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        long before = drive.AvailableFreeSpace;
        var r = await Shell.RunAsync(Shell.Sys("Dism.exe"), "/Online /Cleanup-Image /StartComponentCleanup", ct);
        drive = new DriveInfo(drive.Name);
        long freed = Math.Max(0, drive.AvailableFreeSpace - before);
        return r.Ok
            ? new CleanupResult(freed, 0, 0, null)
            : new CleanupResult(freed, 0, 0, "DISM hata verdi: " + r.StdOut.Trim().Split('\n').LastOrDefault());
    }
}
