using FrameProof.Core.Util;

namespace FrameProof.Core.Scan;

/// <summary>BIOS'ta yapılacak bir ayar: ne, neden, nerede.</summary>
public sealed record BiosStep(string FindingId, string Title, string Why, string Where);

/// <summary>Bu bilgisayara özel BIOS yapılacaklar listesi.</summary>
public sealed record BiosGuideInfo(string Vendor, string Model, bool IsLaptop, bool CanRebootToFirmware, string EnterKey, List<BiosStep> Steps);

/// <summary>
/// Tarama bulgularından ve anakart/laptop üreticisinden BIOS rehberi çıkarır.
/// BIOS ayarları Windows'tan güvenle değiştirilemez (her üreticinin kendi yapısı var); bu yüzden FrameProof
/// yalnız ne yapılacağını ve menünün nerede olduğunu söyler, istersen bilgisayarı doğrudan BIOS'a yeniden başlatır.
/// </summary>
public static class BiosGuide
{
    public static BiosGuideInfo Build(ScanReport r)
    {
        var v = Device.Vendor;
        var steps = new List<BiosStep>();
        foreach (var f in r.Findings)
        {
            // Yalnız BIOS'ta yapılacak bir şey olan bulgular ("ReBAR bu kartta yok" gibi bilgiler değil)
            if (f.Severity == Severity.Good || f.Action != "bios") continue;
            var s = StepFor(f.Id, v);
            // Secure Boot FPS artırmaz; yalnız anti-cheat için gerekir, o yüzden kapalıysa en sona.
            if (s != null && steps.All(x => x.FindingId != s.FindingId)) steps.Add(s);
        }
        steps = steps.OrderBy(s => s.FindingId switch { "xmp" => 0, "rebar" => 1, _ => 2 }).ToList();
        return new BiosGuideInfo(v, Device.Model, Device.IsLaptop, Device.IsUefi, EnterKey(v, Device.IsLaptop), steps);
    }

    /// <summary>Tek bir bulgu için "Nasıl yapılır?" metni.</summary>
    public static string HowTo(string findingId)
    {
        var v = Device.Vendor;
        var s = StepFor(findingId, v);
        var key = EnterKey(v, Device.IsLaptop);
        if (s == null) return $"Bu ayar BIOS'tan yapılır. Bilgisayar açılırken {key} tuşuna bas.";
        return $"{s.Title}\n\n{s.Why}\n\n" +
               $"1. Bilgisayarı yeniden başlat, açılırken {key} tuşuna art arda bas (ya da Tarama sekmesindeki 'BIOS'a yeniden başlat').\n" +
               $"2. {s.Where}\n" +
               "3. F10 ile kaydedip çık.\n\n" +
               "Menü adları BIOS sürümüne göre biraz değişebilir. Bilgisayar açılmazsa çoğu anakart 30–60 sn içinde ayarı kendisi geri alır.";
    }

    public static string EnterKey(string vendor, bool laptop) => vendor switch
    {
        "MSI" or "Gigabyte" => "Del",
        "ASUS" => laptop ? "F2" : "Del ya da F2",
        "ASRock" => "F2 ya da Del",
        "Lenovo" => "F2 (bazı modellerde Fn+F2 ya da yandaki küçük Novo düğmesi)",
        "HP" => "Esc, ardından F10",
        "Dell" or "Acer" or "Monster" or "Casper" or "Samsung" or "Huawei" or "Razer" or "Tongfang" or "Clevo" => "F2",
        "Microsoft" => "sesi açma tuşunu basılı tutarak güç düğmesine",
        _ => "Del ya da F2",
    };

    private static BiosStep? StepFor(string id, string v) => id switch
    {
        "xmp" => new BiosStep("xmp", "RAM'i kutusunda yazan hıza çıkar (XMP / EXPO)",
            "RAM çoğu bilgisayarda kutudaki hızda değil, en düşük standart hızda çalışır. Profili açmak özellikle işlemcinin sınırladığı oyunlarda %5–15 FPS kazandırır; en ucuz ve en büyük kazanç budur.",
            v switch
            {
                "ASUS" => "F7 ile Advanced Mode'a geç. Ai Tweaker > Ai Overclock Tuner ayarını XMP I (AMD'de EXPO I ya da DOCP) yap.",
                "MSI" => "Ana ekranın üst kısmındaki 'XMP' (ya da 'A-XMP' / 'EXPO') düğmesine bas, Profile 1'i seç. Yoksa OC > Extreme Memory Profile.",
                "Gigabyte" => "Tweaker > Extreme Memory Profile (X.M.P.) ayarını Profile1 yap (AMD'de EXPO).",
                "ASRock" => "OC Tweaker > DRAM Configuration > Load XMP Setting (AMD'de EXPO) ayarını Profile 1 yap.",
                _ => "XMP, EXPO ya da DOCP ayarını bul (genelde 'OC', 'Ai Tweaker', 'Tweaker' ya da 'Memory' menüsünde) ve Profile 1'i seç.",
            }),
        "rebar" => new BiosStep("rebar", "Resizable BAR'ı aç",
            "İşlemcinin ekran kartı belleğinin tamamına tek seferde erişmesini sağlar. Destekleyen oyunlarda %0–10; NVIDIA kartlarda sürücü yalnız uygun oyunlarda kullanır, zararı yok.",
            v switch
            {
                "ASUS" => "Advanced > PCI Subsystem Settings > Above 4G Decoding: Enabled, Re-Size BAR Support: Auto. Ayrıca Boot > CSM > Launch CSM: Disabled olmalı.",
                "MSI" => "Settings > Advanced > PCI Subsystem Settings > Above 4G memory: Enabled, Re-Size BAR Support: Enabled. Settings > Advanced > Windows OS Configuration'da BIOS UEFI/CSM Mode: UEFI olmalı.",
                "Gigabyte" => "Settings > IO Ports > Above 4G Decoding: Enabled, Re-Size BAR Support: Auto. Boot > CSM Support: Disabled olmalı.",
                "ASRock" => "Advanced > Chipset Configuration > Above 4G Decoding: Enabled, Re-Size BAR Support: Enabled. Boot > CSM: Disabled olmalı.",
                _ => "'Above 4G Decoding' ve 'Re-Size BAR Support' ayarlarını Enabled yap (genelde Advanced > PCI menüsünde). 'CSM' kapalı olmalı. Laptoplarda bu ayar çoğu zaman BIOS'ta yoktur.",
            }),
        "secureboot" => new BiosStep("secureboot", "Secure Boot'u aç (yalnız anti-cheat için)",
            "FPS'e etkisi yok. Valorant (Vanguard), Battlefield 6 ve bazı yeni oyunların anti-cheat'i Secure Boot olmadan açılmaz. Bu oyunları oynamıyorsan dokunma.",
            v switch
            {
                "ASUS" => "Boot > Secure Boot > OS Type: Windows UEFI mode.",
                "MSI" => "Settings > Security > Secure Boot: Enabled.",
                "Gigabyte" => "Boot > Secure Boot: Enabled.",
                "ASRock" => "Security > Secure Boot: Enabled.",
                _ => "Security ya da Boot menüsünde Secure Boot: Enabled.",
            }),
        _ => null,
    };
}
