using System.Diagnostics;

namespace FrameProof.Core.Processes;

public sealed record AppGroup(string Key, string Name, int Count, long RamBytes, bool Closable, string? Note);

/// <summary>
/// Oyundan önce arka planda RAM/işlemci yiyen programları listeler. Yalnız "kapatılması güvenli" listesindekiler
/// kapatılabilir; başlatıcılar, anti-cheat, ses/ekran kartı/fan araçları, fare yazılımları, Discord ve sistem
/// işlemleri asla kapatılmaz. Kapatma geri alınamaz (kaydedilmemiş işler kaybolabilir), program sonra yeniden açılabilir.
/// </summary>
public static class BackgroundApps
{
    /// <summary>süreç adı (küçük harf, uzantısız) → görünen ad</summary>
    private static readonly Dictionary<string, string> Safe = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Google Chrome",
        ["msedge"] = "Microsoft Edge",
        ["firefox"] = "Firefox",
        ["brave"] = "Brave",
        ["opera"] = "Opera",
        ["opera_gx"] = "Opera GX",
        ["vivaldi"] = "Vivaldi",
        ["onedrive"] = "OneDrive",
        ["dropbox"] = "Dropbox",
        ["googledrivefs"] = "Google Drive",
        ["teams"] = "Microsoft Teams",
        ["ms-teams"] = "Microsoft Teams",
        ["slack"] = "Slack",
        ["zoom"] = "Zoom",
        ["skype"] = "Skype",
        ["telegram"] = "Telegram",
        ["whatsapp"] = "WhatsApp",
        ["spotify"] = "Spotify",
        ["creative cloud"] = "Adobe Creative Cloud",
        ["adobe desktop service"] = "Adobe Creative Cloud",
        ["cclibrary"] = "Adobe Creative Cloud",
        ["manycam"] = "ManyCam",
        ["phoneexperiencehost"] = "Telefon Bağlantısı",
        ["widgets"] = "Pencere öğeleri",
    };

    /// <summary>Asla kapatılmaz, listede bile gösterilmez.</summary>
    private static readonly HashSet<string> Never = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper", "epicgameslauncher", "riotclientservices", "riotclientux", "vgc", "vgtray",
        "easyanticheat", "beservice", "battleye", "faceit", "faceitclient", "battle.net", "eadesktop", "ubisoftconnect",
        "upc", "gog galaxy", "galaxyclient", "discord", "nvcontainer", "nvidia share", "nvidia overlay", "nvdisplay.container",
        "radeonsoftware", "amdrsserv", "armourycrate", "armourycrate.service", "asus_framework", "lightingservice",
        "ghub", "lghub", "lghub_agent", "razer synapse", "razer synapse 3", "icue", "steelseriesgg", "anydesk",
        "claude", "frameproof", "explorer", "dwm", "svchost", "csrss", "audiodg", "msmpeng", "searchhost",
        "startmenuexperiencehost", "shellexperiencehost", "textinputhost", "runtimebroker", "ctfmon", "sihost",
        "obs64", "obs32", "msiafterburner", "rtss", "code", "devenv", "rider64", "unity", "blender",
    };

    public static List<AppGroup> List()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var groups = new Dictionary<string, (string Name, int Count, long Ram, bool Safe)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.SessionId != session || p.Id == Environment.ProcessId) continue;
                var n = p.ProcessName;
                if (Never.Contains(n)) continue;
                bool safe = Safe.TryGetValue(n, out var disp);
                string key = safe ? disp! : n;
                string name = safe ? disp! : Friendly(p) ?? n;
                long ram = p.WorkingSet64;
                if (groups.TryGetValue(key, out var g)) groups[key] = (g.Name, g.Count + 1, g.Ram + ram, g.Safe);
                else groups[key] = (name, 1, ram, safe);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return groups
            .Select(kv => new AppGroup(kv.Key, kv.Value.Name, kv.Value.Count, kv.Value.Ram, kv.Value.Safe,
                kv.Value.Safe ? null : "Sistem ya da tanımadığımız bir program; güvenli olsun diye kapatmıyoruz."))
            .OrderByDescending(g => g.RamBytes)
            .ToList();
    }

    /// <summary>Discord'un (tüm alt işlemleriyle) kullandığı RAM. Discord asla kapatılmaz; yalnız bilgi için.</summary>
    public static long DiscordRam()
    {
        var session = Process.GetCurrentProcess().SessionId;
        long sum = 0;
        foreach (var n in new[] { "discord", "discordptb", "discordcanary" })
            foreach (var p in Process.GetProcessesByName(n))
            {
                try { if (p.SessionId == session) sum += p.WorkingSet64; } catch { }
                finally { p.Dispose(); }
            }
        return sum;
    }

    private static string? Friendly(Process p)
    {
        try
        {
            var d = p.MainModule?.FileVersionInfo.FileDescription;
            return string.IsNullOrWhiteSpace(d) || d.Length > 40 ? null : d.Trim();
        }
        catch { return null; }
    }

    /// <summary>
    /// Önce pencereyi kibarca kapatır (program kendi kapanışını yapar), ~4 sn sonra hâlâ açık kalanları sonlandırır.
    /// Yalnız güvenli listedeki programlar. Kaç işlemin kapandığını döner.
    /// </summary>
    public static async Task<int> CloseAsync(string groupKey)
    {
        var names = Safe.Where(kv => kv.Value == groupKey).Select(kv => kv.Key).ToList();
        if (names.Count == 0) throw new InvalidOperationException("Bu program güvenli kapatma listesinde değil.");
        var session = Process.GetCurrentProcess().SessionId;
        var procs = names.SelectMany(Process.GetProcessesByName).Where(p => { try { return p.SessionId == session; } catch { return false; } }).ToList();
        int total = procs.Count;
        try
        {
            foreach (var p in procs) { try { p.CloseMainWindow(); } catch { } }
            var until = DateTime.Now.AddSeconds(4);
            while (DateTime.Now < until && procs.Any(p => { try { return !p.HasExited; } catch { return false; } }))
                await Task.Delay(250);
            foreach (var p in procs)
            {
                try { if (!p.HasExited) p.Kill(true); } catch { }
            }
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
        return total;
    }
}
