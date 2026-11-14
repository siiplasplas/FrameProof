using System.Runtime.InteropServices;
using System.Text.Json;
using FrameProof.Core.Util;
using Microsoft.Win32;

namespace FrameProof.Core.Tweaks;

/// <summary>Tek bir geri alınabilir değişiklik. Değişiklik yapılmadan ÖNCE eski değer buraya yazılır.</summary>
public sealed class ChangeRecord
{
    /// <summary>reg | power | display | mouse | task (Görev Zamanlayıcı yolu) | hiber (hazırda bekletme) | wof (oyun klasörü sıkıştırması)</summary>
    public string Type { get; set; } = "";
    public string? Hive { get; set; }
    public string? Key { get; set; }
    public string? Name { get; set; }
    public bool Existed { get; set; }
    public string? Kind { get; set; }
    public string? Data { get; set; }
    /// <summary>Ek bilgi: örn. bizim oluşturduğumuz güç planı GUID'i.</summary>
    public string? Extra { get; set; }
}

public sealed class JournalEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string TweakId { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime AppliedAt { get; set; } = DateTime.Now;
    public bool NeedsReboot { get; set; }
    public List<ChangeRecord> Changes { get; set; } = new();
}

public static class Journal
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static List<JournalEntry> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(AppPaths.JournalFile)) return new();
                return JsonSerializer.Deserialize<List<JournalEntry>>(File.ReadAllText(AppPaths.JournalFile)) ?? new();
            }
            catch { return new(); }
        }
    }

    private static void Save(List<JournalEntry> list)
    {
        lock (Gate)
        {
            var tmp = AppPaths.JournalFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, Opts));
            File.Move(tmp, AppPaths.JournalFile, true);
        }
    }

    public static void Add(JournalEntry e)
    {
        var l = Load();
        l.Add(e);
        Save(l);
    }

    /// <summary>Kaydı geri alır ve günlükten siler. Hata olursa kayıt günlükte kalır.</summary>
    public static void Undo(string entryId)
    {
        var l = Load();
        var e = l.FirstOrDefault(x => x.Id == entryId) ?? throw new InvalidOperationException("Kayıt bulunamadı.");
        Revert(e.Changes);
        l.Remove(e);
        Save(l);
    }

    public static void UndoAll()
    {
        foreach (var e in Load().AsEnumerable().Reverse())
            Undo(e.Id);
    }

    public static void RemoveEntries(Func<JournalEntry, bool> pred)
    {
        var l = Load();
        l.RemoveAll(e => pred(e));
        Save(l);
    }

    /// <summary>Değişiklikleri ters sırayla eski haline döndürür.</summary>
    public static void Revert(IList<ChangeRecord> changes)
    {
        var errors = new List<string>();
        for (int i = changes.Count - 1; i >= 0; i--)
        {
            try { RevertOne(changes[i]); }
            catch (Exception ex) { errors.Add($@"{changes[i].Type} {changes[i].Key}\{changes[i].Name}: {ex.Message}"); }
        }
        if (errors.Count > 0) throw new InvalidOperationException("Bazı değişiklikler geri alınamadı:\n" + string.Join("\n", errors));
    }

    private static void RevertOne(ChangeRecord c)
    {
        switch (c.Type)
        {
            case "reg":
                RegRestore(c);
                break;
            case "power":
                Shell.Run(Shell.Sys("powercfg.exe"), "/setactive " + c.Data);
                if (!string.IsNullOrEmpty(c.Extra)) Shell.Run(Shell.Sys("powercfg.exe"), "/delete " + c.Extra);
                break;
            case "display":
                Displays.SetRefresh(c.Name!, int.Parse(c.Data!));
                break;
            case "mouse":
                var p = c.Data!.Split(',').Select(int.Parse).ToArray();
                MouseAccel.Set(p[0], p[1], p[2]);
                break;
            case "task":
                // Görev bu arada silindiyse (Edge kaldırıldı/yeniden kuruldu) geri alacak bir şey yok; hata sayma.
                TaskSched.SetEnabled(c.Name!, c.Data == "1");
                break;
            case "hiber":
                HibernateTweak.Restore();
                break;
            case "wof":
                // Oyun klasörünün sıkıştırmasını aç. Oyun kaldırıldıysa yapacak bir şey yok.
                FrameProof.Core.Games.GameCompress.Decompress(c.Name!);
                break;
            default:
                throw new InvalidOperationException("Bilinmeyen değişiklik türü: " + c.Type);
        }
    }

    // ---- Kayıt defteri yardımcıları ----

    /// <summary>Eski değeri kaydeder, sonra yenisini yazar.</summary>
    public static void RegSet(List<ChangeRecord> changes, string hive, string key, string name, object value, RegistryValueKind kind)
    {
        changes.Add(RegCapture(hive, key, name));
        using var k = Reg.Root(hive).CreateSubKey(key, true) ?? throw new InvalidOperationException("Anahtar açılamadı: " + key);
        k.SetValue(name, value, kind);
    }

    public static ChangeRecord RegCapture(string hive, string key, string name)
    {
        var c = new ChangeRecord { Type = "reg", Hive = hive, Key = key, Name = name };
        using var k = Reg.Root(hive).OpenSubKey(key);
        var v = k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (k is null || v is null) return c;
        var kind = k.GetValueKind(name);
        c.Existed = true;
        c.Kind = kind.ToString();
        c.Data = kind switch
        {
            RegistryValueKind.Binary => Convert.ToBase64String((byte[])v),
            RegistryValueKind.MultiString => JsonSerializer.Serialize((string[])v),
            RegistryValueKind.DWord => ((int)v).ToString(),
            RegistryValueKind.QWord => ((long)v).ToString(),
            _ => v.ToString(),
        };
        return c;
    }

    private static void RegRestore(ChangeRecord c)
    {
        using var k = Reg.Root(c.Hive!).CreateSubKey(c.Key!, true);
        if (!c.Existed)
        {
            k.DeleteValue(c.Name!, false);
            return;
        }
        var kind = Enum.Parse<RegistryValueKind>(c.Kind!);
        object v = kind switch
        {
            RegistryValueKind.Binary => Convert.FromBase64String(c.Data!),
            RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(c.Data!)!,
            RegistryValueKind.DWord => int.Parse(c.Data!),
            RegistryValueKind.QWord => long.Parse(c.Data!),
            _ => c.Data ?? "",
        };
        k.SetValue(c.Name!, v, kind);
    }
}

internal static class MouseAccel
{
    private const uint SPI_GETMOUSE = 0x0003, SPI_SETMOUSE = 0x0004, SPIF_UPDATEINIFILE = 0x01, SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, int[] vparam, uint winIni);

    public static int[] Get()
    {
        var v = new int[3];
        SystemParametersInfo(SPI_GETMOUSE, 0, v, 0);
        return v;
    }

    public static void Set(int t1, int t2, int accel)
    {
        if (!SystemParametersInfo(SPI_SETMOUSE, 0, new[] { t1, t2, accel }, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
            throw new InvalidOperationException("Fare ayarı değiştirilemedi.");
    }
}
