using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrameProof.Core.Tweaks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace FrameProof.Core.Games;

public sealed record InstalledGame(string Name, string Folder, string Store);

/// <summary>Bir klasörün gerçek boyutu ve diskte kapladığı yer (sıkıştırılmış dosyalar daha az yer kaplar).</summary>
public sealed record FolderSize(long Bytes, long OnDisk, int Files)
{
    public long Saved => Math.Max(0, Bytes - OnDisk);
}

public sealed record CompressResult(long Before, long After, int Compressed, int Skipped, int Failed, bool Cancelled)
{
    public long Saved => Math.Max(0, Before - After);
}

/// <summary>Steam ve Epic'te kurulu oyunları kurulum kayıtlarından bulur (exe yollarından tahmin etmez).</summary>
public static class GameLibrary
{
    public static List<InstalledGame> List()
    {
        var all = new List<InstalledGame>();
        try { all.AddRange(Steam()); } catch { }
        try { all.AddRange(Epic()); } catch { }
        return all.Where(g => Directory.Exists(g.Folder))
                  .GroupBy(g => Path.GetFullPath(g.Folder).TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
                  .Select(g => g.First())
                  .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                  .ToList();
    }

    private static readonly Regex Kv = new("\"(\\w+)\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    private static IEnumerable<(string Key, string Value)> Pairs(string text) =>
        Kv.Matches(text).Select(m => (m.Groups[1].Value, m.Groups[2].Value.Replace("\\\\", "\\")));

    private static IEnumerable<InstalledGame> Steam()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var steam = (k?.GetValue("SteamPath") as string)?.Replace('/', '\\');
        if (string.IsNullOrEmpty(steam)) yield break;
        var libs = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            libs.AddRange(Pairs(File.ReadAllText(vdf)).Where(p => p.Key.Equals("path", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value));
        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string? name = null, dir = null;
                try
                {
                    foreach (var (key, val) in Pairs(File.ReadAllText(acf)))
                    {
                        if (name == null && key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = val;
                        if (dir == null && key.Equals("installdir", StringComparison.OrdinalIgnoreCase)) dir = val;
                    }
                }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(dir) || dir.Contains("..")) continue;
                // Steam'in kendi bileşenleri oyun değil.
                if (name is null || name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Steam Linux", StringComparison.OrdinalIgnoreCase)) continue;
                yield return new InstalledGame(name, Path.Combine(apps, "common", dir), "Steam");
            }
        }
    }

    private static IEnumerable<InstalledGame> Epic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.item"))
        {
            string? name = null, loc = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var r = doc.RootElement;
                if (r.TryGetProperty("DisplayName", out var n)) name = n.GetString();
                if (r.TryGetProperty("InstallLocation", out var l)) loc = l.GetString();
            }
            catch { continue; }
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(loc))
                yield return new InstalledGame(name, loc, "Epic");
        }
    }
}

/// <summary>
/// Oyun klasörünü Windows'un dosya sıkıştırmasıyla (WOF, "compact /exe" ile aynı) küçültür. Dosyalar silinmez,
/// oyun onları her zamanki gibi okur; Windows açarken çözer. Steam bir dosyayı güncellerse o dosya sıkıştırılmamış
/// olarak yeniden yazılır (tekrar sıkıştırılabilir). Geri alma: <see cref="Decompress"/>.
/// exe/dll/sys dosyalarına ve anti-cheat klasörlerine dokunulmaz.
/// </summary>
public static class GameCompress
{
    /// <summary>XPRESS16K: LZX'ten biraz az sıkıştırır ama açması çok daha hafif; oyun yüklemesi için daha güvenli.</summary>
    public const uint Algorithm = 3; // 0 XPRESS4K, 1 LZX, 2 XPRESS8K, 3 XPRESS16K
    public const string AlgorithmName = "XPRESS16K";
    private const long MinFile = 16 * 1024;

    private static readonly string[] SkipExt = { ".exe", ".dll", ".sys", ".bat", ".cmd" };
    private static readonly string[] SkipDirs = { "easyanticheat", "battleye", "anticheat", "eac", "vanguard", "punkbuster" };

    // ---- Güvenlik kontrolleri ----

    /// <summary>Sıkıştırılmasına izin verilen bir oyun klasörü mü? (sürücü kökü, Windows, kullanıcı klasörü vb. değil)</summary>
    public static bool IsSafeFolder(string folder)
    {
        string full;
        try { full = Path.GetFullPath(folder).TrimEnd('\\'); } catch { return false; }
        var root = Path.GetPathRoot(full)?.TrimEnd('\\') ?? "";
        if (full.Length <= root.Length) return false;
        // En az iki seviye derinlik: "D:\Oyun" bile kütüphane kökü olabilir.
        if (full.Count(c => c == '\\') < 2) return false;
        string[] forbidden =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        foreach (var f in forbidden.Where(f => !string.IsNullOrEmpty(f)))
            if (full.Equals(f.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
        if (full.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) return false;
        if (full.EndsWith(@"\steamapps\common", StringComparison.OrdinalIgnoreCase) || full.EndsWith(@"\steamapps", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Klasörden çalışan bir program var mı? (oyun açıkken dosyaları kilitli olur)</summary>
    public static string? RunningFrom(string folder)
    {
        var prefix = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, ExecutablePath FROM Win32_Process");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    var p = o["ExecutablePath"] as string;
                    if (p != null && p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return o["Name"] as string ?? p;
                }
            }
        }
        catch { }
        return null;
    }

    // ---- Boyut ----

    public static FolderSize Measure(string folder, CancellationToken ct = default)
    {
        long bytes = 0, disk = 0; int files = 0;
        foreach (var f in Files(folder, all: true))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var len = new FileInfo(f).Length;
                bytes += len;
                disk += OnDisk(f, len);
                files++;
            }
            catch { }
        }
        return new FolderSize(bytes, disk, files);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string name, out uint high);

    public static long OnDisk(string file, long fallback)
    {
        // "\\?\" önekiyle Windows yolu düzeltmez: "/" ve ".." içeren yolu önce normalleştir.
        var full = file.StartsWith(@"\\?\") ? file : @"\\?\" + Path.GetFullPath(file);
        uint lo = GetCompressedFileSizeW(full, out var hi);
        if (lo == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0) return fallback;
        return ((long)hi << 32) | lo;
    }

    /// <summary>Sembolik bağlantı/junction takip etmeden dosyaları dolaşır. all=false ise atlanacakları vermez.</summary>
    private static IEnumerable<string> Files(string folder, bool all)
    {
        var opts = new System.IO.EnumerationOptions
        {
            RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var stack = new Stack<string>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            IEnumerable<string> subs, files;
            try { subs = Directory.EnumerateDirectories(d, "*", opts).ToList(); files = Directory.EnumerateFiles(d, "*", opts).ToList(); }
            catch { continue; }
            foreach (var s in subs)
                if (all || !SkipDirs.Contains(Path.GetFileName(s).ToLowerInvariant())) stack.Push(s);
            foreach (var f in files) yield return f;
        }
    }

    private static bool ShouldSkip(string f, long len) =>
        len < MinFile || SkipExt.Contains(Path.GetExtension(f).ToLowerInvariant());

    // ---- WOF ----

    private const uint FSCTL_SET_EXTERNAL_BACKING = 0x9030C;
    private const uint FSCTL_GET_EXTERNAL_BACKING = 0x90310;
    private const uint FSCTL_DELETE_EXTERNAL_BACKING = 0x90314;
    private const int ERROR_COMPRESSION_NOT_BENEFICIAL = 344;
    private const int ERROR_OBJECT_NOT_EXTERNALLY_BACKED = 342;

    [StructLayout(LayoutKind.Sequential)]
    private struct WofFileInfo
    {
        public uint WofVersion;   // WOF_EXTERNAL_INFO.Version = 1
        public uint Provider;     // WOF_PROVIDER_FILE = 2
        public uint FileVersion;  // FILE_PROVIDER_EXTERNAL_INFO_V1.Version = 1
        public uint Algorithm;
        public uint Flags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref WofFileInfo inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, ref WofFileInfo outBuf, int outSize, out int returned, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr ov);

    /// <summary>0 = sıkıştırıldı, 1 = fayda yok (atlandı); hata olursa Win32Exception.</summary>
    private static int CompressFile(string f)
    {
        var attr = File.GetAttributes(f);
        bool ro = attr.HasFlag(FileAttributes.ReadOnly);
        if (ro) File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
        try
        {
            using var h = File.OpenHandle(f, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var info = new WofFileInfo { WofVersion = 1, Provider = 2, FileVersion = 1, Algorithm = Algorithm };
            if (DeviceIoControl(h, FSCTL_SET_EXTERNAL_BACKING, ref info, Marshal.SizeOf<WofFileInfo>(), IntPtr.Zero, 0, out _, IntPtr.Zero)) return 0;
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_COMPRESSION_NOT_BENEFICIAL) return 1;
            throw new Win32Exception(err);
        }
        finally { if (ro) File.SetAttributes(f, attr); }
    }

    public static bool IsCompressed(string f)
    {
        try
        {
            using var h = File.OpenHandle(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var info = new WofFileInfo();
            return DeviceIoControl(h, FSCTL_GET_EXTERNAL_BACKING, IntPtr.Zero, 0, ref info, Marshal.SizeOf<WofFileInfo>(), out _, IntPtr.Zero)
                   && info.Provider == 2;
        }
        catch { return false; }
    }

    private static void DecompressFile(string f)
    {
        var attr = File.GetAttributes(f);
        bool ro = attr.HasFlag(FileAttributes.ReadOnly);
        if (ro) File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
        try
        {
            using var h = File.OpenHandle(f, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            if (DeviceIoControl(h, FSCTL_DELETE_EXTERNAL_BACKING, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero)) return;
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_OBJECT_NOT_EXTERNALLY_BACKED) return;
            throw new Win32Exception(err);
        }
        finally { if (ro) File.SetAttributes(f, attr); }
    }

    // ---- Ana işlemler ----

    /// <summary>
    /// Klasörü sıkıştırır ve günlüğe "Geri al" kaydı ekler (kısmen yapılsa bile). progress: (işlenen bayt, toplam bayt).
    /// </summary>
    public static CompressResult Compress(InstalledGame game, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        if (!IsSafeFolder(game.Folder)) throw new InvalidOperationException("Bu klasör sıkıştırma için güvenli görünmüyor: " + game.Folder);
        var running = RunningFrom(game.Folder);
        if (running != null) throw new InvalidOperationException($"{game.Name} şu an açık ({running}). Önce oyunu kapat.");

        var list = Files(game.Folder, all: false).Select(f => { try { return (f, new FileInfo(f).Length); } catch { return (f, -1L); } })
                                                  .Where(x => x.Item2 >= 0).ToList();
        long total = list.Sum(x => x.Item2), done = 0, before = 0, after = 0;
        int ok = 0, skipped = 0, failed = 0;
        bool cancelled = false, journaled = false;
        foreach (var (f, len) in list)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            long b = OnDisk(f, len);
            before += b;
            if (ShouldSkip(f, len) || b < len * 0.97) { skipped++; after += b; } // zaten sıkışık ya da değmez
            else
            {
                try
                {
                    if (!journaled) { Record(game); journaled = true; } // ilk dosyadan önce: yarıda kesilse de geri alınabilir
                    if (CompressFile(f) == 0) ok++; else skipped++;
                }
                catch { failed++; }
                after += OnDisk(f, len);
            }
            done += len;
            progress?.Report((done, total));
        }
        return new CompressResult(before, after, ok, skipped, failed, cancelled);
    }

    private static void Record(InstalledGame game)
    {
        var full = Path.GetFullPath(game.Folder).TrimEnd('\\');
        // Aynı oyun için zaten kayıt varsa yenisini ekleme (yeniden sıkıştırma).
        if (Journal.Load().Any(e => e.Changes.Any(c => c.Type == "wof" && string.Equals(c.Name, full, StringComparison.OrdinalIgnoreCase)))) return;
        Journal.Add(new JournalEntry
        {
            TweakId = "wof:" + full,
            Title = $"Oyun sıkıştırıldı: {game.Name}",
            Changes = { new ChangeRecord { Type = "wof", Name = full, Data = AlgorithmName } },
        });
    }

    /// <summary>Daha önce sıkıştırılmış oyunun klasörü (günlükte kayıt varsa).</summary>
    public static bool IsRecorded(string folder)
    {
        var full = Path.GetFullPath(folder).TrimEnd('\\');
        return Journal.Load().Any(e => e.Changes.Any(c => c.Type == "wof" && string.Equals(c.Name, full, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Sıkıştırmayı geri alır. Klasör yoksa (oyun kaldırıldı) sessizce geçer. Diskte yer yoksa açıklayıcı hata verir.
    /// </summary>
    public static void Decompress(string folder)
    {
        if (!Directory.Exists(folder)) return;
        var running = RunningFrom(folder);
        if (running != null) throw new InvalidOperationException($"Klasördeki program açık ({running}). Önce kapat.");
        var compressed = new List<(string F, long Need)>();
        foreach (var f in Files(folder, all: true))
        {
            try
            {
                var len = new FileInfo(f).Length;
                var disk = OnDisk(f, len);
                if (disk < len && IsCompressed(f)) compressed.Add((f, len - disk));
            }
            catch { }
        }
        long need = compressed.Sum(x => x.Need);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!);
        if (need + 512L * 1024 * 1024 > drive.AvailableFreeSpace)
            throw new InvalidOperationException($"Sıkıştırmayı geri almak için {drive.Name} sürücüsünde {need / 1024.0 / 1024 / 1024:0.0} GB boş yer gerekiyor " +
                                                $"(şu an {drive.AvailableFreeSpace / 1024.0 / 1024 / 1024:0.0} GB). Önce yer aç.");
        int failed = 0;
        foreach (var (f, _) in compressed)
        {
            try { DecompressFile(f); } catch { failed++; }
        }
        if (failed > 0) throw new InvalidOperationException($"{failed} dosya açılamadı (kullanımda olabilir). Tekrar dene.");
    }
}
