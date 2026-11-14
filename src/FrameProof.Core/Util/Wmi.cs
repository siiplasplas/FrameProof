using System.Management;

namespace FrameProof.Core.Util;

public static class Wmi
{
    public static List<ManagementBaseObject> Query(string wql, string ns = @"root\cimv2")
    {
        try
        {
            using var s = new ManagementObjectSearcher(ns, wql);
            return s.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch { return new(); }
    }

    public static string Str(ManagementBaseObject o, string p)
    {
        try { return o[p]?.ToString()?.Trim() ?? ""; } catch { return ""; }
    }

    public static long Long(ManagementBaseObject o, string p)
    {
        try { var v = o[p]; return v is null ? 0 : Convert.ToInt64(v); } catch { return 0; }
    }

    public static uint[] UIntArray(ManagementBaseObject o, string p)
    {
        try { return o[p] is uint[] a ? a : Array.Empty<uint>(); } catch { return Array.Empty<uint>(); }
    }

    public static DateTime? Date(ManagementBaseObject o, string p)
    {
        try
        {
            var s = Str(o, p);
            return string.IsNullOrEmpty(s) ? null : ManagementDateTimeConverter.ToDateTime(s);
        }
        catch { return null; }
    }
}

public sealed record VbsState(bool VbsRunning, bool HvciRunning, bool HvciConfiguredOff, bool Locked);

public static class Vbs
{
    public const string DgKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
    public const string HvciKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";

    public static VbsState Read()
    {
        bool vbs = false, hvci = false;
        foreach (var o in Wmi.Query("SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard", @"root\Microsoft\Windows\DeviceGuard"))
        {
            vbs = Wmi.Long(o, "VirtualizationBasedSecurityStatus") == 2;
            hvci = Wmi.UIntArray(o, "SecurityServicesRunning").Contains(2u);
        }
        var cfg = Reg.GetInt("HKLM", HvciKey, "Enabled");
        var locked = Reg.GetInt("HKLM", HvciKey, "Locked") == 1 || Reg.GetInt("HKLM", DgKey, "Locked") == 1;
        return new VbsState(vbs, hvci, cfg == 0, locked);
    }
}
