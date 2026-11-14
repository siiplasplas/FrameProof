using System.Runtime.InteropServices;

namespace FrameProof.Core.Util;

public sealed record DisplayInfo(string DeviceName, string Adapter, int Width, int Height, int CurrentHz, int MaxHz, bool Primary);

/// <summary>Monitör yenileme hızı okuma/değiştirme (user32 EnumDisplaySettings / ChangeDisplaySettingsEx).</summary>
public static class Displays
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1, DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    private const int DM_DISPLAYFREQUENCY = 0x400000;
    private const int CDS_UPDATEREGISTRY = 0x1, CDS_TEST = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, int flags, IntPtr lParam);

    private static DEVMODE NewMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    public static List<DisplayInfo> List()
    {
        var result = new List<DisplayInfo>();
        for (uint i = 0; ; i++)
        {
            var d = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref d, 0)) break;
            if ((d.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

            var cur = NewMode();
            if (!EnumDisplaySettings(d.DeviceName, ENUM_CURRENT_SETTINGS, ref cur)) continue;

            int max = cur.dmDisplayFrequency;
            var m = NewMode();
            for (int n = 0; EnumDisplaySettings(d.DeviceName, n, ref m); n++)
            {
                if (m.dmPelsWidth == cur.dmPelsWidth && m.dmPelsHeight == cur.dmPelsHeight && m.dmBitsPerPel == cur.dmBitsPerPel)
                    max = Math.Max(max, m.dmDisplayFrequency);
                m = NewMode();
            }
            result.Add(new DisplayInfo(d.DeviceName, d.DeviceString, cur.dmPelsWidth, cur.dmPelsHeight, cur.dmDisplayFrequency, max,
                (d.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0));
        }
        return result;
    }

    public static void SetRefresh(string deviceName, int hz)
    {
        var mode = NewMode();
        if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref mode))
            throw new InvalidOperationException("Ekran bulunamadı: " + deviceName);
        mode.dmDisplayFrequency = hz;
        mode.dmFields = DM_DISPLAYFREQUENCY;
        int test = ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
        if (test != 0) throw new InvalidOperationException($"{hz} Hz bu ekranda desteklenmiyor (kod {test}).");
        int r = ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (r != 0) throw new InvalidOperationException($"Yenileme hızı değiştirilemedi (kod {r}).");
    }
}
