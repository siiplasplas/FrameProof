using System.Globalization;
using System.Text.Json;
using FrameProof.Core.Util;

namespace FrameProof.Core.Measure;

public sealed class RunSummary
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Label { get; set; } = "";
    public string Process { get; set; } = "";
    public DateTime At { get; set; } = DateTime.Now;
    public double Seconds { get; set; }
    public int Frames { get; set; }
    public double AvgFps { get; set; }
    public double Low1Fps { get; set; }
    public double Low01Fps { get; set; }
    public double MedianMs { get; set; }
    public double StutterPct { get; set; }
    public double? GpuBusyRatio { get; set; }

    // Ölçüm sırasında donanımdan okunanlar (eski kayıtlarda boş)
    public int HwSamples { get; set; }
    public string? ExePath { get; set; }
    public string? GpuName { get; set; }
    public bool? GpuIntegrated { get; set; }
    public double? CpuPerfAvg { get; set; }
    public double? CpuPerfMin { get; set; }
    public double? GpuTempMax { get; set; }
    public double? GpuClockPct { get; set; }
    public double? ThermalPct { get; set; }
    public double? PowerCapPct { get; set; }
    public bool? OnBattery { get; set; }
    public long? RamTotalMb { get; set; }
    /// <summary>Ölçüm boyunca en az kaç MB RAM boş kaldı.</summary>
    public long? RamAvailMinMb { get; set; }
    /// <summary>Sanal belleğin (RAM + pagefile) en çok yüzde kaçı doldu.</summary>
    public long? CommitPeakPct { get; set; }
    /// <summary>Saniyede diskten okunan bellek sayfası (ortalama). Yüksekse RAM yetmiyor, oyun diske taşıyor.</summary>
    public double? PagesInAvg { get; set; }

    /// <summary>RAM yetti mi: tahmini bir yargı. null = bilgi yok.</summary>
    public bool? RamShort => RamAvailMinMb is null ? null
        : (RamAvailMinMb < 700 && PagesInAvg > 100) || CommitPeakPct >= 92;

    public string Bottleneck => GpuBusyRatio switch
    {
        null => "bilinmiyor",
        > 0.9 => "Ekran kartı sınırı (GPU-bound)",
        < 0.75 => "İşlemci/oyun sınırı (CPU-bound)",
        _ => "Karışık",
    };
}

/// <summary>PresentMon CSV'sini okur. Sütun adlarına göre çalışır (1.x ve 2.x adlarının ikisi de).</summary>
public static class FrameCsv
{
    private static readonly string[] FrameCols = { "MsBetweenPresents", "FrameTime", "msBetweenPresents" };
    private static readonly string[] GpuCols = { "MsGPUBusy", "GPUBusy", "msGPUActive", "MsGPUActive" };
    private static readonly string[] AppCols = { "Application", "ProcessName" };
    private static readonly string[] SwapCols = { "SwapChainAddress", "SwapChain" };

    public sealed record Parsed(string Process, List<double> FrameMs, List<double> GpuMs);

    public static Parsed Parse(string path)
    {
        using var r = new StreamReader(path);
        var header = r.ReadLine() ?? throw new InvalidDataException("CSV boş.");
        var cols = SplitCsv(header).Select(c => c.Trim()).ToList();
        int Find(string[] names) => names.Select(n => cols.FindIndex(c => c.Equals(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(i => i >= 0, -1);

        int fi = Find(FrameCols);
        if (fi < 0) throw new InvalidDataException("CSV'de kare süresi sütunu yok (MsBetweenPresents / FrameTime).");
        int gi = Find(GpuCols), ai = Find(AppCols), si = Find(SwapCols);

        // Bir süreçte birden fazla swapchain olabilir (başlatıcı, overlay). En çok kare çizeni al.
        var bySwap = new Dictionary<string, (string app, List<double> ft, List<double> gpu)>();
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var v = SplitCsv(line);
            if (v.Count <= fi) continue;
            if (!TryNum(v[fi], out var ft) || ft <= 0 || ft > 5000) continue;
            var key = si >= 0 && si < v.Count ? v[si] : "";
            if (!bySwap.TryGetValue(key, out var bucket))
            {
                bucket = (ai >= 0 && ai < v.Count ? v[ai] : "", new List<double>(), new List<double>());
                bySwap[key] = bucket;
            }
            bucket.ft.Add(ft);
            if (gi >= 0 && gi < v.Count && TryNum(v[gi], out var g) && g >= 0) bucket.gpu.Add(g);
        }
        if (bySwap.Count == 0) throw new InvalidDataException("CSV'de hiç kare yok. Oyun ölçüm sırasında ekranda mıydı?");
        var best = bySwap.Values.OrderByDescending(b => b.ft.Count).First();
        return new Parsed(best.app, best.ft, best.gpu.Count == best.ft.Count ? best.gpu : new List<double>());
    }

    private static bool TryNum(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v);

    private static List<string> SplitCsv(string line)
    {
        var res = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool q = false;
        foreach (var ch in line)
        {
            if (ch == '"') q = !q;
            else if (ch == ',' && !q) { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        res.Add(sb.ToString());
        return res;
    }
}

public static class Stats
{
    public static RunSummary Summarize(FrameCsv.Parsed p, string label)
    {
        var ft = p.FrameMs;
        if (ft.Count < 30) throw new InvalidDataException($"Çok az kare yakalandı ({ft.Count}). Ölçüm süresini uzat.");
        double sum = ft.Sum();
        var sorted = ft.OrderBy(x => x).ToArray();
        double median = Percentile(sorted, 50);
        // "1% low" = en yavaş %1 karenin ortalama süresinden FPS (tek bir yüzdelik noktasından daha kararlı).
        double WorstAvg(double frac)
        {
            int k = Math.Max(1, (int)Math.Round(sorted.Length * frac));
            return sorted.Skip(sorted.Length - k).Average();
        }
        double p99 = WorstAvg(0.01);
        double p999 = WorstAvg(0.001);
        int stutters = ft.Count(x => x > 2 * median);
        double? gpu = p.GpuMs.Count == ft.Count && p.GpuMs.Count > 0 ? p.GpuMs.Sum() / sum : null;

        return new RunSummary
        {
            Label = label,
            Process = p.Process,
            Seconds = sum / 1000,
            Frames = ft.Count,
            AvgFps = 1000.0 * ft.Count / sum,
            Low1Fps = 1000.0 / p99,
            Low01Fps = 1000.0 / p999,
            MedianMs = median,
            StutterPct = 100.0 * stutters / ft.Count,
            GpuBusyRatio = gpu,
        };
    }

    /// <summary>Doğrusal aradeğerlemeli yüzdelik (sıralı dizi).</summary>
    public static double Percentile(double[] sorted, double pct)
    {
        if (sorted.Length == 0) return double.NaN;
        double rank = pct / 100 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}

public enum Verdict { Better, Worse, NoDifference, NotEnoughData }

public sealed record Comparison(string Metric, double MeanA, double MeanB, double DiffPct, double CiLowPct, double CiHighPct, Verdict Verdict, int Na, int Nb)
{
    public string VerdictText => Verdict switch
    {
        Verdict.Better => "Gerçek iyileşme",
        Verdict.Worse => "Gerçek kötüleşme",
        Verdict.NoDifference => "Fark yok (gürültü içinde)",
        _ => "Yetersiz veri: her taraf için en az 3 ölçüm al",
    };

    public string Text => Verdict == Verdict.NotEnoughData
        ? $"{Metric}: {Fmt.Num(MeanA)} → {Fmt.Num(MeanB)} · {VerdictText}"
        : $"{Metric}: {Fmt.Num(MeanA)} → {Fmt.Num(MeanB)} ({Fmt.Pct(DiffPct)}, %95 aralık {Fmt.Pct(CiLowPct)} … {Fmt.Pct(CiHighPct)}) · {VerdictText}";
}

/// <summary>
/// Önce/sonra karşılaştırması. Her ölçümün özeti bir örnek; Welch t-testi ile %95 güven aralığı.
/// Aralık sıfırı içeriyorsa ya da fark %1'den küçükse "fark yok" denir. Pazarlama değil, istatistik.
/// </summary>
public static class Compare
{
    public static Comparison Welch(string metric, IReadOnlyList<double> a, IReadOnlyList<double> b, bool higherIsBetter = true)
    {
        double ma = a.Count > 0 ? a.Average() : double.NaN, mb = b.Count > 0 ? b.Average() : double.NaN;
        if (a.Count < 3 || b.Count < 3)
        {
            double d0 = a.Count > 0 && b.Count > 0 ? (mb - ma) / ma * 100 : 0;
            return new Comparison(metric, ma, mb, d0, double.NaN, double.NaN, Verdict.NotEnoughData, a.Count, b.Count);
        }
        double va = Var(a, ma), vb = Var(b, mb);
        double sa = va / a.Count, sb = vb / b.Count;
        double se = Math.Sqrt(sa + sb);
        double diff = mb - ma;
        double df = se == 0 ? 120 : (sa + sb) * (sa + sb) / (sa * sa / (a.Count - 1) + sb * sb / (b.Count - 1));
        double t = TCrit(df);
        double lo = diff - t * se, hi = diff + t * se;
        double baseV = Math.Abs(ma) < 1e-9 ? 1 : ma; // takılma %0 ise yüzde yerine mutlak fark
        double pct = diff / baseV * 100, loP = lo / baseV * 100, hiP = hi / baseV * 100;

        Verdict v;
        bool sig = lo > 0 || hi < 0;
        if (!sig || Math.Abs(pct) < 1) v = Verdict.NoDifference;
        else v = (diff > 0) == higherIsBetter ? Verdict.Better : Verdict.Worse;
        return new Comparison(metric, ma, mb, pct, loP, hiP, v, a.Count, b.Count);
    }

    private static double Var(IReadOnlyList<double> x, double m) => x.Sum(v => (v - m) * (v - m)) / (x.Count - 1);

    // t dağılımı, iki yönlü %95 (0.975 yüzdelik)
    private static readonly double[] T975 =
    {
        12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
        2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
        2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042,
    };

    public static double TCrit(double df)
    {
        if (double.IsNaN(df) || df < 1) df = 1;
        int d = (int)Math.Floor(df); // aşağı yuvarla: daha temkinli
        if (d <= 30) return T975[d - 1];
        if (d < 40) return 2.042;
        if (d < 60) return 2.021;
        if (d < 120) return 2.000;
        return 1.980;
    }
}

public static class RunStore
{
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static List<RunSummary> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.RunsFile)) return new();
            return JsonSerializer.Deserialize<List<RunSummary>>(File.ReadAllText(AppPaths.RunsFile)) ?? new();
        }
        catch { return new(); }
    }

    public static void Save(List<RunSummary> runs)
    {
        var tmp = AppPaths.RunsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(runs, Opt));
        File.Move(tmp, AppPaths.RunsFile, true);
    }

    public static void Add(RunSummary r)
    {
        var all = Load();
        all.Add(r);
        Save(all);
    }

    public static void Delete(string id)
    {
        var all = Load();
        all.RemoveAll(r => r.Id == id);
        Save(all);
    }

    public static IEnumerable<string> Labels() => Load().Select(r => r.Label).Distinct();

    public static List<Comparison> CompareLabels(string a, string b)
    {
        var all = Load();
        var ra = all.Where(r => r.Label == a).ToList();
        var rb = all.Where(r => r.Label == b).ToList();
        return new List<Comparison>
        {
            Compare.Welch("Ortalama FPS", ra.Select(r => r.AvgFps).ToList(), rb.Select(r => r.AvgFps).ToList()),
            Compare.Welch("1% low FPS", ra.Select(r => r.Low1Fps).ToList(), rb.Select(r => r.Low1Fps).ToList()),
            Compare.Welch("Takılma (%)", ra.Select(r => r.StutterPct).ToList(), rb.Select(r => r.StutterPct).ToList(), higherIsBetter: false),
        };
    }
}
