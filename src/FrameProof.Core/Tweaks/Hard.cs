using System.Reflection;
using System.Runtime.InteropServices;
using FrameProof.Core.Util;
using Microsoft.Win32;

namespace FrameProof.Core.Tweaks;

/// <summary>
/// Sert mod: Windows'u daha da sadeleştiren ama her birinin küçük bir bedeli olan ayarlar. Hiçbiri tek tıkla
/// (Optimize et) yapılmaz; her biri ayrı ayrı uygulanır ve "Geri al"dan geri alınır. Defender, Windows Update,
/// Güvenlik Duvarı ve sürücülere dokunulmaz; servis ya da dosya silinmez, yalnızca kapatılır.
/// </summary>
public static class HardMode
{
    public const string GroupName = "Sert mod";
}

/// <summary>Edge'in kendi güncelleme servisleri ve zamanlanmış görevleri kapatılır. Edge silinmez.</summary>
public sealed class EdgeUpdateTweak : Tweak
{
    private static readonly string[] Services = { "edgeupdate", "edgeupdatem" };
    private const string SvcKey = @"SYSTEM\CurrentControlSet\Services\";

    public override string Id => "edge_update_off";
    public override string Title => "Edge güncelleyicisini kapat";
    public override string Group => HardMode.GroupName;
    public override string WhatItDoes => "Edge'in kendi güncelleme servislerini (edgeupdate, edgeupdatem) ve zamanlanmış güncelleme görevlerini kapatır. Edge silinmez, açıp kullanabilirsin.";
    public override string Effect => "FPS'e etkisi yok. Bilgisayar açılırken ve gün içinde arkada çalışan Edge güncelleme kontrolleri durur.";
    public override string? Risk => "Edge ve WebView2 (Discord, Teams gibi uygulamaların kullandığı tarayıcı parçası) güvenlik güncellemesi almaz. Ayda bir 'Geri al'dan açıp güncelletmek iyi olur.";
    public override bool NeedsAdmin => true;

    private static bool AnyService() => Services.Any(s => Reg.GetInt("HKLM", SvcKey + s, "Start") is not null);

    public override TweakState GetState()
    {
        if (!AnyService()) return TweakState.NotApplicable;
        bool svcOff = Services.All(s => Reg.GetInt("HKLM", SvcKey + s, "Start") is null or 4);
        bool tasksOff = TaskSched.Find(EdgeTask).All(t => !t.Enabled);
        return svcOff && tasksOff ? TweakState.Applied : TweakState.NotApplied;
    }

    private static bool EdgeTask(string name) => name.StartsWith("MicrosoftEdgeUpdateTask", StringComparison.OrdinalIgnoreCase);

    protected override void ApplyCore(List<ChangeRecord> changes)
    {
        foreach (var s in Services)
        {
            var v = Reg.GetInt("HKLM", SvcKey + s, "Start");
            if (v is null or 4) continue;
            Journal.RegSet(changes, "HKLM", SvcKey + s, "Start", 4, RegistryValueKind.DWord);
        }
        foreach (var t in TaskSched.Find(EdgeTask).Where(t => t.Enabled))
        {
            changes.Add(new ChangeRecord { Type = "task", Name = t.Path, Data = "1" });
            TaskSched.SetEnabled(t.Path, false);
        }
    }
}

/// <summary>Başlat menüsü aramasında internet (Bing) sonuçları.</summary>
public sealed class BingSearchTweak : RegValuesTweak
{
    public override string Id => "bing_search_off";
    public override string Title => "Başlat aramasında Bing'i kapat";
    public override string Group => HardMode.GroupName;
    public override string WhatItDoes => "Başlat menüsünde arama yaparken internetten (Bing) gelen sonuçları ve önerileri kapatır. Yalnızca bilgisayarındaki uygulama ve dosyalar aranır.";
    public override string Effect => "FPS'e etkisi yok. Başlat araması daha hızlı açılır, yazdığın her harf internete gitmez.";
    public override string? Risk => "Başlat menüsünden web araması yapılamaz; tarayıcıyı açıp aramak gerekir. Tam etkisi oturumu kapatıp açınca görülür.";

    protected override (string, string, string, int)[] Values => new[]
    {
        ("HKCU", @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1),
        ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0),
    };
}

/// <summary>
/// Eski (Windows 10 / Windows 11 23H2 ve öncesi) Copilot. 24H2 ve sonrasında Copilot ayrı bir Mağaza uygulamasıdır
/// ve bu ilke artık işe yaramaz; orada "bu sistemde gerekmiyor" deriz, uygulamayı kaldırmayı kullanıcıya bırakırız.
/// </summary>
public sealed class CopilotTweak : RegValuesTweak
{
    private const int NewCopilotBuild = 26100;
    public override string Id => "copilot_off";
    public override string Title => "Copilot'u kapat";
    public override string Group => HardMode.GroupName;
    public override string WhatItDoes => NewStyle
        ? "Bu Windows sürümünde Copilot ayrı bir uygulamadır; eski kapatma ayarı işe yaramaz. Kullanmıyorsan Ayarlar > Uygulamalar > Yüklü uygulamalar'dan Copilot'u kaldırabilirsin (Mağaza'dan geri yüklenir)."
        : "Windows'un yerleşik Copilot panelini ve görev çubuğundaki Copilot düğmesini kapatır.";
    public override string Effect => "FPS'e etkisi yok. Copilot paneli ve onun arka plandaki tarayıcı işlemleri çalışmaz.";
    public override string? Risk => "Copilot'u kullanmak istersen 'Geri al'dan açman gerekir.";

    protected override (string, string, string, int)[] Values => new[]
    {
        ("HKCU", @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1),
        ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 0),
    };

    public static bool NewStyle => Environment.OSVersion.Version.Build >= NewCopilotBuild;

    public override TweakState GetState() => NewStyle ? TweakState.NotApplicable : base.GetState();

    public override string StateText => NewStyle
        ? "Gerekmiyor: Copilot burada ayrı uygulama"
        : base.StateText;
}

/// <summary>Hazırda bekletmeyi kapatır, hiberfil.sys silinir. powercfg /h on ile geri gelir.</summary>
public sealed class HibernateTweak : Tweak
{
    private const string K = @"SYSTEM\CurrentControlSet\Control\Power";
    public override string Id => "hibernate_off";
    public override string Title => "Hazırda bekletmeyi kapat (diskte yer açar)";
    public override string Group => HardMode.GroupName;
    public override string WhatItDoes => "Hazırda bekletmeyi ve Hızlı Başlangıç'ı kapatır. Windows'un RAM'i diske yazdığı hiberfil.sys dosyası silinir.";
    public override string Effect => "FPS'e etkisi yok. Diskte RAM'in yaklaşık %40'ı kadar yer açılır (16 GB RAM'de ~6 GB). Kapatıp açınca Windows her seferinde temiz başlar.";
    public override string? Risk => "Bilgisayar soğuk açılışta birkaç saniye geç açılır. Laptopta pil biterken bilgisayar hazırda bekletmeye geçemez, kapanır; kaydedilmemiş iş gider. Geri açmak için diskte RAM kadar boş yer gerekir.";
    public override bool NeedsAdmin => true;

    public static bool Enabled() => Reg.GetInt("HKLM", K, "HibernateEnabled") is 1;

    public override TweakState GetState()
    {
        if (Reg.GetInt("HKLM", K, "HibernateEnabled") is null) return TweakState.NotApplicable;
        return Enabled() ? TweakState.NotApplied : TweakState.Applied;
    }

    protected override void ApplyCore(List<ChangeRecord> changes)
    {
        if (!Enabled()) return;
        var r = Shell.Run(Shell.Sys("powercfg.exe"), "/hibernate off");
        if (!r.Ok) throw new InvalidOperationException("Hazırda bekletme kapatılamadı: " + (r.StdErr + r.StdOut).Trim());
        changes.Add(new ChangeRecord { Type = "hiber", Data = "on" });
    }

    public static void Restore()
    {
        var r = Shell.Run(Shell.Sys("powercfg.exe"), "/hibernate on");
        if (!r.Ok)
            throw new InvalidOperationException("Hazırda bekletme açılamadı (diskte RAM kadar boş yer olmayabilir): " + (r.StdErr + r.StdOut).Trim());
    }
}

/// <summary>Pencere animasyonları ve saydamlık. Yazı yumuşatma (ClearType) ve küçük resimlere dokunulmaz.</summary>
public sealed class VisualFxTweak : Tweak
{
    private const string Desk = @"Control Panel\Desktop";
    private const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Pers = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string Vfx = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";

    // UserPreferencesMask: bayt 0 = menü/açılır liste animasyonu, liste kaydırma; bayt 1 = menü, seçim ve araç ipucu solması.
    private const byte Mask0 = 0x02 | 0x04 | 0x08;
    private const byte Mask1 = 0x02 | 0x04 | 0x08 | 0x10;

    public override string Id => "visual_fx_perf";
    public override string Title => "Görsel efektleri performansa al";
    public override string Group => HardMode.GroupName;
    public override string WhatItDoes => "Pencere açma/küçültme animasyonlarını, görev çubuğu animasyonlarını, menü solmalarını ve saydamlığı kapatır. Yazı yumuşatma ve dosya küçük resimleri kalır.";
    public override string Effect => "Tam ekran oyunda FPS'i değiştirmez. Pencereli/kenarlıksız oyunda ve zayıf laptoplarda masaüstü biraz daha hızlı hisseder. Tam etkisi oturumu kapatıp açınca görülür.";
    public override string? Risk => "Windows daha düz ve 'eski' görünür; pencereler animasyonsuz açılır.";

    private static byte[]? MaskNow() => Reg.Get("HKCU", Desk, "UserPreferencesMask") as byte[];

    public override TweakState GetState()
    {
        var m = MaskNow();
        bool maskOk = m is null || m.Length < 2 || ((m[0] & Mask0) == 0 && (m[1] & Mask1) == 0);
        bool ok = maskOk
            && Reg.GetString("HKCU", Desk + @"\WindowMetrics", "MinAnimate") == "0"
            && Reg.GetInt("HKCU", Adv, "TaskbarAnimations") == 0
            && Reg.GetInt("HKCU", Pers, "EnableTransparency") == 0;
        return ok ? TweakState.Applied : TweakState.NotApplied;
    }

    protected override void ApplyCore(List<ChangeRecord> changes)
    {
        var m = MaskNow();
        if (m is { Length: >= 2 } && ((m[0] & Mask0) != 0 || (m[1] & Mask1) != 0))
        {
            var n = (byte[])m.Clone();
            n[0] = (byte)(n[0] & ~Mask0);
            n[1] = (byte)(n[1] & ~Mask1);
            Journal.RegSet(changes, "HKCU", Desk, "UserPreferencesMask", n, RegistryValueKind.Binary);
        }
        if (Reg.GetString("HKCU", Desk + @"\WindowMetrics", "MinAnimate") != "0")
            Journal.RegSet(changes, "HKCU", Desk + @"\WindowMetrics", "MinAnimate", "0", RegistryValueKind.String);
        if (Reg.GetInt("HKCU", Adv, "TaskbarAnimations") != 0)
            Journal.RegSet(changes, "HKCU", Adv, "TaskbarAnimations", 0, RegistryValueKind.DWord);
        if (Reg.GetInt("HKCU", Pers, "EnableTransparency") != 0)
            Journal.RegSet(changes, "HKCU", Pers, "EnableTransparency", 0, RegistryValueKind.DWord);
        // Görsel Efektler penceresi "Özel" göstersin.
        if (changes.Count > 0 && Reg.GetInt("HKCU", Vfx, "VisualFXSetting") != 3)
            Journal.RegSet(changes, "HKCU", Vfx, "VisualFXSetting", 3, RegistryValueKind.DWord);
        if (changes.Count > 0) VisualFx.Broadcast();
    }
}

internal static class VisualFx
{
    private const uint SPI_SETANIMATION = 0x0049, SPIF_UPDATEINIFILE = 0x01, SPIF_SENDCHANGE = 0x02;
    private const int HWND_BROADCAST = 0xFFFF, WM_SETTINGCHANGE = 0x001A;

    [StructLayout(LayoutKind.Sequential)]
    private struct ANIMATIONINFO { public uint cbSize; public int iMinAnimate; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref ANIMATIONINFO vparam, uint winIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    /// <summary>Küçültme animasyonunu hemen kapatır ve açık pencerelere ayar değişikliğini duyurur. Hata olursa sessiz geçer.</summary>
    public static void Broadcast()
    {
        try
        {
            var ai = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>(), iMinAnimate = 0 };
            SystemParametersInfo(SPI_SETANIMATION, ai.cbSize, ref ai, 0);
            SendMessageTimeout((IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", 0x0002, 1000, out _);
        }
        catch { }
    }
}

public sealed record SchedTask(string Path, string Name, bool Enabled);

/// <summary>
/// Görev Zamanlayıcı (COM). schtasks metin çıktısı dile göre değiştiği için onu okumuyoruz.
/// dynamic yerine yansıma kullanılır; tek dosya yayında da çalışır.
/// </summary>
public static class TaskSched
{
    private static object Call(object o, string name, params object?[] args) =>
        o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args)!;

    private static object? Get(object o, string name) =>
        o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null);

    private static object Connect()
    {
        var t = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Görev Zamanlayıcı bulunamadı.");
        var svc = Activator.CreateInstance(t)!;
        Call(svc, "Connect", null, null, null, null);
        return svc;
    }

    /// <summary>Kök klasördeki (gizliler dahil) adı eşleşen görevler. Hata olursa boş liste.</summary>
    public static List<SchedTask> Find(Func<string, bool> match)
    {
        var list = new List<SchedTask>();
        try
        {
            var svc = Connect();
            var folder = Call(svc, "GetFolder", "\\");
            var tasks = Call(folder, "GetTasks", 1); // TASK_ENUM_HIDDEN
            int n = Convert.ToInt32(Get(tasks, "Count"));
            for (int i = 1; i <= n; i++)
            {
                var task = tasks.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, tasks, new object[] { i })!;
                var name = Get(task, "Name")?.ToString() ?? "";
                if (!match(name)) continue;
                list.Add(new SchedTask(Get(task, "Path")?.ToString() ?? "\\" + name, name, Convert.ToBoolean(Get(task, "Enabled"))));
            }
        }
        catch { }
        return list;
    }

    /// <summary>Görevi açar/kapatır. Görev artık yoksa false döner (hata değildir).</summary>
    public static bool SetEnabled(string path, bool enabled)
    {
        var svc = Connect();
        var folder = Call(svc, "GetFolder", "\\");
        object task;
        try { task = Call(folder, "GetTask", path); }
        catch (TargetInvocationException ex) when (IsNotFound(ex.InnerException)) { return false; }
        task.GetType().InvokeMember("Enabled", BindingFlags.SetProperty, null, task, new object[] { enabled });
        return true;
    }

    /// <summary>0x80070002 / 0x80070003: görev ya da yol yok. Erişim reddi gibi diğer hatalar gerçek hatadır.</summary>
    public static bool IsNotFound(Exception? e) =>
        e is FileNotFoundException or DirectoryNotFoundException
        || e is COMException c && (c.HResult == unchecked((int)0x80070002) || c.HResult == unchecked((int)0x80070003));
}
