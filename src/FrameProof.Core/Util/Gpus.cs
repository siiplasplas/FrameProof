using System.Runtime.InteropServices;

namespace FrameProof.Core.Util;

/// <param name="Outputs">Bu karta doğrudan bağlı ekran sayısı (monitör kablosu hangi karta takılı).</param>
public sealed record GpuAdapter(string Name, uint VendorId, long DedicatedMemory, uint LuidLow, int LuidHigh, bool Integrated, int Outputs = 0)
{
    public string LuidKey => $"0x{(uint)LuidHigh:X8}_0x{LuidLow:X8}";
}

/// <summary>
/// DXGI ile ekran kartlarını listeler. LUID, oyunun hangi kartta çalıştığını (GPU Engine sayaçlarından)
/// bulmak için gerekir. Yazılımsal "Microsoft Basic Render Driver" atlanır.
/// </summary>
public static class Gpus
{
    private static List<GpuAdapter>? _cache;

    public static List<GpuAdapter> List() => _cache ??= Enumerate();

    /// <summary>Hem dahili hem harici kart varsa (çoğu oyun laptopu).</summary>
    public static bool IsHybrid
    {
        get
        {
            var l = List();
            return l.Any(a => a.Integrated) && l.Any(a => !a.Integrated);
        }
    }

    public static GpuAdapter? Discrete => List().Where(a => !a.Integrated).OrderByDescending(a => a.DedicatedMemory).FirstOrDefault();

    public static GpuAdapter? ByLuid(string luidKey) =>
        List().FirstOrDefault(a => a.LuidKey.Equals(luidKey, StringComparison.OrdinalIgnoreCase));

    private const uint NVIDIA = 0x10DE, AMD = 0x1002, INTEL = 0x8086;

    private static List<GpuAdapter> Enumerate()
    {
        var list = new List<GpuAdapter>();
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var factory) != 0 || factory is null) return list;
            try
            {
                for (uint i = 0; i < 16; i++)
                {
                    if (factory.EnumAdapters1(i, out var adapter) != 0 || adapter is null) break;
                    try
                    {
                        if (adapter.GetDesc1(out var d) != 0) continue;
                        if ((d.Flags & 2) != 0) continue; // yazılımsal adaptör
                        long mem = (long)d.DedicatedVideoMemory;
                        // NVIDIA her zaman harici. AMD/Intel: 1 GB'tan az ayrılmış bellek = işlemcinin içindeki kart.
                        bool integrated = d.VendorId != NVIDIA && mem < 1L * 1024 * 1024 * 1024
                            && !d.Description.Contains("Arc", StringComparison.OrdinalIgnoreCase);
                        if (list.Any(a => a.LuidLow == d.AdapterLuidLow && a.LuidHigh == d.AdapterLuidHigh)) continue;
                        list.Add(new GpuAdapter(d.Description.Trim(), d.VendorId, mem, d.AdapterLuidLow, d.AdapterLuidHigh, integrated, CountOutputs(adapter)));
                    }
                    finally { Marshal.ReleaseComObject(adapter); }
                }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch { }
        return list;
    }

    private static int CountOutputs(IDXGIAdapter1 adapter)
    {
        int n = 0;
        for (uint i = 0; i < 16; i++)
        {
            if (adapter.EnumOutputs(i, out var o) != 0 || o is null) break;
            n++;
            Marshal.ReleaseComObject(o);
        }
        return n;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1? factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;
    }

    // Yalnız çağrılan metotların imzası önemli; öncekiler vtable sırasını korumak için yer tutucu.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1? adapter);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        [PreserveSig] int EnumOutputs(uint index, [MarshalAs(UnmanagedType.IUnknown)] out object? output);
        void GetDesc();
        void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }
}
