using System.Globalization;
using System.Text;
using FrameProof.Core.Cleanup;
using FrameProof.Core.Games;
using FrameProof.Core.Measure;
using FrameProof.Core.Tweaks;
using FrameProof.Core.Update;
using FrameProof.Core.Util;
using Microsoft.Win32;

/// <summary>
/// Sistemi değiştirmeden çalışan güvenlik testleri: sahte klasörde silme, test kayıt anahtarında geri alma,
/// sentetik kare verisiyle istatistik. Gerçek kullanıcı dosyalarına ve ayarlarına dokunmaz.
/// </summary>
static class SelfTest
{
    static int fails;

    static void Check(bool ok, string name)
    {
        Console.WriteLine($"{(ok ? "GEÇTİ " : "KALDI ")} {name}");
        if (!ok) fails++;
    }

    public static int Run(string dir)
    {
        fails = 0;
        try { SafeDeleteTests(dir); } catch (Exception ex) { Check(false, "SafeDelete istisna: " + ex.Message); }
        try { JournalTests(); } catch (Exception ex) { Check(false, "Journal istisna: " + ex.Message); }
        try { HardModeTests(); } catch (Exception ex) { Check(false, "Sert mod istisna: " + ex.Message); }
        try { CompressTests(dir); } catch (Exception ex) { Check(false, "Sıkıştırma istisna: " + ex.Message); }
        try { GpuPrefTests(); } catch (Exception ex) { Check(false, "GpuPref istisna: " + ex.Message); }
        try { NewFeatureTests(); } catch (Exception ex) { Check(false, "Yeni özellik istisna: " + ex.Message); }
        try { StatsTests(dir); } catch (Exception ex) { Check(false, "Stats istisna: " + ex.Message); }
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        Console.WriteLine(fails == 0 ? "\nTüm testler geçti." : $"\n{fails} test kaldı.");
        return fails == 0 ? 0 : 1;
    }

    static void Touch(string path, int sizeBytes, int ageDays)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        var t = DateTime.Now.AddDays(-ageDays);
        File.SetCreationTime(path, t);
        File.SetLastWriteTime(path, t);
    }

    static void SafeDeleteTests(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var target = Path.Combine(dir, "temizlenecek");
        var outside = Path.Combine(dir, "disaridaki_degerli");
        Touch(Path.Combine(target, "eski1.tmp"), 1000, 5);
        Touch(Path.Combine(target, "alt", "eski2.tmp"), 2000, 5);
        Touch(Path.Combine(target, "yeni.tmp"), 500, 0);
        Touch(Path.Combine(outside, "onemli.txt"), 300, 30);
        File.SetAttributes(Path.Combine(target, "eski1.tmp"), FileAttributes.ReadOnly);

        // Hedefin içine dışarıyı gösteren bir junction koy: silme işlemi içine GİRMEMELİ.
        var junction = Path.Combine(target, "baglanti");
        var mk = Shell.Run(Shell.Sys("cmd.exe"), $"/c mklink /J \"{junction}\" \"{outside}\"");
        Check(mk.Ok && Directory.Exists(junction), "junction oluşturuldu");

        // Açık (kilitli) dosya atlanmalı
        var locked = Path.Combine(target, "kilitli.tmp");
        Touch(locked, 400, 5);

        var m = new CleanStats();
        SafeDelete.Measure(target, "*", true, DateTime.UtcNow.AddDays(-1), m);
        Check(m.Bytes == 1000 + 2000 + 400 && m.Files == 3, $"ölçüm junction içini saymadı ve yeni dosyayı atladı ({m.Bytes} B, {m.Files} dosya)");

        var s = new CleanStats();
        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            SafeDelete.Delete(target, "*", true, DateTime.UtcNow.AddDays(-1), s, CancellationToken.None);

        Check(File.Exists(Path.Combine(outside, "onemli.txt")), "junction hedefindeki dosya korunuyor");
        Check(!Directory.Exists(junction), "junction'ın kendisi kaldırıldı");
        Check(File.Exists(Path.Combine(target, "yeni.tmp")), "yeni dosya korunuyor");
        Check(!File.Exists(Path.Combine(target, "eski1.tmp")), "salt okunur eski dosya silindi");
        Check(!File.Exists(Path.Combine(target, "alt", "eski2.tmp")), "alt klasördeki eski dosya silindi");
        Check(File.Exists(locked) && s.Skipped >= 1, "kilitli dosya atlandı");
        Check(Directory.Exists(target), "kök klasör yerinde kaldı");

        // Kök klasörün kendisi junction ise hiç dokunma
        var rootLink = Path.Combine(dir, "kok_baglanti");
        Shell.Run(Shell.Sys("cmd.exe"), $"/c mklink /J \"{rootLink}\" \"{outside}\"");
        var s2 = new CleanStats();
        SafeDelete.Delete(rootLink, "*", true, DateTime.UtcNow, s2, CancellationToken.None);
        Check(File.Exists(Path.Combine(outside, "onemli.txt")) && s2.Files == 0, "kökü junction olan klasöre girilmedi");

        // Yasak kökler
        bool threw(string p)
        {
            try { SafeDelete.Measure(p, "*", true, DateTime.UtcNow, new CleanStats()); return false; }
            catch (InvalidOperationException) { return true; }
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Check(threw(@"C:\"), "C:\\ toplu silmeye kapalı");
        Check(threw(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), "Windows klasörü kapalı");
        Check(threw(profile), "kullanıcı klasörü kapalı");
        Check(threw(Path.Combine(profile, "Downloads")), "İndirilenler kapalı");
        Check(threw(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)), "Masaüstü kapalı");
        Check(threw(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)), "Belgeler kapalı");
        Check(SafeDelete.IsSafeRoot(target), "normal klasör izinli");

        // Katalogdaki hiçbir klasör hedefi yasak bir kök olmamalı
        foreach (var t in CleanupCatalog.Build().OfType<FolderTarget>())
            foreach (var g in t.Groups)
                foreach (var spec in g.Specs())
                    if (spec.Recursive || spec.Pattern.Contains('*'))
                        Check(SafeDelete.IsSafeRoot(spec.Path), $"katalog hedefi güvenli: {t.Id} → {spec.Path}");
    }

    static void JournalTests()
    {
        const string key = @"Software\FrameProofTest";
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, false);
            using (var k = Registry.CurrentUser.CreateSubKey(key))
            {
                k.SetValue("Var", 5, RegistryValueKind.DWord);
                k.SetValue("Bin", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
            }
            var ch = new List<ChangeRecord>();
            Journal.RegSet(ch, "HKCU", key, "Var", 9, RegistryValueKind.DWord);
            Journal.RegSet(ch, "HKCU", key, "Yok", "yeni", RegistryValueKind.String);
            Journal.RegSet(ch, "HKCU", key, "Bin", new byte[] { 9, 9 }, RegistryValueKind.Binary);
            Check(Reg.GetInt("HKCU", key, "Var") == 9, "kayıt değeri yazıldı");
            Journal.Revert(ch);
            Check(Reg.GetInt("HKCU", key, "Var") == 5, "DWORD eski değerine döndü");
            Check(Reg.Get("HKCU", key, "Yok") is null, "önceden olmayan değer silindi");
            Check(Reg.Get("HKCU", key, "Bin") is byte[] b && b.SequenceEqual(new byte[] { 1, 2, 3 }), "binary değer eski haline döndü");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, false);
        }
    }

    /// <summary>Sert mod: yalnız okur ve olmayan bir görevi geri almayı dener. Hiçbir ayar uygulanmaz.</summary>
    static void HardModeTests()
    {
        var all = TaskSched.Find(_ => true);
        Check(all.Count > 0, $"Görev Zamanlayıcı okunuyor ({all.Count} görev)");
        Journal.Revert(new List<ChangeRecord> { new() { Type = "task", Name = @"\FrameProofOlmayanGorev_7f3a", Data = "1" } });
        Check(true, "silinmiş görevi geri almak hata vermiyor");
        Check(!TaskSched.IsNotFound(new UnauthorizedAccessException()), "erişim reddi 'görev yok' sayılmıyor");
        foreach (var t in TweakCatalog.All.Where(t => t.Group == HardMode.GroupName))
        {
            TweakState s = TweakState.NotApplicable;
            bool ok = true;
            try { s = t.GetState(); } catch { ok = false; }
            Check(ok, $"{t.Id} durumu okunuyor: {s}");
        }
        var r = new FrameProof.Core.Measure.RunSummary { RamAvailMinMb = 500, PagesInAvg = 400, CommitPeakPct = 70 };
        Check(r.RamShort == true, "az boş RAM + yoğun diskten okuma = RAM yetmedi");
        r = new FrameProof.Core.Measure.RunSummary { RamAvailMinMb = 3000, PagesInAvg = 20, CommitPeakPct = 60 };
        Check(r.RamShort == false, "bol boş RAM = RAM yetti");
        Check(new FrameProof.Core.Measure.RunSummary().RamShort is null, "eski kayıtta RAM yargısı yok");
    }

    static void CompressTests(string dir)
    {
        Check(!GameCompress.IsSafeFolder(@"C:\"), "sürücü kökü sıkıştırılmaz");
        Check(!GameCompress.IsSafeFolder(@"D:\Games"), "kütüphane kökü sıkıştırılmaz");
        Check(!GameCompress.IsSafeFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "kullanıcı klasörü sıkıştırılmaz");
        Check(!GameCompress.IsSafeFolder(@"C:\Program Files (x86)\Steam\steamapps\common"), @"steamapps\common sıkıştırılmaz");
        Check(!GameCompress.IsSafeFolder(@"C:\Windows\System32\x"), "Windows altı sıkıştırılmaz");
        Check(GameCompress.IsSafeFolder(@"D:\SteamLibrary\steamapps\common\Oyun"), "oyun klasörü sıkıştırılabilir");
        var games = GameLibrary.List();
        Check(true, $"kurulu oyunlar okunuyor ({games.Count} oyun: {string.Join(", ", games.Take(5).Select(x => x.Name))})");

        var g = Path.Combine(dir, "lib", "steamapps", "common", "Sahte Oyun");
        var text = string.Concat(Enumerable.Repeat("FrameProof sıkıştırma testi 0123456789 ", 30000)); // ~1,2 MB, iyi sıkışır
        Directory.CreateDirectory(Path.Combine(g, "data"));
        Directory.CreateDirectory(Path.Combine(g, "EasyAntiCheat"));
        File.WriteAllText(Path.Combine(g, "data", "a.txt"), text);
        File.WriteAllText(Path.Combine(g, "data", "b.txt"), text);
        File.WriteAllText(Path.Combine(g, "oyun.exe"), text);
        File.WriteAllText(Path.Combine(g, "EasyAntiCheat", "eac.bin"), text);
        File.SetAttributes(Path.Combine(g, "data", "b.txt"), FileAttributes.ReadOnly);
        var game = new InstalledGame("Sahte Oyun", g, "test");
        var before = GameCompress.Measure(g);
        var res = GameCompress.Compress(game, null, CancellationToken.None);
        var after = GameCompress.Measure(g);
        Check(res.Compressed == 2 && res.Failed == 0, $"iki veri dosyası sıkıştı ({res.Compressed} sıkıştı, {res.Skipped} atlandı, {res.Failed} hata)");
        // 4 dosyadan 2'si (exe ve anti-cheat) bilerek sıkıştırılmaz; diğer ikisi neredeyse sıfıra iner.
        Check(after.OnDisk < before.OnDisk * 0.6,$"diskte kapladığı yer azaldı ({before.OnDisk:N0} → {after.OnDisk:N0})");
        Check(File.ReadAllText(Path.Combine(g, "data", "a.txt")) == text, "sıkışmış dosya aynen okunuyor");
        Check(File.GetAttributes(Path.Combine(g, "data", "b.txt")).HasFlag(FileAttributes.ReadOnly), "salt okunur özniteliği korundu");
        Check(!GameCompress.IsCompressed(Path.Combine(g, "oyun.exe")), "exe'ye dokunulmadı");
        Check(!GameCompress.IsCompressed(Path.Combine(g, "EasyAntiCheat", "eac.bin")), "anti-cheat klasörüne dokunulmadı");
        Check(GameCompress.IsRecorded(g), "günlüğe geri alma kaydı eklendi");
        GameCompress.Compress(game, null, CancellationToken.None);
        var entries = Journal.Load().Where(e => e.Changes.Any(c => c.Type == "wof" && c.Name!.EndsWith("Sahte Oyun"))).ToList();
        Check(entries.Count == 1, "yeniden sıkıştırmak ikinci kayıt eklemiyor");
        foreach (var e in entries) Journal.Undo(e.Id);
        var undone = GameCompress.Measure(g);
        Check(undone.OnDisk >= before.OnDisk && !GameCompress.IsCompressed(Path.Combine(g, "data", "a.txt")), $"geri al: eski boyutuna döndü ({undone.OnDisk:N0})");
        Check(File.ReadAllText(Path.Combine(g, "data", "b.txt")) == text, "geri alınan dosya aynen okunuyor");
        Check(!GameCompress.IsRecorded(g), "geri alınca kayıt silindi");
        Journal.Revert(new List<ChangeRecord> { new() { Type = "wof", Name = Path.Combine(dir, "silinmis oyun") } });
        Check(true, "kaldırılmış oyunu geri almak hata vermiyor");
        File.SetAttributes(Path.Combine(g, "data", "b.txt"), FileAttributes.Normal);
    }

    static void GpuPrefTests()
    {
        Check(GameGpu.Merge(null) == "GpuPreference=2;", "boş değere GpuPreference=2 yazılır");
        Check(GameGpu.Merge("GpuPreference=1;") == "GpuPreference=2;", "zayıf kart tercihi güçlüye çevrilir");
        Check(GameGpu.Merge("SwapEffectUpgradeEnable=1;GpuPreference=1;") == "GpuPreference=2;SwapEffectUpgradeEnable=1;", "diğer ayarlar korunur");
        Check(GameGpu.Parse("GpuPreference=1;") == GpuPref.PowerSaving && GameGpu.Parse("SwapEffectUpgradeEnable=1;GpuPreference=2;") == GpuPref.HighPerformance
              && GameGpu.Parse("") == GpuPref.Unset && GameGpu.Parse("GpuPreference=0;") == GpuPref.Unset, "tercih okunur");
    }

    static void NewFeatureTests()
    {
        // Pencereli oyun ayarı: diğer DirectX ayarları korunur
        Check(WindowedOptTweak.SetFlag(null, "SwapEffectUpgradeEnable", "1") == "SwapEffectUpgradeEnable=1;", "pencereli ayar boş değere yazılır");
        Check(WindowedOptTweak.SetFlag("VRROptimizeEnable=0;SwapEffectUpgradeEnable=0;", "SwapEffectUpgradeEnable", "1") == "VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;", "pencereli ayar diğerlerini korur");
        Check(WindowedOptTweak.Flag("AutoHDREnable=1;SwapEffectUpgradeEnable=1;", "SwapEffectUpgradeEnable") == "1"
              && WindowedOptTweak.Flag("AutoHDREnable=1;", "SwapEffectUpgradeEnable") == null, "pencereli ayar okunur");

        // Güncelleme: yalnız izinli HTTPS adresi, geçerli SHA-256 ve makul boyut
        var sha = new string('a', 64);
        UpdateInfo U(string url, string h = "", long size = 60_000_000) => new("9.9.9", url, h.Length == 0 ? sha : h, size, null);
        Check(Updater.IsValid(U("https://github.com/siiplasplas/FrameProof/releases/download/v9.9.9/FrameProof.exe")), "güncelleme: GitHub adresi kabul");
        Check(!Updater.IsValid(U("http://github.com/x/FrameProof.exe")), "güncelleme: HTTP reddedilir");
        Check(!Updater.IsValid(U("https://evil.example.com/FrameProof.exe")), "güncelleme: yabancı sunucu reddedilir");
        Check(!Updater.IsValid(U("https://github.com.evil.example/FrameProof.exe")), "güncelleme: benzer adlı sunucu reddedilir");
        Check(!Updater.IsValid(U("https://github.com/x/FrameProof.exe", "xyz")), "güncelleme: bozuk SHA reddedilir");
        Check(!Updater.IsValid(U("https://github.com/x/FrameProof.exe", size: 100)), "güncelleme: küçük boyut reddedilir");
        Check(new UpdateInfo("v0.10.0", "", "", 0, null).Parsed > new Version(0, 9, 5), "sürüm karşılaştırma (0.10 > 0.9)");

        // BIOS rehberi: yalnız sorunlu bulgulardan, önce XMP
        var rep = new FrameProof.Core.Scan.ScanReport(DateTime.Now, new()
        {
            new("secureboot", "BIOS", "", "", FrameProof.Core.Scan.Severity.Info, Action: "bios"),
            new("rebar", "", "", "", FrameProof.Core.Scan.Severity.Warn, Action: "bios"),
            new("rebar", "", "", "", FrameProof.Core.Scan.Severity.Info),
            new("xmp", "", "", "", FrameProof.Core.Scan.Severity.Bad, Action: "bios"),
            new("vbs", "", "", "", FrameProof.Core.Scan.Severity.Warn),
        });
        var g = FrameProof.Core.Scan.BiosGuide.Build(rep);
        Check(string.Join(",", g.Steps.Select(x => x.FindingId)) == "xmp,rebar,secureboot", "BIOS rehberi sırası ve içeriği");
        var good = new FrameProof.Core.Scan.ScanReport(DateTime.Now, new() { new("xmp", "", "", "", FrameProof.Core.Scan.Severity.Good) });
        Check(FrameProof.Core.Scan.BiosGuide.Build(good).Steps.Count == 0, "BIOS rehberi: sorun yoksa boş");
        Check(Device.Short("ASUSTeK COMPUTER INC.") == "ASUS" && Device.Short("Micro-Star International Co., Ltd.") == "MSI"
              && Device.Short("Gigabyte Technology Co., Ltd.") == "Gigabyte" && Device.Short("LENOVO") == "Lenovo", "üretici adı tanınır");
    }

    static void StatsTests(string dir)
    {
        Directory.CreateDirectory(dir);
        var csv = Path.Combine(dir, "sentetik.csv");
        var sb = new StringBuilder("Application,ProcessID,SwapChainAddress,Runtime,MsBetweenPresents,MsGPUBusy\n");
        var rnd = new Random(1);
        // Asıl oyun: 1000 kare, 10 ms (100 FPS), her 100 karede bir 40 ms takılma
        for (int i = 0; i < 1000; i++)
        {
            double ft = i % 100 == 99 ? 40 : 10;
            sb.Append(CultureInfo.InvariantCulture, $"oyun.exe,1,0xAAA,DXGI,{ft:F3},{ft * 0.95:F3}\n");
        }
        // Overlay: az kare, farklı swapchain (yok sayılmalı)
        for (int i = 0; i < 50; i++) sb.Append("oyun.exe,1,0xBBB,DXGI,33.300,NA\n");
        File.WriteAllText(csv, sb.ToString());

        var p = FrameCsv.Parse(csv);
        Check(p.FrameMs.Count == 1000, "en çok kare çizen swapchain seçildi");
        var r = Stats.Summarize(p, "test");
        double expectedAvg = 1000.0 * 1000 / (990 * 10 + 10 * 40);
        Check(Math.Abs(r.AvgFps - expectedAvg) < 0.01, $"ortalama FPS zaman ağırlıklı ({r.AvgFps:F2} ≈ {expectedAvg:F2})");
        Check(Math.Abs(r.StutterPct - 1.0) < 0.001, $"takılma oranı %1 ({r.StutterPct:F2})");
        Check(Math.Abs(r.Low1Fps - 25) < 0.01, $"1% low takılmaları yansıtıyor ({r.Low1Fps:F1} ≈ 25)");
        Check(r.GpuBusyRatio is > 0.9, "GPU-bound tespit edildi");

        // v2 sütun adları
        var csv2 = Path.Combine(dir, "v2.csv");
        var sb2 = new StringBuilder("Application,ProcessID,SwapChain,FrameTime,GPUBusy\n");
        for (int i = 0; i < 100; i++) sb2.Append("oyun.exe,1,0x1,8.000,4.000\n");
        File.WriteAllText(csv2, sb2.ToString());
        var r2 = Stats.Summarize(FrameCsv.Parse(csv2), "v2");
        Check(Math.Abs(r2.AvgFps - 125) < 0.01 && r2.GpuBusyRatio is < 0.75, "PresentMon 2.x sütun adları + CPU-bound");

        // Welch
        var same = Compare.Welch("fps", new[] { 100.0, 101, 99, 100 }, new[] { 100.5, 99.5, 100, 101 });
        Check(same.Verdict == Verdict.NoDifference, "gürültü 'fark yok' çıkıyor");
        var better = Compare.Welch("fps", new[] { 100.0, 101, 99, 100 }, new[] { 110.0, 111, 109, 110 });
        Check(better.Verdict == Verdict.Better && better.CiLowPct > 0, $"gerçek artış 'iyileşme' ({better.Text})");
        var few = Compare.Welch("fps", new[] { 100.0, 101 }, new[] { 120.0, 121 });
        Check(few.Verdict == Verdict.NotEnoughData, "2 ölçümle karar verilmiyor");
        var stutter = Compare.Welch("takılma", new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 0 }, false);
        Check(stutter.Verdict == Verdict.NoDifference && !double.IsNaN(stutter.DiffPct), "sıfır takılmada sayı patlamıyor");
    }
}
