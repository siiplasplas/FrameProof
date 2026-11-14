using System.Runtime.InteropServices;

namespace FrameProof.Core.Util;

/// <summary>Bilgisayarın üreticisi, modeli, laptop olup olmadığı ve UEFI bilgisi. Yalnız okur.</summary>
public static class Device
{
    private static (string Vendor, string Model, bool Laptop)? _cache;

    /// <summary>Kısa üretici adı: "ASUS", "MSI", "Lenovo"… Tanınmazsa ham adı.</summary>
    public static string Vendor => Read().Vendor;

    public static string Model => Read().Model;

    public static bool IsLaptop => Read().Laptop;

    private static (string Vendor, string Model, bool Laptop) Read()
    {
        if (_cache is { } c) return c;
        string vendor = "", model = "";
        bool laptop = false;
        try
        {
            var cs = Wmi.Query("SELECT Manufacturer, Model, PCSystemType FROM Win32_ComputerSystem").FirstOrDefault();
            var board = Wmi.Query("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
            if (cs != null)
            {
                vendor = Wmi.Str(cs, "Manufacturer");
                model = Wmi.Str(cs, "Model");
                laptop = Wmi.Long(cs, "PCSystemType") == 2;
            }
            // Toplama masaüstünde sistem adı "To Be Filled By O.E.M." gibi gelir: anakartı kullan.
            if (board != null && (IsPlaceholder(vendor) || IsPlaceholder(model)))
            {
                vendor = Wmi.Str(board, "Manufacturer");
                model = Wmi.Str(board, "Product");
            }
            if (!laptop)
            {
                var enc = Wmi.Query("SELECT ChassisTypes FROM Win32_SystemEnclosure").FirstOrDefault();
                if (enc != null) laptop = Wmi.UIntArray(enc, "ChassisTypes").Any(t => t is 8 or 9 or 10 or 11 or 12 or 14 or 18 or 21 or 31 or 32);
            }
        }
        catch { }
        var r = (Short(vendor), model.Trim(), laptop);
        _cache = r;
        return r;
    }

    private static bool IsPlaceholder(string s) =>
        string.IsNullOrWhiteSpace(s) || s.Contains("To Be Filled", StringComparison.OrdinalIgnoreCase)
        || s.Contains("System Product", StringComparison.OrdinalIgnoreCase) || s.Contains("System manufacturer", StringComparison.OrdinalIgnoreCase)
        || s.Contains("Default string", StringComparison.OrdinalIgnoreCase);

    public static string Short(string raw)
    {
        var s = raw.Trim();
        (string Key, string Name)[] known =
        {
            ("ASUS", "ASUS"), ("Micro-Star", "MSI"), ("MSI", "MSI"), ("Gigabyte", "Gigabyte"), ("AORUS", "Gigabyte"),
            ("ASRock", "ASRock"), ("Lenovo", "Lenovo"), ("HP", "HP"), ("Hewlett", "HP"), ("Dell", "Dell"), ("Alienware", "Dell"),
            ("Acer", "Acer"), ("Monster", "Monster"), ("Casper", "Casper"), ("Huawei", "Huawei"), ("Samsung", "Samsung"),
            ("Microsoft", "Microsoft"), ("Razer", "Razer"), ("Tongfang", "Tongfang"), ("Clevo", "Clevo"), ("Xiaomi", "Xiaomi"),
        };
        foreach (var (k, n) in known)
            if (s.StartsWith(k, StringComparison.OrdinalIgnoreCase) || s.Contains(k + " ", StringComparison.OrdinalIgnoreCase)
                || s.Equals(k, StringComparison.OrdinalIgnoreCase))
                return n;
        return s;
    }

    // ---- UEFI ----

    private enum FirmwareType { Unknown = 0, Bios = 1, Uefi = 2 }

    [DllImport("kernel32.dll")]
    private static extern bool GetFirmwareType(out FirmwareType t);

    /// <summary>Windows UEFI ile açılmışsa true (BIOS'a doğrudan yeniden başlatma yalnız bunda çalışır).</summary>
    public static bool IsUefi
    {
        get
        {
            try { return GetFirmwareType(out var t) && t == FirmwareType.Uefi; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Bilgisayarı hemen yeniden başlatır ve doğrudan BIOS/UEFI ayar ekranına girer (Windows'un kendi "shutdown /r /fw" komutu).
    /// Yönetici izni ister. Başarısızsa nedenini döner; başarılıysa null.
    /// </summary>
    public static string? RebootToFirmware()
    {
        if (!Admin.IsElevated) return "Bunun için yönetici izni gerekiyor.";
        if (!IsUefi) return "Bu bilgisayar eski BIOS modunda açılmış; doğrudan BIOS'a yeniden başlatma desteklenmiyor.";
        var r = Shell.Run(Shell.Sys("shutdown.exe"), "/r /fw /t 0", 15_000);
        return r.ExitCode == 0 ? null : "Windows yeniden başlatmayı reddetti: " + (r.StdErr + r.StdOut).Trim();
    }
}
