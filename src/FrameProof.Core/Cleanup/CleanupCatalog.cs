using FrameProof.Core.Util;
using Microsoft.Win32;

namespace FrameProof.Core.Cleanup;

/// <summary>
/// Temizlenecek yerlerin listesi. Bilinçli olarak YOK:
/// - Prefetch (silmek açılışları yavaşlatır, efsane)
/// - DirectX / NVIDIA / AMD shader önbellekleri (silinirse oyunlar yeniden derler ve takılır)
/// - İndirilenler, Masaüstü, Belgeler gibi kullanıcı klasörleri
/// - Tarayıcı çerezleri, geçmişi, şifreleri (sadece önbellek klasörleri, isteğe bağlı)
/// </summary>
public static class CleanupCatalog
{
    private static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string ProgData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string SysRoot => Path.GetPathRoot(Environment.SystemDirectory)!;

    private static SpecGroup G(params FolderSpec[] specs) => new("", Array.Empty<string>(), () => specs);

    public static IReadOnlyList<CleanupTarget> Build() => new CleanupTarget[]
    {
        new FolderTarget
        {
            Id = "user_temp", Name = "Geçici dosyalar (kullanıcı)",
            Description = "%TEMP% klasörü. Sadece 1 günden eski dosyalar; kullanımdakiler atlanır.",
            DefaultOn = true,
            Groups = new[] { G(new FolderSpec(Path.GetTempPath(), MinAgeDays: 1)) },
        },
        new FolderTarget
        {
            Id = "win_temp", Name = "Geçici dosyalar (Windows)",
            Description = @"C:\Windows\Temp. Sadece 1 günden eski dosyalar.",
            DefaultOn = true, NeedsAdmin = true,
            Groups = new[] { G(new FolderSpec(Path.Combine(Win, "Temp"), MinAgeDays: 1)) },
        },
        new FolderTarget
        {
            Id = "wu_cache", Name = "Windows Update indirme önbelleği",
            Description = "Kurulmuş güncellemelerin indirme dosyaları. Gerekirse Windows yeniden indirir.",
            DefaultOn = true, NeedsAdmin = true,
            Groups = new[] { G(new FolderSpec(Path.Combine(Win, @"SoftwareDistribution\Download"))) },
            Before = () =>
            {
                Shell.Run(Shell.Sys("net.exe"), "stop wuauserv /y", 120_000);
                Shell.Run(Shell.Sys("net.exe"), "stop bits /y", 120_000);
            },
            After = () =>
            {
                Shell.Run(Shell.Sys("net.exe"), "start bits", 60_000);
                Shell.Run(Shell.Sys("net.exe"), "start wuauserv", 60_000);
            },
        },
        new FolderTarget
        {
            Id = "delivery_opt", Name = "Teslim İyileştirme önbelleği",
            Description = "Windows'un güncellemeleri başka PC'lerle paylaşmak için tuttuğu dosyalar.",
            DefaultOn = true, NeedsAdmin = true,
            Groups = new[] { G(
                new FolderSpec(Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache")),
                new FolderSpec(Path.Combine(Win, @"SoftwareDistribution\DeliveryOptimization"))) },
            Before = () => Shell.Run(Shell.Sys("net.exe"), "stop dosvc /y", 60_000),
            After = () => Shell.Run(Shell.Sys("net.exe"), "start dosvc", 60_000),
        },
        new FolderTarget
        {
            Id = "crash_dumps", Name = "Çökme dökümleri",
            Description = "Mavi ekran ve uygulama çökmesi döküm dosyaları (.dmp).",
            Warning = "Mavi ekran sorunu yaşıyorsan önce bu dosyaları incelet.",
            DefaultOn = true, NeedsAdmin = true,
            Groups = new[] { G(
                new FolderSpec(Path.Combine(Win, "Minidump"), "*.dmp"),
                new FolderSpec(Win, "MEMORY.DMP", Recursive: false),
                new FolderSpec(Path.Combine(Local, "CrashDumps"))) },
        },
        new FolderTarget
        {
            Id = "wer", Name = "Hata raporları",
            Description = "Windows Hata Bildirimi'nin biriktirdiği rapor dosyaları.",
            DefaultOn = true,
            Groups = new[] { G(
                new FolderSpec(Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportArchive")),
                new FolderSpec(Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportQueue")),
                new FolderSpec(Path.Combine(ProgData, @"Microsoft\Windows\WER\Temp")),
                new FolderSpec(Path.Combine(Local, @"Microsoft\Windows\WER"))) },
        },
        new FolderTarget
        {
            Id = "win_logs", Name = "Eski Windows günlükleri",
            Description = "CBS ve DISM günlükleri. Sadece 7 günden eskiler.",
            DefaultOn = true, NeedsAdmin = true,
            Groups = new[] { G(
                new FolderSpec(Path.Combine(Win, @"Logs\CBS"), MinAgeDays: 7),
                new FolderSpec(Path.Combine(Win, @"Logs\DISM"), MinAgeDays: 7)) },
        },
        new FolderTarget
        {
            Id = "thumbs", Name = "Küçük resim önbelleği",
            Description = "Dosya Gezgini'nin resim önizlemeleri. Gerektikçe yeniden oluşur; kullanımdakiler atlanır.",
            DefaultOn = true,
            Groups = new[] { G(new FolderSpec(Path.Combine(Local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db", Recursive: false)) },
        },
        new FolderTarget
        {
            Id = "gpu_installers", Name = "Ekran kartı sürücü kurulum artıkları",
            Description = @"C:\NVIDIA ve C:\AMD klasörlerine açılmış eski kurulum dosyaları.",
            DefaultOn = true, NeedsAdmin = true,
            Groups = new[] { G(
                new FolderSpec(Path.Combine(SysRoot, "NVIDIA"), MinAgeDays: 1),
                new FolderSpec(Path.Combine(SysRoot, "AMD"), MinAgeDays: 1),
                new FolderSpec(Path.Combine(ProgData, @"NVIDIA Corporation\Downloader"), MinAgeDays: 1)) },
        },
        new RecycleBinTarget
        {
            Id = "recycle_bin", Name = "Geri Dönüşüm Kutusu",
            Description = "Çöp kutusundaki dosyalar.",
            Warning = "Kalıcıdır: buradaki dosyalar geri getirilemez.",
        },
        new FolderTarget
        {
            Id = "browser_cache", Name = "Tarayıcı önbellekleri",
            Description = "Chrome, Edge, Brave, Opera/Opera GX, Vivaldi ve Firefox'un sadece önbellek klasörleri. Çerezler, geçmiş ve şifreler silinmez.",
            Warning = "Siteler ilk açılışta biraz yavaş yüklenebilir. Açık tarayıcılar atlanır.",
            Groups = new[]
            {
                Chromium("Chrome", "chrome", Path.Combine(Local, @"Google\Chrome\User Data")),
                Chromium("Edge", "msedge", Path.Combine(Local, @"Microsoft\Edge\User Data")),
                Chromium("Brave", "brave", Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data")),
                Chromium("Vivaldi", "vivaldi", Path.Combine(Local, @"Vivaldi\User Data")),
                Chromium("Opera", "opera", Path.Combine(Local, @"Opera Software\Opera Stable")),
                Chromium("Opera GX", "opera", Path.Combine(Local, @"Opera Software\Opera GX Stable")),
                new SpecGroup("Firefox", new[] { "firefox" }, () => SubDirs(Path.Combine(Local, @"Mozilla\Firefox\Profiles"))
                    .Select(p => new FolderSpec(Path.Combine(p, "cache2")))),
            },
        },
        new FolderTarget
        {
            Id = "app_cache", Name = "Discord ve Steam web önbelleği",
            Description = "Discord'un ve Steam arayüzünün önbellek klasörleri. Oyun dosyalarına dokunulmaz.",
            Warning = "Açık uygulamalar atlanır.",
            Groups = new[]
            {
                new SpecGroup("Discord", new[] { "Discord", "DiscordPTB", "DiscordCanary" }, () =>
                    new[] { "discord", "discordptb", "discordcanary" }.SelectMany(d => CacheDirs(Path.Combine(Roaming, d)))),
                new SpecGroup("Steam", new[] { "steam", "steamwebhelper" }, () => new[] { new FolderSpec(Path.Combine(Local, @"Steam\htmlcache")) }),
            },
        },
        new PreviousWindowsTarget
        {
            Id = "windows_old", Name = "Eski Windows kurulumu (Windows.old)",
            Description = "Büyük güncellemeden kalan eski Windows. Windows'un kendi Disk Temizleme aracıyla silinir.",
            Warning = "Silinince önceki Windows sürümüne geri dönemezsin.",
            NeedsAdmin = true, Slow = true,
        },
        new ComponentStoreTarget
        {
            Id = "component_store", Name = "Windows bileşen deposu (WinSxS)",
            Description = "DISM ile eski güncelleme bileşenlerini temizler.",
            Warning = "5–15 dakika sürebilir ve geri alınamaz (eski güncellemeler kaldırılamaz hale gelir).",
            NeedsAdmin = true, Slow = true,
        },
    };

    private static IEnumerable<string> SubDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<FolderSpec> CacheDirs(string baseDir) =>
        new[] { "Cache", "Code Cache", "GPUCache" }
            .Select(n => Path.Combine(baseDir, n))
            .Where(Directory.Exists)
            .Select(p => new FolderSpec(p));

    private static SpecGroup Chromium(string label, string process, string userData) =>
        new(label, new[] { process }, () =>
        {
            var bases = new List<string> { userData };
            bases.AddRange(SubDirs(userData).Where(d =>
            {
                var n = Path.GetFileName(d);
                return n == "Default" || n.StartsWith("Profile ", StringComparison.Ordinal);
            }));
            return bases.SelectMany(CacheDirs);
        });
}
