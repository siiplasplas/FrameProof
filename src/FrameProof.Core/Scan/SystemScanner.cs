using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FrameProof.Core.Games;
using FrameProof.Core.Startup;
using FrameProof.Core.Tweaks;
using FrameProof.Core.Util;

namespace FrameProof.Core.Scan;

public enum Severity { Good, Info, Warn, Bad }

public sealed record Finding(
    string Id,
    string Category,
    string Title,
    string Value,
    Severity Severity,
    string Advice = "",
    string? FixTweakId = null,
    string? Action = null);

public sealed record ScanReport(DateTime At, List<Finding> Findings)
{
    public int Count(Severity s) => Findings.Count(f => f.Severity == s);
}

/// <summary>
/// Sadece okur, hiçbir şeyi değiştirmez. Her kontrol kendi içinde hata yakalar; biri patlarsa diğerleri sürer.
/// </summary>
public static class SystemScanner
{
    public static ScanReport Run()
    {
        var f = new List<Finding>();
        void Try(Action<List<Finding>> check)
        {
            try { check(f); } catch (Exception ex) { f.Add(new Finding("error", "Sistem", "Bir kontrol çalışmadı", ex.Message, Severity.Info)); }
        }

        Try(Cpu);
        Try(Gpu);
        Try(GameGpus);
        Try(DisplayWiring);
        Try(Laptop);
        Try(Memory);
        Try(Pagefile);
        Try(Monitors);
        Try(VbsCheck);
        Try(Power);
        Try(GameBar);
        Try(Mouse);
        Try(Hags);
        Try(Disk);
        Try(StartupApps);
        Try(WindowsExtras);
        Try(Bios);
        Try(Os);
        return new ScanReport(DateTime.Now, f);
    }

    // ---------- Monitör kablosu hangi karta takılı ----------
    private static void DisplayWiring(List<Finding> f)
    {
        if (Device.IsLaptop) return; // laptop ekranı her zaman dahili karttan geçebilir, bu normal
        var d = Gpus.Discrete;
        if (d == null) return;
        var igpuWithScreen = Gpus.List().FirstOrDefault(a => a.Integrated && a.Outputs > 0);
        if (d.Outputs == 0 && igpuWithScreen != null)
            f.Add(new Finding("display_cable", "Monitör", "Monitör kablosu anakarta takılı",
                $"Ekran {igpuWithScreen.Name} üzerinden gösteriliyor, {d.Name} çıkışında monitör yok", Severity.Bad,
                "Kabloyu kasanın arkasında üstteki anakart çıkışından çıkarıp aşağıdaki ekran kartı çıkışına tak. " +
                "Böyle kalırsa oyunlar ya zayıf kartta çalışır ya da her kare ekstra kopyalanır; FPS çok düşer."));
    }

    // ---------- Laptop: performans modu ve MUX ----------
    private static void Laptop(List<Finding> f)
    {
        if (!Device.IsLaptop) return;
        var v = Device.Vendor;
        var app = v switch
        {
            "Lenovo" => "Legion Space / Lenovo Vantage (kısayol: Fn+Q, LED kırmızı olana kadar)",
            "ASUS" => "Armoury Crate > Turbo ya da Performans (kısayol: Fn+F5)",
            "MSI" => "MSI Center > User Scenario > Extreme Performance",
            "HP" => "OMEN Gaming Hub > Performans",
            "Dell" => "Alienware Command Center ya da G serisinde Game Shift",
            "Acer" => "NitroSense / PredatorSense > Turbo ya da Performans",
            "Monster" => "Monster Control Center > Performans modu",
            "Casper" => "Excalibur Control Center > Performans modu",
            _ => "üreticinin oyun uygulamasında 'Performans' ya da 'Turbo' modu",
        };
        f.Add(new Finding("laptop_mode", "Laptop", "Üretici performans modu", app, Severity.Info,
            "Oyun laptoplarında işlemci ve ekran kartının ne kadar güç çekeceğini bu mod belirler; sessiz modla performans modu arasında %10–30 fark olabilir. " +
            "Windows'tan değiştirilemez, FrameProof bu yüzden yalnız yerini gösterir. Oynarken prize takılı ol."));

        var d = Gpus.Discrete;
        if (d == null || !Gpus.IsHybrid) return;
        if (d.Outputs > 0)
        {
            f.Add(new Finding("mux", "Laptop", "Ekran bağlantısı", $"Ekran doğrudan {d.Name} kartına bağlı", Severity.Good));
            return;
        }
        var where = v switch
        {
            "ASUS" => "Armoury Crate > GPU Modu > Ultimate",
            "Lenovo" => "Legion Space / Vantage'da 'Hybrid Mode' kapalı (ya da BIOS'ta Graphics Device: Discrete Graphics)",
            "MSI" => "MSI Center > GPU Switch > Discrete Graphics Mode",
            "HP" => "OMEN Gaming Hub > Grafik > Ayrık mod",
            "Dell" => "BIOS > Display > Hybrid Graphics: Disabled (ya da Advanced Optimus)",
            "Acer" => "NitroSense / PredatorSense > GPU Modu (bazı modellerde)",
            _ => "üreticinin uygulamasında ya da BIOS'ta 'GPU Mode', 'Discrete', 'dGPU' ya da 'MUX' ayarı",
        };
        f.Add(new Finding("mux", "Laptop", "Ekran bağlantısı", "Laptop ekranı dahili kart üzerinden gösteriliyor (Optimus)", Severity.Info,
            $"Oyun {d.Name} kartında çalışsa bile her kare dahili karttan geçer; bu %5–15 FPS ve biraz gecikme kaybettirir. " +
            $"Laptopunda MUX anahtarı varsa 'yalnız harici kart' moduna al: {where}. Yeniden başlatma ister, pil daha hızlı biter. " +
            "MUX yoksa harici monitörü ekran kartına bağlı porta (genelde arkadaki HDMI) takmak aynı işi görür."));
    }

    // ---------- Laptop: oyunlar hangi ekran kartında ----------
    private static void GameGpus(List<Finding> f)
    {
        if (!Gpus.IsHybrid) return;
        var dgpu = Gpus.Discrete?.Name ?? "harici kart";
        var games = GameGpu.List();
        if (games.Count == 0) return;
        var weak = games.Where(g => g.Pref == GpuPref.PowerSaving).ToList();
        var unset = games.Count(g => g.Pref == GpuPref.Unset);
        if (weak.Count > 0)
            f.Add(new Finding("game_gpu", "Ekran kartı", "Zayıf karta sabitlenmiş oyunlar",
                $"{weak.Select(g => g.Name).Distinct().Count()} oyun işlemcinin içindeki karta sabitlenmiş: {string.Join(", ", weak.Select(g => g.Name).Distinct().Take(4))}",
                Severity.Bad,
                $"Bu oyunlar {dgpu} yerine zayıf kartta çalışıyor; FPS'leri 2–3 kat düşük olabilir. Düzelt'e basınca hepsi güçlü karta sabitlenir.",
                FixTweakId: "gpu_pin"));
        else if (unset > 0)
            f.Add(new Finding("game_gpu", "Ekran kartı", "Oyunların ekran kartı", $"{unset} oyunda kart seçimi Windows'a bırakılmış", Severity.Info,
                $"Windows çoğu zaman doğru seçer ama garanti değil. Sabitlemek zararsızdır: oyunlar her zaman {dgpu} ile açılır.",
                FixTweakId: "gpu_pin"));
        else
            f.Add(new Finding("game_gpu", "Ekran kartı", "Oyunların ekran kartı", $"{games.Count} oyunun hepsi {dgpu} kartına sabit", Severity.Good));
    }

    // ---------- Windows'un arka plan özellikleri ----------
    private static void WindowsExtras(List<Finding> f)
    {
        var list = TweakCatalog.All.Where(t => t.Group == RegValuesTweak.GroupName).ToList();
        var open = list.Where(t => { try { return t.GetState() == TweakState.NotApplied; } catch { return false; } }).ToList();
        f.Add(open.Count == 0
            ? new Finding("win_extras", "Windows", "Gereksiz arka plan özellikleri", "Hepsi kapalı", Severity.Good)
            : new Finding("win_extras", "Windows", "Gereksiz arka plan özellikleri", $"{open.Count} özellik açık: {string.Join(", ", open.Select(t => Short(t.Id)))}",
                open.Count >= 3 ? Severity.Warn : Severity.Info,
                "Edge'in arka planı, haber paneli, reklamlar gibi şeyler RAM ve arka plan işlemi harcar. Ayarlar sekmesindeki 'Windows'u sadeleştir' bölümünden kapatabilirsin.",
                Action: "tweaks"));
    }

    private static string Short(string id) => id switch
    {
        "edge_bg_off" => "Edge arka planı",
        "widgets_off" => "haber paneli",
        "ads_off" => "reklamlar",
        "delivery_opt_off" => "güncelleme paylaşımı",
        "bg_apps_off" => "Mağaza uygulamaları",
        "telemetry_low" => "telemetri",
        _ => id,
    };

    // ---------- BIOS (yalnız okunur; BIOS ayarı Windows'tan güvenle değiştirilemez) ----------
    private static void Bios(List<Finding> f)
    {
        var b = Wmi.Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
        if (b is null) return;
        var cs = Wmi.Query("SELECT Manufacturer, Model FROM Win32_ComputerSystem").FirstOrDefault();
        var board = Wmi.Query("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        string model = cs is null ? "" : $"{Wmi.Str(cs, "Manufacturer")} {Wmi.Str(cs, "Model")}".Trim();
        if (board != null && (model.Length == 0 || model.Contains("To Be Filled", StringComparison.OrdinalIgnoreCase) || model.Contains("System Product", StringComparison.OrdinalIgnoreCase)))
            model = $"{Wmi.Str(board, "Manufacturer")} {Wmi.Str(board, "Product")}".Trim();
        var ver = Wmi.Str(b, "SMBIOSBIOSVersion");
        var date = Wmi.Date(b, "ReleaseDate");
        var val = $"{model} · sürüm {ver}" + (date is { } d ? $" ({d:MM.yyyy})" : "");
        var ageDays = date is { } dd ? (DateTime.Now - dd).TotalDays : 0;
        f.Add(ageDays > 730
            ? new Finding("bios", "BIOS", "BIOS sürümü", val, Severity.Info,
                $"BIOS {(int)(ageDays / 365)} yıldan eski. Üreticinin destek sayfasında ({model}) daha yeni sürüm varsa kurulması kararlılık ve bazen performans getirir. " +
                "Güncellemeyi yalnız üreticinin kendi aracıyla, laptop prize takılıyken yap.")
            : new Finding("bios", "BIOS", "BIOS sürümü", val, Severity.Good));

        // Secure Boot ve CSM: Resizable BAR için gerekli
        var sb = Reg.GetInt("HKLM", @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        if (sb is not null)
            f.Add(new Finding("secureboot", "BIOS", "Önyükleme modu", sb == 1 ? "UEFI · Secure Boot açık" : "UEFI · Secure Boot kapalı",
                sb == 1 ? Severity.Good : Severity.Info,
                sb == 1 ? "" : "Valorant (Vanguard), Battlefield 6 ve bazı yeni oyunların anti-cheat'i Secure Boot ister. BIOS'ta 'Secure Boot' ayarından açılır.",
                Action: sb == 1 ? null : "bios"));
    }

    // ---------- İşlemci ----------
    private static void Cpu(List<Finding> f)
    {
        foreach (var o in Wmi.Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
        {
            var name = Regex.Replace(Wmi.Str(o, "Name"), @"\s+", " ");
            f.Add(new Finding("cpu", "İşlemci", "İşlemci", $"{name} · {Wmi.Long(o, "NumberOfCores")} çekirdek / {Wmi.Long(o, "NumberOfLogicalProcessors")} izlek", Severity.Info));

            // İki CCD'li X3D işlemcilerde oyunlar doğru çekirdeklere AMD sürücüsü + Game Bar ile yönlendirilir.
            if (Regex.IsMatch(name, @"(7950X3D|7900X3D|9950X3D|9900X3D)", RegexOptions.IgnoreCase))
            {
                bool driver = ServiceExists("3dvcache");
                f.Add(driver
                    ? new Finding("x3d", "İşlemci", "X3D çekirdek yönlendirme", "AMD 3D V-Cache sürücüsü kurulu", Severity.Good)
                    : new Finding("x3d", "İşlemci", "X3D çekirdek yönlendirme", "AMD 3D V-Cache sürücüsü bulunamadı", Severity.Warn,
                        "Bu işlemcide oyunların V-Cache'li çekirdeklerde çalışması için AMD Chipset sürücüsünü kur ve Xbox Game Bar'ı kaldırma."));
            }
        }
    }

    private static bool ServiceExists(string contains)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            return k?.GetSubKeyNames().Any(n => n.Contains(contains, StringComparison.OrdinalIgnoreCase)) ?? false;
        }
        catch { return false; }
    }

    // ---------- Ekran kartı ----------
    private static void Gpu(List<Finding> f)
    {
        var gpus = Wmi.Query("SELECT Name, DriverVersion, DriverDate, AdapterCompatibility FROM Win32_VideoController").ToList();
        bool hasDiscrete = gpus.Any(o => Regex.IsMatch(Wmi.Str(o, "Name"), @"NVIDIA|Radeon\s*(RX|Pro)|Arc\s*A|Arc\s*B", RegexOptions.IgnoreCase));
        foreach (var o in gpus)
        {
            var name = Wmi.Str(o, "Name");
            if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || name.Contains("Remote", StringComparison.OrdinalIgnoreCase))
            {
                if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
                    f.Add(new Finding("gpu_driver_missing", "Ekran kartı", "Ekran kartı sürücüsü", "Microsoft Temel Görüntü Bağdaştırıcısı", Severity.Bad,
                        "Ekran kartı sürücüsü kurulu değil! NVIDIA/AMD/Intel sitesinden sürücüyü kur. Bu tek başına FPS'i katlar."));
                continue;
            }
            var ver = Wmi.Str(o, "DriverVersion");
            var date = Wmi.Date(o, "DriverDate");
            bool nvidia = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
            var shown = nvidia ? NvidiaVersion(ver) ?? ver : ver;
            var vram = VramFor(name);
            var title = vram > 0 ? $"{name} · {Fmt.Bytes(vram)}" : name;
            bool integrated = hasDiscrete && !nvidia && !Regex.IsMatch(name, @"Radeon\s*(RX|Pro)|Arc", RegexOptions.IgnoreCase);
            f.Add(new Finding("gpu", "Ekran kartı", integrated ? "Dahili ekran kartı" : "Ekran kartı", title, Severity.Info));

            if (date is { } d)
            {
                var age = (DateTime.Now - d).TotalDays;
                var val = $"{shown} · {d:dd.MM.yyyy}";
                if (integrated)
                    f.Add(new Finding("igpu_driver", "Ekran kartı", "Dahili GPU sürücüsü", val, Severity.Info,
                        age > 365 ? "Oyunlar harici kartta çalıştığı için FPS'e etkisi küçük; yine de laptop üreticisinin sitesinden güncelleyebilirsin." : ""));
                else f.Add(age > 180
                    ? new Finding("gpu_driver", "Ekran kartı", "Sürücü", val + $" ({(int)(age / 30)} ay önce)", Severity.Warn,
                        "Sürücü 6 aydan eski. Yeni oyunlarda performans ve hata düzeltmeleri için güncelle.")
                    : new Finding("gpu_driver", "Ekran kartı", "Sürücü", val, Severity.Good));
            }

            if (nvidia) RebarNvidia(f);
        }
    }

    /// <summary>Windows sürücü sürümü 32.0.15.6094 → NVIDIA 560.94</summary>
    private static string? NvidiaVersion(string winVer)
    {
        var parts = winVer.Split('.');
        if (parts.Length < 4) return null;
        var digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5) return null;
        var last5 = digits[^5..];
        return $"{last5[..3]}.{last5[3..]}";
    }

    private static long VramFor(string name)
    {
        try
        {
            using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return 0;
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (!Regex.IsMatch(sub, @"^\d{4}$")) continue;
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k?.GetValue("DriverDesc")?.ToString() != name) continue;
                    var q = k.GetValue("HardwareInformation.qwMemorySize");
                    if (q is long l) return l;
                    if (q is byte[] b && b.Length >= 8) return BitConverter.ToInt64(b, 0);
                    var m = k.GetValue("HardwareInformation.MemorySize");
                    if (m is int i) return (uint)i;
                    if (m is byte[] mb && mb.Length >= 4) return BitConverter.ToUInt32(mb, 0);
                }
                catch { }
            }
        }
        catch { }
        return 0;
    }

    private static void RebarNvidia(List<Finding> f)
    {
        var smi = Shell.Sys("nvidia-smi.exe");
        if (!File.Exists(smi)) return;
        var r = Shell.Run(smi, "-q -d MEMORY", 15_000);
        var m = Regex.Match(r.StdOut, @"BAR1 Memory Usage\s*[\r\n]+\s*Total\s*:\s*(\d+)\s*MiB", RegexOptions.IgnoreCase);
        if (!m.Success) return;
        int bar = int.Parse(m.Groups[1].Value);
        // ReBAR'ı yalnız RTX 30 ve sonrası destekler; GTX 16/RTX 20'de uyarı vermek yanlış olur.
        var name = Shell.Run(smi, "--query-gpu=name --format=csv,noheader", 15_000).StdOut.Trim();
        var gen = Regex.Match(name, @"RTX\s*(?:A)?(\d{2})\d{2}", RegexOptions.IgnoreCase);
        bool supported = gen.Success && int.Parse(gen.Groups[1].Value) >= 30 && int.Parse(gen.Groups[1].Value) < 100;
        if (bar <= 256 && !supported)
        {
            f.Add(new Finding("rebar", "Ekran kartı", "Resizable BAR", "Bu ekran kartında yok", Severity.Info,
                "Resizable BAR, RTX 30 serisi ve sonrasında var. Senin kartında yapılacak bir şey yok."));
            return;
        }
        f.Add(bar > 256
            ? new Finding("rebar", "Ekran kartı", "Resizable BAR", $"Açık ({bar} MiB)", Severity.Good)
            : new Finding("rebar", "Ekran kartı", "Resizable BAR", "Kapalı", Severity.Warn,
                "BIOS'ta 'Above 4G Decoding' ve 'Re-Size BAR Support' ayarlarını aç. Bazı oyunlarda %0–10 kazandırır.", Action: "bios"));
    }

    // ---------- Bellek ----------
    private sealed record Dimm(long Capacity, int Speed, int Configured, int Type, int FormFactor, string Part, string Locator);

    private static void Memory(List<Finding> f)
    {
        var dimms = Wmi.Query("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, FormFactor, PartNumber, DeviceLocator, BankLabel FROM Win32_PhysicalMemory")
            .Select(o => new Dimm(Wmi.Long(o, "Capacity"), (int)Wmi.Long(o, "Speed"), (int)Wmi.Long(o, "ConfiguredClockSpeed"),
                (int)Wmi.Long(o, "SMBIOSMemoryType"), (int)Wmi.Long(o, "FormFactor"), Wmi.Str(o, "PartNumber"),
                Wmi.Str(o, "BankLabel") + " " + Wmi.Str(o, "DeviceLocator")))
            .Where(d => d.Capacity > 0)
            .ToList();
        if (dimms.Count == 0) return;

        long total = dimms.Sum(d => d.Capacity);
        string type = dimms[0].Type switch { 26 => "DDR4", 34 => "DDR5", 24 => "DDR3", _ => "RAM" };
        bool laptop = dimms.All(d => d.FormFactor == 12);
        int configured = dimms.Where(d => d.Configured > 0).Select(d => d.Configured).DefaultIfEmpty(0).Min();
        int rated = dimms.Select(d => Math.Max(d.Speed, PartSpeed(d.Part))).DefaultIfEmpty(0).Max();

        f.Add(new Finding("ram", "Bellek", "RAM", $"{Fmt.Bytes(total)} {type} · {dimms.Count} modül · {configured} MT/s", Severity.Info));

        if (total < 15L * 1024 * 1024 * 1024)
            f.Add(new Finding("ram_size", "Bellek", "RAM miktarı", Fmt.Bytes(total), Severity.Warn,
                "Güncel oyunlar için 16 GB önerilir. 8 GB'ta takılmalar ve uzun yüklemeler olur."));

        // XMP / EXPO
        if (configured > 0)
        {
            if (!laptop && rated > configured + 100)
                f.Add(new Finding("xmp", "Bellek", "RAM hız profili (XMP/EXPO)", $"{rated} MT/s destekliyor ama {configured} MT/s'de çalışıyor", Severity.Bad,
                    "BIOS'ta XMP (Intel) ya da EXPO/DOCP (AMD) profilini aç. İşlemciye yüklenen oyunlarda (Valorant, CS2) kabaca %10–20 ve takılmalarda belirgin iyileşme.",
                    Action: "bios"));
            else if (!laptop && ((type == "DDR4" && configured <= 2666) || (type == "DDR5" && configured <= 4800)))
                f.Add(new Finding("xmp", "Bellek", "RAM hız profili (XMP/EXPO)", $"{configured} MT/s (standart hız)", Severity.Warn,
                    "RAM standart hızda çalışıyor. RAM'in kutusunda daha yüksek hız yazıyorsa BIOS'ta XMP/EXPO'yu aç.", Action: "bios"));
            else
                f.Add(new Finding("xmp", "Bellek", "RAM hız profili (XMP/EXPO)", laptop ? $"{configured} MT/s (laptop)" : $"{configured} MT/s", Severity.Good));
        }

        // Kanal
        if (dimms.Count == 1)
            f.Add(new Finding("ram_channel", "Bellek", "Çift kanal", "Tek modül: tek kanal çalışıyor", laptop ? Severity.Warn : Severity.Bad,
                "Aynı modelden ikinci bir RAM takarsan bellek bant genişliği iki katına çıkar. İşlemciye yüklenen oyunlarda %20+ fark edebilir."));
        else
        {
            var channels = dimms.Select(d => ChannelOf(d.Locator)).ToList();
            if (channels.All(c => c != null) && channels.Distinct().Count() == 1)
                f.Add(new Finding("ram_channel", "Bellek", "Çift kanal", $"Modüller aynı kanalda görünüyor ({channels[0]})", Severity.Warn,
                    "RAM'leri anakart kılavuzundaki slotlara tak (genelde 2. ve 4. slot, A2/B2)."));
            else
                f.Add(new Finding("ram_channel", "Bellek", "Çift kanal", $"{dimms.Count} modül", Severity.Good));
        }
    }

    /// <summary>
    /// Sanal bellek (pagefile). 16 GB'ta RAM dolunca Windows buraya taşar; kapalıysa ya da büyüyemiyorsa oyun
    /// "bellek yetersiz" deyip çöker. RAM temizleyici yerine asıl bakılması gereken yer budur.
    /// </summary>
    private static void Pagefile(List<Finding> f)
    {
        const string fix = "Sistem > Hakkında > Gelişmiş sistem ayarları > Performans Ayarları > Gelişmiş > Sanal bellek > Değiştir: 'Tüm sürücüler için disk belleği dosyası boyutunu otomatik olarak yönet' kutusunu işaretle.";
        var cs = Wmi.Query("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem").FirstOrDefault();
        bool auto = cs != null && Wmi.Str(cs, "AutomaticManagedPagefile").Equals("True", StringComparison.OrdinalIgnoreCase);
        var usage = Wmi.Query("SELECT Name, AllocatedBaseSize, PeakUsage FROM Win32_PageFileUsage").ToList();
        var settings = Wmi.Query("SELECT Name, InitialSize, MaximumSize FROM Win32_PageFileSetting").ToList();

        if (usage.Count == 0 && !auto)
        {
            f.Add(new Finding("pagefile", "Bellek", "Sanal bellek", "Kapalı", Severity.Bad,
                "Sanal bellek kapalı. RAM dolunca oyun uyarı vermeden çöker ya da 'bellek yetersiz' der; 16 GB'ta güncel oyunlarda bu sık olur. " + fix));
            return;
        }

        long allocMb = usage.Sum(o => Wmi.Long(o, "AllocatedBaseSize"));
        long peakMb = usage.Sum(o => Wmi.Long(o, "PeakUsage"));
        string where = usage.Select(o => Path.GetPathRoot(Wmi.Str(o, "Name")) ?? "").FirstOrDefault(s => s.Length > 0) ?? "C:\\";
        long freeOnDrive = -1;
        try { freeOnDrive = new DriveInfo(where).AvailableFreeSpace; } catch { }
        long manualMax = settings.Select(o => Wmi.Long(o, "MaximumSize")).DefaultIfEmpty(0).Max();
        string mode = auto ? "Windows yönetiyor" : manualMax > 0 ? $"Elle ayarlı (en fazla {Fmt.Bytes(manualMax * 1024 * 1024)})" : "Windows yönetiyor (C:)";
        string value = $"{mode} · şu an {Fmt.Bytes(allocMb * 1024 * 1024)} ({where.TrimEnd('\\')})";

        // Elle ayarlanmış küçük üst sınır: sistem yönetimli girişte (0/0) sınır yoktur.
        long maxMb = settings.Select(o => Wmi.Long(o, "MaximumSize")).Where(v => v > 0).DefaultIfEmpty(0).Sum();
        bool sysManaged = auto || settings.Any(o => Wmi.Long(o, "MaximumSize") == 0);
        if (!sysManaged && maxMb > 0 && maxMb < 8192)
        {
            f.Add(new Finding("pagefile", "Bellek", "Sanal bellek", $"Elle küçük ayarlanmış (en fazla {Fmt.Bytes(maxMb * 1024 * 1024)})", Severity.Warn,
                "Bir 'optimizasyon' rehberi sanal belleği küçültmüş olabilir. RAM dolunca oyun çöker. " + fix));
            return;
        }

        if (freeOnDrive >= 0 && freeOnDrive < 4L * 1024 * 1024 * 1024)
        {
            f.Add(new Finding("pagefile", "Bellek", "Sanal bellek", $"{value} · diskte {Fmt.Bytes(freeOnDrive)} boş", Severity.Warn,
                $"Sanal bellek gerektiğinde büyüyemiyor çünkü {where.TrimEnd('\\')} sürücüsünde yer çok az. RAM dolunca oyun çökebilir. 'Temizlik' sekmesinden yer aç; en az 10 GB boş bırak."));
            return;
        }

        f.Add(new Finding("pagefile", "Bellek", "Sanal bellek", value + (peakMb > 0 ? $" · en çok {Fmt.Bytes(peakMb * 1024 * 1024)} kullanıldı" : ""), Severity.Good));
    }

    /// <summary>Parça numarasından satış hızını tahmin eder: F4-3600C16 → 3600, KF436C → 3600, BL8G32C → 3200.</summary>
    public static int PartSpeed(string part)
    {
        if (string.IsNullOrWhiteSpace(part)) return 0;
        var m = Regex.Match(part, @"(?<!\d)(2[1-9]\d{2}|[3-8]\d{3})(?!\d)");
        if (m.Success) return int.Parse(m.Value);
        m = Regex.Match(part, @"^KF(\d{3})C", RegexOptions.IgnoreCase);
        if (m.Success) return int.Parse(m.Groups[1].Value) * 10;
        m = Regex.Match(part, @"^BL\d+G(\d{2})C", RegexOptions.IgnoreCase);
        if (m.Success) return int.Parse(m.Groups[1].Value) * 100;
        return 0;
    }

    private static string? ChannelOf(string locator)
    {
        var m = Regex.Match(locator, @"Channel\s*([A-D])", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
        m = Regex.Match(locator, @"DIMM_?([A-D])\d", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
        m = Regex.Match(locator, @"(?<![A-Z])([A-D])\d(?!\d)");
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    // ---------- Monitör ----------
    private static void Monitors(List<Finding> f)
    {
        foreach (var d in Displays.List())
        {
            var label = $"{d.Width}×{d.Height}";
            if (d.CurrentHz < d.MaxHz)
                f.Add(new Finding("refresh", "Monitör", d.Primary ? "Ana monitör" : "Monitör", $"{label} · {d.CurrentHz} Hz (en fazla {d.MaxHz} Hz)", Severity.Bad,
                    $"Monitörün {d.MaxHz} Hz destekliyor ama {d.CurrentHz} Hz'de çalışıyor. Tek tıkla düzelt.", FixTweakId: "refresh_max"));
            else
                f.Add(new Finding("refresh", "Monitör", d.Primary ? "Ana monitör" : "Monitör", $"{label} · {d.CurrentHz} Hz", Severity.Good));
        }
    }

    // ---------- Windows ayarları ----------
    private static void VbsCheck(List<Finding> f)
    {
        var s = Vbs.Read();
        if (s.HvciRunning)
            f.Add(new Finding("vbs", "Windows", "Bellek Bütünlüğü (HVCI)", s.HvciConfiguredOff ? "Açık, kapanması için yeniden başlat" : "Açık", Severity.Warn,
                "Oyunlarda ortalama ~%5, bazılarında %10–17 kayıp ölçülmüş. Kapatmak bir güvenlik takasıdır; geri alınabilir.", FixTweakId: "vbs_off"));
        else if (s.VbsRunning)
            f.Add(new Finding("vbs", "Windows", "Sanallaştırma tabanlı güvenlik (VBS)", "Açık (HVCI kapalı)", Severity.Info,
                "VBS açık ama Bellek Bütünlüğü kapalı. Etkisi daha küçüktür.", FixTweakId: "vbs_off"));
        else
            f.Add(new Finding("vbs", "Windows", "Bellek Bütünlüğü (HVCI)", "Kapalı", Severity.Good));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    private static void Power(List<Finding> f)
    {
        var a = PowerPlanTweak.Active();
        var name = PowerPlanTweak.PlanName(a);
        if (a == PowerPlanTweak.Saver)
            f.Add(new Finding("power", "Windows", "Güç planı", name, Severity.Bad, "Güç tasarrufu planı işlemciyi yavaşlatır.", FixTweakId: "power_high"));
        else if (a is PowerPlanTweak.HighPerf or PowerPlanTweak.Ultimate)
            f.Add(new Finding("power", "Windows", "Güç planı", name, Severity.Good));
        else
            f.Add(new Finding("power", "Windows", "Güç planı", name, Severity.Info,
                "Dengeli plan modern işlemcilerde çoğu zaman yeterli. Yüksek performans saat hızı dalgalanmasını azaltır (%0–3).", FixTweakId: "power_high"));

        if (GetSystemPowerStatus(out var ps) && ps.BatteryFlag != 128 && ps.BatteryFlag != 255 && ps.ACLineStatus == 0)
            f.Add(new Finding("battery", "Windows", "Güç kaynağı", "Pilde çalışıyor", Severity.Bad,
                "Laptop pildeyken ekran kartı ve işlemci kısılır. Oynarken şarja tak."));
    }

    private static void GameBar(List<Finding> f)
    {
        var gm = TweakCatalog.Find("game_mode_on")!;
        f.Add(gm.GetState() == TweakState.Applied
            ? new Finding("game_mode", "Windows", "Oyun Modu", "Açık", Severity.Good)
            : new Finding("game_mode", "Windows", "Oyun Modu", "Kapalı", Severity.Warn, "Oyun Modu kapalı; arka plan işleri oyun sırasında araya girebilir.", FixTweakId: "game_mode_on"));

        bool hist = Reg.GetInt("HKCU", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled") == 1;
        f.Add(hist
            ? new Finding("game_dvr", "Windows", "Xbox arka plan kaydı", "Açık (sürekli kayıt alıyor)", Severity.Warn,
                "Arka plan kaydı sürekli video kodlar ve FPS düşürür.", FixTweakId: "game_dvr_off")
            : new Finding("game_dvr", "Windows", "Xbox arka plan kaydı", "Kapalı", Severity.Good));

        var wo = TweakCatalog.Find("windowed_opt")!;
        var ws = wo.GetState();
        if (ws == TweakState.NotApplied)
            f.Add(new Finding("windowed_opt", "Windows", "Pencereli oyunlar için iyileştirmeler", "Kapalı", Severity.Warn,
                "Kenarlıksız pencerede oynuyorsan gecikme ve takılma yaratır. Windows'un kendi ayarı, açması zararsız.", FixTweakId: "windowed_opt"));
        else if (ws == TweakState.Applied)
            f.Add(new Finding("windowed_opt", "Windows", "Pencereli oyunlar için iyileştirmeler", "Açık", Severity.Good));
    }

    private static void Mouse(List<Finding> f)
    {
        var t = TweakCatalog.Find("mouse_accel_off")!;
        if (t.GetState() != TweakState.Applied)
            f.Add(new Finding("mouse", "Windows", "Fare ivmesi", "Açık", Severity.Info,
                "Masaüstünde ve bazı oyunlarda nişan tutarlılığını bozar. CS2/Valorant raw input kullandığı için onları etkilemez.", FixTweakId: "mouse_accel_off"));
        else
            f.Add(new Finding("mouse", "Windows", "Fare ivmesi", "Kapalı", Severity.Good));
    }

    private static void Hags(List<Finding> f)
    {
        var v = Reg.GetInt("HKLM", @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
        var val = v switch { 2 => "Açık", 1 => "Kapalı", _ => "Varsayılan" };
        f.Add(new Finding("hags", "Windows", "GPU zamanlaması (HAGS)", val, Severity.Info,
            "Ölçümlerde fark genelde gürültü düzeyinde. DLSS Kare Oluşturma için açık olmalı.", FixTweakId: v == 2 ? null : "hags_on"));
    }

    // ---------- Disk ----------
    private static void Disk(List<Finding> f)
    {
        var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        long free = sys.AvailableFreeSpace, size = sys.TotalSize;
        double pct = 100.0 * free / size;
        var val = $"{sys.Name.TrimEnd('\\')} · {Fmt.Bytes(free)} boş ({Fmt.Num(pct, 0)}%)";
        if (free < 10L * 1024 * 1024 * 1024 || pct < 5)
            f.Add(new Finding("disk_free", "Disk", "Sistem diski boş alan", val, Severity.Bad,
                "Disk neredeyse dolu: Windows güncellemeleri, sanal bellek ve oyun yamaları sorun çıkarır. Temizlik sekmesini kullan.", Action: "cleanup"));
        else if (pct < 15)
            f.Add(new Finding("disk_free", "Disk", "Sistem diski boş alan", val, Severity.Warn, "Boş alan azalıyor. Temizlik sekmesine göz at.", Action: "cleanup"));
        else
            f.Add(new Finding("disk_free", "Disk", "Sistem diski boş alan", val, Severity.Good));

        // Sistem diski SSD mi?
        var letter = sys.Name[..1];
        var part = Wmi.Query($"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{letter}'", @"root\Microsoft\Windows\Storage").FirstOrDefault();
        if (part is null) return;
        var num = Wmi.Long(part, "DiskNumber");
        var disk = Wmi.Query($"SELECT MediaType, BusType, FriendlyName FROM MSFT_PhysicalDisk WHERE DeviceId = '{num}'", @"root\Microsoft\Windows\Storage").FirstOrDefault();
        if (disk is null) return;
        var media = Wmi.Long(disk, "MediaType");
        var bus = Wmi.Long(disk, "BusType");
        var dname = Wmi.Str(disk, "FriendlyName");
        if (media == 3)
            f.Add(new Finding("disk_type", "Disk", "Sistem diski", $"{dname} · HDD", Severity.Bad,
                "Windows mekanik diskte. SSD'ye geçmek açılışları, yüklemeleri ve oyun içi takılmaları ciddi azaltır."));
        else if (media == 4)
            f.Add(new Finding("disk_type", "Disk", "Sistem diski", $"{dname} · {(bus == 17 ? "NVMe SSD" : "SSD")}", Severity.Good));
    }

    private static void StartupApps(List<Finding> f)
    {
        var on = StartupManager.List().Count(i => i.Enabled);
        f.Add(on > 12
            ? new Finding("startup", "Windows", "Başlangıç uygulamaları", $"{on} uygulama açılışta başlıyor", Severity.Warn,
                "Çok fazla uygulama açılışta başlıyor; arka planda RAM ve işlemci yer. Başlangıç sekmesinden gereksizleri kapat.", Action: "startup")
            : new Finding("startup", "Windows", "Başlangıç uygulamaları", $"{on} uygulama", on > 6 ? Severity.Info : Severity.Good,
                on > 6 ? "Kullanmadıklarını Başlangıç sekmesinden kapatabilirsin." : "", Action: on > 6 ? "startup" : null));
    }

    private static void Os(List<Finding> f)
    {
        var caption = Wmi.Query("SELECT Caption FROM Win32_OperatingSystem").Select(o => Wmi.Str(o, "Caption")).FirstOrDefault() ?? "Windows";
        var disp = Reg.GetString("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion");
        var build = Reg.GetString("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild");
        f.Add(new Finding("os", "Windows", "Sürüm", $"{caption.Replace("Microsoft ", "")} {disp} (derleme {build})", Severity.Info));

        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (up.TotalDays >= 7)
            f.Add(new Finding("uptime", "Windows", "Açık kalma süresi", $"{(int)up.TotalDays} gündür yeniden başlatılmadı", Severity.Info,
                "Uzun süre açık kalan Windows'ta bellek ve sürücü sorunları birikebilir. Oynamadan önce yeniden başlat."));
    }
}
