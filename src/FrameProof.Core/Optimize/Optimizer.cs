using FrameProof.Core.Cleanup;
using FrameProof.Core.Tweaks;
using FrameProof.Core.Util;

namespace FrameProof.Core.Optimize;

public sealed record PlannedClean(CleanupTarget Target, long Bytes);

/// <summary>"Optimize et" düğmesinin ne yapacağı. Önce plan çıkarılır ve kullanıcıya gösterilir, sonra uygulanır.</summary>
public sealed class OptimizePlan
{
    public List<PlannedClean> Clean { get; } = new();
    public List<Tweak> Tweaks { get; } = new();
    /// <summary>Yönetici izni olmadığı için atlananlar.</summary>
    public List<string> SkippedNoAdmin { get; } = new();
    public long Bytes => Clean.Sum(c => Math.Max(0, c.Bytes));
    public bool IsEmpty => Clean.Count == 0 && Tweaks.Count == 0;
}

public sealed class OptimizeResult
{
    public long FreedBytes { get; set; }
    public int Files { get; set; }
    public List<string> Applied { get; } = new();
    public List<string> Errors { get; } = new();
    public long FreeBefore { get; set; }
    public long FreeAfter { get; set; }
}

/// <summary>
/// Tek tıkla güvenle yapılabilecekleri seçer: varsayılan açık temizlik maddeleri ve riski/yeniden başlatması olmayan ayarlar.
/// Bellek Bütünlüğü, HAGS, telemetri servisi gibi tartışmalı ya da yeniden başlatma isteyenler ASLA otomatik yapılmaz;
/// programları kapatmak da bu düğmenin işi değildir.
/// </summary>
public static class Optimizer
{
    public static OptimizePlan Build()
    {
        var plan = new OptimizePlan();
        bool admin = Admin.IsElevated;

        foreach (var t in CleanupCatalog.Build().Where(t => t.DefaultOn && !t.Slow))
        {
            if (t.NeedsAdmin && !admin) { plan.SkippedNoAdmin.Add(t.Name); continue; }
            CleanupAnalysis a;
            try { a = t.Analyze(); } catch { continue; }
            if (a.Bytes > 1024 * 1024) plan.Clean.Add(new PlannedClean(t, a.Bytes));
        }

        foreach (var t in TweakCatalog.All)
        {
            bool eligible = t.OneClick && !t.NeedsReboot
                || (t.Id == "power_high" && PowerPlanTweak.Active() == PowerPlanTweak.Saver);
            if (!eligible) continue;
            TweakState s;
            try { s = t.GetState(); } catch { continue; }
            if (s != TweakState.NotApplied) continue;
            if (t.NeedsAdmin && !admin) { plan.SkippedNoAdmin.Add(t.Title); continue; }
            plan.Tweaks.Add(t);
        }
        return plan;
    }

    public static async Task<OptimizeResult> ExecuteAsync(OptimizePlan plan, IProgress<string>? progress, CancellationToken ct)
    {
        var r = new OptimizeResult { FreeBefore = FreeSpace() };
        foreach (var c in plan.Clean)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(c.Target.Name + " temizleniyor…");
            try
            {
                var res = await c.Target.CleanAsync(ct);
                r.FreedBytes += res.FreedBytes;
                r.Files += res.Files;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { r.Errors.Add($"{c.Target.Name}: {ex.Message}"); }
        }
        foreach (var t in plan.Tweaks)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(t.Title + "…");
            try
            {
                if (t.GetState() != TweakState.NotApplied) continue; // plan çıkarıldıktan sonra başka yerden yapılmış
                await Task.Run(t.Apply, ct);
                r.Applied.Add(t.Title);
            }
            catch (Exception ex) { r.Errors.Add($"{t.Title}: {ex.Message}"); }
        }
        r.FreeAfter = FreeSpace();
        return r;
    }

    public static long FreeSpace()
    {
        try { return new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!).AvailableFreeSpace; }
        catch { return 0; }
    }
}
