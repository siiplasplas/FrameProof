using FrameProof.Core.Util;
using Microsoft.Win32;

namespace FrameProof.Core.Tweaks;

/// <summary>
/// Windows'u sadeleştiren ayarlar. Hepsi resmi Windows/Edge ilkesi ya da kullanıcı ayarıdır; servis silme,
/// dosya silme, Defender/Update/Güvenlik Duvarı'na dokunma yoktur. Her değer önce günlüğe yazılır, geri alınabilir.
/// </summary>
public abstract class RegValuesTweak : Tweak
{
    public const string GroupName = "Windows'u sadeleştir";
    public override string Group => GroupName;

    protected abstract (string Hive, string Key, string Name, int Value)[] Values { get; }

    public override TweakState GetState()
    {
        bool all = Values.All(v => Reg.GetInt(v.Hive, v.Key, v.Name) == v.Value);
        if (all && NeedsReboot && !Active()) return TweakState.PendingReboot;
        return all ? TweakState.Applied : TweakState.NotApplied;
    }

    /// <summary>Yeniden başlatma gereken ayarlarda: ayar yazıldı ama etkisi henüz başladı mı.</summary>
    protected virtual bool Active() => true;

    protected override void ApplyCore(List<ChangeRecord> changes)
    {
        foreach (var v in Values)
            if (Reg.GetInt(v.Hive, v.Key, v.Name) != v.Value)
                Journal.RegSet(changes, v.Hive, v.Key, v.Name, v.Value, RegistryValueKind.DWord);
    }
}

public sealed class EdgeBackgroundTweak : RegValuesTweak
{
    private const string K = @"SOFTWARE\Policies\Microsoft\Edge";
    public override string Id => "edge_bg_off";
    public override string Title => "Edge'in arka planda çalışmasını kapat";
    public override string WhatItDoes => "Edge'in 'Başlangıç hızlandırma' ve 'Edge kapalıyken arka planda çalış' özelliklerini kapatır. Edge'i kullanmaya devam edebilirsin.";
    public override string Effect => "Edge kapalıyken arka planda duran birkaç Edge işlemi ve 100–400 MB RAM geri gelir. FPS'e doğrudan etkisi küçük; 8–16 GB RAM'li laptoplarda oyun içi takılmayı azaltabilir.";
    public override string? Risk => "Edge ilk açılışta 1–2 sn geç açılır. Edge ayarlarında 'Kuruluşunuz tarafından yönetiliyor' yazısı görünür; bu normaldir.";
    public override bool NeedsAdmin => true;
    public override bool OneClick => true;

    protected override (string, string, string, int)[] Values => new[]
    {
        ("HKLM", K, "StartupBoostEnabled", 0),
        ("HKLM", K, "BackgroundModeEnabled", 0),
    };
}

public sealed class WidgetsTweak : RegValuesTweak
{
    public override string Id => "widgets_off";
    public override string Title => "Pencere öğelerini (haber, hava durumu) kapat";
    public override string WhatItDoes => "Görev çubuğundaki haber/hava durumu panelini kapatır. Bu panel arka planda Widgets ve WebView2 işlemleri çalıştırır.";
    public override string Effect => "Arka planda 100–300 MB RAM ve ara sıra internet/işlemci kullanımı biter. Tam etkisi oturumu kapatıp açınca görülür.";
    public override bool NeedsAdmin => true;
    public override bool OneClick => true;

    protected override (string, string, string, int)[] Values => new[]
    {
        ("HKLM", @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0),
    };
}

public sealed class AdsTweak : RegValuesTweak
{
    private const string K = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    public override string Id => "ads_off";
    public override string Title => "Windows reklamlarını ve gizli uygulama kurulumunu kapat";
    public override string WhatItDoes => "Başlat menüsü önerilerini, ipucu bildirimlerini, reklam kimliğini ve Windows'un sormadan uygulama (Candy Crush vb.) kurmasını kapatır.";
    public override string Effect => "FPS'i değiştirmez. Windows'un arkadan uygulama indirip diske yazmasını ve oyun sırasında ipucu bildirimi atmasını engeller.";
    public override bool OneClick => true;

    protected override (string, string, string, int)[] Values => new[]
    {
        ("HKCU", K, "SilentInstalledAppsEnabled", 0),
        ("HKCU", K, "SystemPaneSuggestionsEnabled", 0),
        ("HKCU", K, "SoftLandingEnabled", 0),
        ("HKCU", K, "SubscribedContent-338388Enabled", 0),
        ("HKCU", K, "SubscribedContent-338389Enabled", 0),
        ("HKCU", K, "SubscribedContent-353694Enabled", 0),
        ("HKCU", K, "SubscribedContent-353696Enabled", 0),
        ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0),
    };
}

public sealed class DeliveryOptimizationTweak : RegValuesTweak
{
    public override string Id => "delivery_opt_off";
    public override string Title => "Güncellemeleri başkalarına yüklemeyi kapat";
    public override string WhatItDoes => "Teslim İyileştirme'nin eşler arası paylaşımını kapatır. Windows güncellemeleri yine iner, ama bilgisayarın başka bilgisayarlara güncelleme parçası yüklemez.";
    public override string Effect => "Oyun sırasında arkada çalışan yüklemenin ping'i zıplatması engellenir. Güncellemeler normal şekilde Microsoft'tan iner.";
    public override bool NeedsAdmin => true;
    public override bool OneClick => true;

    protected override (string, string, string, int)[] Values => new[]
    {
        ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0),
    };
}

public sealed class BackgroundAppsTweak : RegValuesTweak
{
    public override string Id => "bg_apps_off";
    public override string Title => "Mağaza uygulamalarının arka planda çalışmasını kapat";
    public override string WhatItDoes => "Microsoft Store uygulamalarının (Hava Durumu, Haberler, Telefon Bağlantısı, Posta vb.) sen açmadan arka planda çalışmasını engeller.";
    public override string Effect => "Arka plandaki gereksiz işlemleri ve RAM kullanımını azaltır. Steam, Discord gibi normal programları etkilemez.";
    public override string? Risk => "Telefon Bağlantısı ve Posta gibi Mağaza uygulamaları kapalıyken bildirim göstermez. Xbox uygulaması ve Game Pass indirmeleri etkilenmez.";
    public override bool NeedsAdmin => true;

    protected override (string, string, string, int)[] Values => new[]
    {
        ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", 2),
    };
}

public sealed class TelemetryTweak : RegValuesTweak
{
    private const string Svc = @"SYSTEM\CurrentControlSet\Services\DiagTrack";
    public override string Id => "telemetry_low";
    public override string Title => "Microsoft'a veri gönderimini en aza indir";
    public override string WhatItDoes => "Tanılama verisini 'yalnız gerekli' seviyesine indirir ve bu veriyi toplayan 'Bağlı Kullanıcı Deneyimleri ve Telemetri' (DiagTrack) servisini kapatır.";
    public override string Effect => "DiagTrack ara sıra arka planda işlemci ve disk kullanır; kapanınca bu ani yükler biter. FPS'e etkisi küçük ama oyun içi ani takılmaların bir kaynağını kaldırır.";
    public override string? Risk => "Windows Insider programı çalışmaz. Windows Update, Defender ve Mağaza normal çalışmaya devam eder.";
    public override bool NeedsAdmin => true;
    public override bool NeedsReboot => true;

    protected override (string, string, string, int)[] Values => new[]
    {
        ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1),
        ("HKLM", Svc, "Start", 4),
    };

    public override TweakState GetState()
    {
        if (Reg.GetInt("HKLM", Svc, "Start") is null) return TweakState.NotApplicable; // servis yok
        return base.GetState();
    }

    protected override bool Active()
    {
        try
        {
            var o = Wmi.Query("SELECT State FROM Win32_Service WHERE Name='DiagTrack'").FirstOrDefault();
            return o is null || Wmi.Str(o, "State") != "Running";
        }
        catch { return true; }
    }
}
