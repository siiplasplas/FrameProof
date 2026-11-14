using FrameProof.Core.Tweaks;
using FrameProof.Core.Util;
using Microsoft.Win32;

namespace FrameProof.Core.Startup;

public sealed record StartupItem(string Name, string Command, string Source, string ApprovedHive, string ApprovedKey, bool Enabled)
{
    public bool NeedsAdmin => ApprovedHive == "HKLM";
}

/// <summary>
/// Başlangıç uygulamaları. Görev Yöneticisi ile aynı yöntemi kullanır (StartupApproved):
/// Run anahtarına dokunmaz, sadece aç/kapat işaretini değiştirir. Her değişiklik günlüğe yazılır.
/// </summary>
public static class StartupManager
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupItem> List()
    {
        var items = new List<StartupItem>();
        AddRun(items, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", "Kullanıcı (Run)", "HKCU", Approved + "Run");
        AddRun(items, "HKLM", @"Software\Microsoft\Windows\CurrentVersion\Run", "Tüm kullanıcılar (Run)", "HKLM", Approved + "Run");
        AddRun(items, "HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Tüm kullanıcılar (32-bit)", "HKLM", Approved + "Run32");
        AddFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Başlangıç klasörü", "HKCU");
        AddFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Ortak başlangıç klasörü", "HKLM");
        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void AddRun(List<StartupItem> items, string hive, string key, string source, string apHive, string apKey)
    {
        try
        {
            using var k = Reg.Root(hive).OpenSubKey(key);
            if (k is null) return;
            foreach (var name in k.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                var cmd = k.GetValue(name)?.ToString() ?? "";
                items.Add(new StartupItem(name, cmd, source, apHive, apKey, IsEnabled(apHive, apKey, name)));
            }
        }
        catch { }
    }

    private static void AddFolder(List<StartupItem> items, string dir, string source, string apHive)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                items.Add(new StartupItem(name, f, source, apHive, Approved + "StartupFolder", IsEnabled(apHive, Approved + "StartupFolder", name)));
            }
        }
        catch { }
    }

    private static bool IsEnabled(string hive, string key, string name)
    {
        // Değer yoksa açık. İlk baytın en düşük biti 1 ise kapalı (Görev Yöneticisi 0x02 = açık, 0x03 = kapalı yazar).
        return Reg.Get(hive, key, name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
    }

    public static JournalEntry SetEnabled(StartupItem item, bool enabled)
    {
        if (item.NeedsAdmin && !Admin.IsElevated)
            throw new UnauthorizedAccessException("Bu başlangıç öğesi için yönetici izni gerekiyor.");
        var data = new byte[12];
        if (enabled)
        {
            data[0] = 0x02;
        }
        else
        {
            data[0] = 0x03;
            BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
        }
        var changes = new List<ChangeRecord>();
        Journal.RegSet(changes, item.ApprovedHive, item.ApprovedKey, item.Name, data, RegistryValueKind.Binary);
        var e = new JournalEntry
        {
            TweakId = "startup",
            Title = $"Başlangıç: {item.Name} {(enabled ? "açıldı" : "kapatıldı")}",
            Changes = changes,
        };
        Journal.Add(e);
        return e;
    }
}
