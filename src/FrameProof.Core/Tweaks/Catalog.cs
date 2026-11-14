using System.Text.RegularExpressions;
using FrameProof.Core.Util;
using Microsoft.Win32;

namespace FrameProof.Core.Tweaks;

public static class TweakCatalog
{
    public static IReadOnlyList<Tweak> All { get; } = new Tweak[]
    {
        new RefreshRateTweak(),
        new PowerPlanTweak(),
        new GameModeTweak(),
        new GameDvrTweak(),
        new MouseAccelTweak(),
        new VbsTweak(),
        new HagsTweak(),
        new WindowedOptTweak(),
        new FrameProof.Core.Games.GpuPinTweak(),
        new EdgeBackgroundTweak(),
        new WidgetsTweak(),
        new AdsTweak(),
        new DeliveryOptimizationTweak(),
        new BackgroundAppsTweak(),
        new TelemetryTweak(),

        new EdgeUpdateTweak(),
        new CopilotTweak(),
        new BingSearchTweak(),
        new HibernateTweak(),
        new VisualFxTweak(),
    };

    public static Tweak? Find(string id) => All.FirstOrDefault(t => t.Id == id);
}

/// <summary>Monitör en yüksek yenileme hızında değilse yükseltir.</summary>
public sealed class RefreshRateTweak : Tweak
{
    public override string Id => "refresh_max";
    public override string Title => "Monitörü en yüksek Hz'e al";
    public override bool OneClick => true;
    public override string WhatItDoes => "Her ekranı mevcut çözünürlükte desteklediği en yüksek yenileme hızına ayarlar.";
    public override string Effect => "FPS sayacını değiştirmez, ama 144 Hz monitör 60 Hz'de unutulduysa fark çok büyüktür: oyun gözle görülür şekilde akıcılaşır.";
    public override string? Risk => "Ekran bir saniyeliğine kararabilir.";

    public override TweakState GetState()
    {
        var d = Displays.List();
        if (d.Count == 0) return TweakState.NotApplicable;
        return d.All(x => x.CurrentHz >= x.MaxHz) ? TweakState.Applied : TweakState.NotApplied;
    }

    protected override void ApplyCore(List<ChangeRecord> changes)
    {
        foreach (var d in Displays.List().Where(x => x.CurrentHz < x.MaxHz))
        {
            changes.Add(new ChangeRecord { Type = "display", Name = d.DeviceName, Data = d.CurrentHz.ToString() });
            Displays.SetRefresh(d.DeviceName, d.MaxHz);
        }
    }
}

public sealed class PowerPlanTweak : Tweak
{
    public const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string Saver = "a1841308-3541-4fab-bc81-f71556f20b4a";
    private static readonly Regex GuidRx = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

    public override string Id => "power_high";
    public override string Title => "Güç planı: Yüksek performans";
    public override string WhatItDoes => "Windows güç planını 'Yüksek performans' yapar (yoksa onun bir kopyasını oluşturur).";
    public override string Effect => "Modern işlemcilerde genelde %0–3. Asıl fayda işlemci saat hızının daha az dalgalanması. 'Güç tasarrufu' planından geliyorsan fark büyük olabilir.";
    public override string? Risk => "Laptopta pil daha hızlı biter, fan biraz daha çalışabilir.";

    public static string? Active()
    {
        var r = Shell.Run(Shell.Sys("powercfg.exe"), "/getactivescheme");
        var m = GuidRx.Match(r.StdOut);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    public static string PlanName(string? guid) => guid switch
    {
        HighPerf => "Yüksek performans",
        Ultimate => "Nihai performans",
        Balanced => "Dengeli",
        Saver => "Güç tasarrufu",
        null => "Bilinmiyor",
        _ => "Özel plan",
    };

    private static IEnumerable<string> OurPlans() =>
        Journal.Load().Where(e => e.TweakId == "power_high").SelectMany(e => e.Changes).Select(c => c.Extra).OfType<string>();

    public override TweakState GetState()
    {
        var a = Active();
        if (a is HighPerf or Ultimate || (a is not null && OurPlans().Contains(a))) return TweakState.Applied;
        return TweakState.NotApplied;
    }

    protected override void ApplyCore(List<ChangeRecord> changes)
    {
        var prev = Active() ?? throw new InvalidOperationException("Etkin güç planı okunamadı.");
        var list = Shell.Run(Shell.Sys("powercfg.exe"), "/list").StdOut.ToLowerInvariant();
        string target = HighPerf;
        string? created = null;
        if (!list.Contains(HighPerf))
        {
            var dup = Shell.Run(Shell.Sys("powercfg.exe"), "-duplicatescheme " + HighPerf);
            var m = GuidRx.Match(dup.StdOut);
            if (!dup.Ok || !m.Success)
                throw new InvalidOperationException("Bu sistemde 'Yüksek performans' planı yok (Modern Standby). Ayarlar > Sistem > Güç > Güç modu: 'En iyi performans' seç.");
            target = created = m.Value.ToLowerInvariant();
        }
        changes.Add(new ChangeRecord { Type = "power", Data = prev, Extra = created });
        var r = Shell.Run(Shell.Sys("powercfg.exe"), "/setactive " + target);
        if (!r.Ok) throw new InvalidOperationException("Güç planı değiştirilemedi: " + r.StdErr + r.StdOut);
    }
}

public sealed class GameModeTweak : Tweak
{
    private const string K = @"Software\Microsoft\GameBar";
    public override string Id => "game_mode_on";
    public override string Title => "Oyun Modu'nu aç";
    public override bool OneClick => true;
    public override string WhatItDoes => "Windows Oyun Modu'nu açar: oyun açıkken arka plan güncellemeleri ve bildirimler geri planda tutulur.";
    public override string Effect => "Ortalama FPS'e etkisi küçük; arka planda iş varken ani takılmaları azaltabilir. Windows'ta varsayılan olarak açıktır.";

    public override TweakState GetState()
    {
        var v = Reg.GetInt("HKCU", K, "AutoGameModeEnabled");
        return v is null or 1 ? TweakState.Applied : TweakState.NotApplied;
    }

    protected override void ApplyCore(List<ChangeRecord> changes)
    {
        Journal.RegSet(changes, "HKCU", K, "AutoGameModeEnabled", 1, RegistryValueKind.DWord);
        Journal.RegSet(changes, "HKCU", K, "AllowAutoGameMode", 1, RegistryValueKind.DWord);
    }
}

public sealed class GameDvrTweak : Tweak
{
    private const string Dvr = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string Store = @"System\GameConfigStore";
    public override string Id => "game_dvr_off";
    public override string Title => "Xbox arka plan kaydını kapat";
    public override bool OneClick => true;
    public override string WhatItDoes => "Game DVR'ı ve arka plan kaydını kapatır (Win+Alt+R kaydı da kapanır).";
    public override string Effect => "Arka plan kaydı açıksa ölçülebilir kazanç (özellikle zayıf ekran kartlarında). Zaten kapalıysa fark yok.";
    public override string? Risk => "Xbox Game Bar ile video kaydı alamazsın. NVIDIA/AMD kayıt araçları etkilenmez.";

    public override TweakState GetState()
    {
        bool hist = Reg.GetInt("HKCU", Dvr, "HistoricalCaptureEnabled") == 1;
        bool dvr = Reg.GetInt("HKCU", Store, "GameDVR_Enabled") is null or 1;
        return !hist && !dvr ? TweakState.Applied : TweakState.NotApplied;
    }

    protected override void ApplyCore(List<ChangeRecord> changes)
    {
        Journal.RegSet(changes, "HKCU", Dvr, "AppCaptureEnabled", 0, RegistryValueKind.DWord);
        Journal.RegSet(changes, "HKCU", Dvr, "HistoricalCaptureEnabled", 0, RegistryValueKind.DWord);
        Journal.RegSet(changes, "HKCU", Store, "GameDVR_Enabled", 0, RegistryValueKind.DWord);
    }
}

public sealed class MouseAccelTweak : Tweak
{
    public override string Id => "mouse_accel_off";
    public override string Title => "Fare ivmesini kapat";
    public override string WhatItDoes => "'İşaretçi hassasiyetini artır' seçeneğini kapatır: fareyi aynı mesafe oynatınca nişan hep aynı yere gider.";
    public override string Effect => "FPS'i değiştirmez. Masaüstünde ve raw input kullanmayan oyunlarda nişan tutarlılığı sağlar; CS2 ve Valorant'ta zaten etkisizdir.";

    public override TweakState GetState() => MouseAccel.Get()[2] == 0 ? TweakState.Applied : TweakState.NotApplied;

    protected override void ApplyCore(List<ChangeRecord> changes)
    {
        var p = MouseAccel.Get();
        changes.Add(new ChangeRecord { Type = "mouse", Data = string.Join(",", p) });
        MouseAccel.Set(0, 0, 0);
    }
}

public sealed class VbsTweak : Tweak
{
    public override string Id => "vbs_off";
    public override string Title => "Bellek Bütünlüğü'nü (VBS/HVCI) kapat";
    public override string WhatItDoes => "Windows Güvenliği > Cihaz güvenliği > Çekirdek yalıtımı > Bellek bütünlüğü ayarını kapatır.";
    public override string Effect => "Ölçülmüş etki ortalama ~%5, bazı oyunlarda %10–17. Windows 11 yeni kurulumlarda genelde açık gelir.";
    public override string? Risk => "Güvenlik takası: sürücü seviyesindeki zararlı yazılımlara karşı korumayı kapatır. Windows Güvenliği uyarı gösterebilir. İstediğin an geri alabilirsin.";
    public override bool NeedsAdmin => true;
    public override bool NeedsReboot => true;

    public override TweakState GetState()
    {
        var s = Vbs.Read();
        if (!s.VbsRunning && !s.HvciRunning) return TweakState.Applied;
        if (s.HvciConfiguredOff) return TweakState.PendingReboot;
        return TweakState.NotApplied;
    }

    protected override void ApplyCore(List<ChangeRecord> changes)
    {
        Journal.RegSet(changes, "HKLM", Vbs.HvciKey, "Enabled", 0, RegistryValueKind.DWord);
        Journal.RegSet(changes, "HKLM", Vbs.DgKey, "EnableVirtualizationBasedSecurity", 0, RegistryValueKind.DWord);
    }
}

/// <summary>
/// Windows 11 "Pencereli oyunlar için iyileştirmeler" (Ayarlar > Ekran > Grafik). Kenarlıksız/pencereli DirectX 10–11
/// oyunlarını eski "blit" sunumdan "flip" sunuma çevirir: tam ekran gibi düşük gecikme, VRR ve Otomatik HDR pencerede de çalışır.
/// </summary>
public sealed class WindowedOptTweak : Tweak
{
    private const string K = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string N = "DirectXUserGlobalSettings";
    public override string Id => "windowed_opt";
    public override string Title => "Pencereli oyunlar için iyileştirmeleri aç";
    public override bool OneClick => true;
    public override string WhatItDoes => "Windows 11'in kendi 'Pencereli oyunlar için iyileştirmeler' ayarını açar (Ayarlar > Ekran > Grafik).";
    public override string Effect => "Kenarlıksız pencerede oynuyorsan gecikme düşer, takılma azalır, G-Sync/FreeSync pencerede de çalışır. Tam ekranda oynuyorsan fark yok. DirectX 12 ve Vulkan oyunları zaten böyle çalışır.";

    public override TweakState GetState()
    {
        if (Environment.OSVersion.Version.Build < 22000) return TweakState.NotApplicable;
        return Flag(Reg.GetString("HKCU", K, N), "SwapEffectUpgradeEnable") == "1" ? TweakState.Applied : TweakState.NotApplied;
    }

    protected override void ApplyCore(List<ChangeRecord> changes) =>
        Journal.RegSet(changes, "HKCU", K, N, SetFlag(Reg.GetString("HKCU", K, N), "SwapEffectUpgradeEnable", "1"), RegistryValueKind.String);

    /// <summary>"A=1;B=0;" biçimindeki değerden birini okur.</summary>
    public static string? Flag(string? value, string name) =>
        (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(kv => kv.Length == 2 && kv[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv[1].Trim()).LastOrDefault();

    /// <summary>Diğer ayarları koruyarak birini yazar.</summary>
    public static string SetFlag(string? value, string name, string v)
    {
        var parts = (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0 && !p.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        parts.Add($"{name}={v}");
        return string.Join(";", parts) + ";";
    }
}

public sealed class HagsTweak : Tweak
{
    private const string K = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    public override string Id => "hags_on";
    public override string Title => "Donanım hızlandırmalı GPU zamanlamasını aç";
    public override string WhatItDoes => "HAGS'ı açar (Ayarlar > Ekran > Grafik > Varsayılan grafik ayarları).";
    public override string Effect => "Çoğu ölçümde fark gürültü düzeyinde (%0–2). NVIDIA DLSS Kare Oluşturma için gereklidir. Emin değilsen önce/sonra ölç.";
    public override bool NeedsAdmin => true;
    public override bool NeedsReboot => true;

    public override TweakState GetState() =>
        Reg.GetInt("HKLM", K, "HwSchMode") == 2 ? TweakState.Applied : TweakState.NotApplied;

    protected override void ApplyCore(List<ChangeRecord> changes) =>
        Journal.RegSet(changes, "HKLM", K, "HwSchMode", 2, RegistryValueKind.DWord);
}
