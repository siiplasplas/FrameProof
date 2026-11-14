using System.Diagnostics;
using FrameProof.Core.Tweaks;
using FrameProof.Core.Util;
using Microsoft.Win32;

namespace FrameProof.Core.Games;

/// <summary>Windows Ayarlar > Grafik'teki uygulama başına ekran kartı tercihi.</summary>
public enum GpuPref { Unset = 0, PowerSaving = 1, HighPerformance = 2 }

public sealed record GameExe(string Path, string Name, GpuPref Pref);

/// <summary>
/// Oyunların hangi ekran kartına sabitlendiğini okur ve "güçlü kart"a sabitler.
/// Windows'un kendi ayarıdır (HKCU\Software\Microsoft\DirectX\UserGpuPreferences), yönetici izni gerekmez,
/// oyuna ya da anti-cheat'e dokunmaz. Oyun listesi Windows'un oyun olarak tanıdığı exe'lerden gelir
/// (GameConfigStore), böylece başlatıcı (launcher) yerine oyunun gerçek exe'si sabitlenir.
/// </summary>
public static class GameGpu
{
    public const string PrefKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string StoreKey = @"System\GameConfigStore\Children";

    private static readonly string[] GameDirs =
    {
        @"\steamapps\common\", @"\Epic Games\", @"\Riot Games\", @"\GOG Games\", @"\GOG Galaxy\Games\",
        @"\EA Games\", @"\Electronic Arts\", @"\Ubisoft Game Launcher\games\", @"\XboxGames\", @"\Battle.net\",
        @"\Rockstar Games\", @"\Program Files\WindowsApps\",
    };

    public static bool IsGamePath(string p) => GameDirs.Any(d => p.Contains(d, StringComparison.OrdinalIgnoreCase));

    public static List<GameExe> List()
    {
        var prefs = ReadPrefs();
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1) Windows'un oyun olarak gördüğü exe'ler
        try
        {
            using var store = Registry.CurrentUser.OpenSubKey(StoreKey);
            foreach (var sub in store?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                try
                {
                    using var k = store!.OpenSubKey(sub);
                    if (k?.GetValue("MatchedExeFullPath") is string p && File.Exists(p)) paths.TryAdd(p, p);
                }
                catch { }
            }
        }
        catch { }

        // 2) Tercihi daha önce ayarlanmış, oyun klasöründeki exe'ler
        foreach (var p in prefs.Keys)
            if (IsGamePath(p) && File.Exists(p)) paths.TryAdd(p, p);

        return paths.Keys
            .Select(p => new GameExe(p, NameOf(p), prefs.TryGetValue(p, out var v) ? v : GpuPref.Unset))
            .OrderBy(g => g.Pref == GpuPref.HighPerformance)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static GpuPref Get(string exePath) =>
        ReadPrefs().TryGetValue(exePath, out var v) ? v : GpuPref.Unset;

    private static Dictionary<string, GpuPref> ReadPrefs()
    {
        var d = new Dictionary<string, GpuPref>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(PrefKey);
            foreach (var name in k?.GetValueNames() ?? Array.Empty<string>())
                d[name] = Parse(k!.GetValue(name)?.ToString());
        }
        catch { }
        return d;
    }

    public static GpuPref Parse(string? s)
    {
        foreach (var part in (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("GpuPreference", StringComparison.OrdinalIgnoreCase))
                return kv[1].Trim() switch { "1" => GpuPref.PowerSaving, "2" => GpuPref.HighPerformance, _ => GpuPref.Unset };
        }
        return GpuPref.Unset;
    }

    /// <summary>"SwapEffectUpgradeEnable=1;" gibi diğer ayarları koruyarak GpuPreference=2 yazar.</summary>
    public static string Merge(string? existing)
    {
        var parts = (existing ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0 && !p.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        parts.Insert(0, "GpuPreference=2");
        return string.Join(";", parts) + ";";
    }

    /// <summary>Verilen exe'leri güçlü karta sabitler; eski değerler günlüğe yazılır.</summary>
    public static void PinCore(IEnumerable<string> exePaths, List<ChangeRecord> changes)
    {
        foreach (var p in exePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var cur = Reg.GetString("HKCU", PrefKey, p);
            Journal.RegSet(changes, "HKCU", PrefKey, ExistingName(p) ?? p, Merge(cur), RegistryValueKind.String);
        }
    }

    /// <summary>Tek oyunu (ör. ölçümden sonra) sabitler ve ayrı bir günlük kaydı açar.</summary>
    public static JournalEntry Pin(string exePath)
    {
        var changes = new List<ChangeRecord>();
        PinCore(new[] { exePath }, changes);
        var e = new JournalEntry { TweakId = "gpu_pin_one", Title = $"Ekran kartı: {NameOf(exePath)} güçlü karta sabitlendi", Changes = changes };
        Journal.Add(e);
        return e;
    }

    /// <summary>Kayıt defterindeki değer adının asıl yazımı (büyük/küçük harf farklı olabilir).</summary>
    private static string? ExistingName(string p)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(PrefKey);
            return k?.GetValueNames().FirstOrDefault(n => n.Equals(p, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    public static string NameOf(string exePath)
    {
        // Steam: ...\steamapps\common\<Oyun>\... → klasör adı en okunaklısı
        var i = exePath.IndexOf(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            var rest = exePath[(i + 18)..];
            var dir = rest.Split('\\')[0];
            if (dir.Length > 0) return dir;
        }
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrWhiteSpace(vi.ProductName) && vi.ProductName.Length < 40) return vi.ProductName.Trim();
        }
        catch { }
        return System.IO.Path.GetFileNameWithoutExtension(exePath);
    }
}

/// <summary>Laptoplarda oyunları harici (güçlü) ekran kartına sabitler.</summary>
public sealed class GpuPinTweak : Tweak
{
    public override string Id => "gpu_pin";
    public override string Title => "Oyunları güçlü ekran kartına sabitle";
    public override string WhatItDoes => "Windows'un Grafik ayarında bilgisayarındaki oyunları 'Yüksek performans' (harici kart) olarak işaretler. Windows'un oyun olarak tanıdığı exe'leri kullanır.";
    public override string Effect => "Oyun yanlışlıkla işlemcinin içindeki zayıf karta düşmüşse FPS 2–3 katına çıkabilir. Zaten doğru karttaysa fark yok, zararı da yok.";
    public override bool OneClick => true;

    private static List<GameExe> Targets() => GameGpu.List().Where(g => g.Pref != GpuPref.HighPerformance).ToList();

    public override TweakState GetState()
    {
        if (!Gpus.IsHybrid) return TweakState.NotApplicable;
        var games = GameGpu.List();
        if (games.Count == 0) return TweakState.NotApplicable;
        return games.All(g => g.Pref == GpuPref.HighPerformance) ? TweakState.Applied : TweakState.NotApplied;
    }

    protected override void ApplyCore(List<ChangeRecord> changes) =>
        GameGpu.PinCore(Targets().Select(g => g.Path), changes);
}
