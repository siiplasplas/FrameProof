using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FrameProof.Core.Util;

namespace FrameProof.Core.Measure;

/// <summary>
/// Ölçüm sırasında ~2 sn'de bir donanımı okur: oyun hangi ekran kartında çalışıyor, işlemci hızı düşüyor mu,
/// NVIDIA kart ısıdan ya da güç sınırından yavaşlıyor mu, laptop pilde mi, RAM yetiyor mu. Yalnız okur, oyuna dokunmaz.
/// </summary>
public sealed class LiveSampler
{
    private readonly string _name; // uzantısız süreç adı
    private readonly Dictionary<string, double> _gpuUtil = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<double> _cpuPerf = new();
    private readonly List<double> _gpuTemp = new();
    private readonly List<double> _gpuClockPct = new();
    private int _nvSamples, _thermal, _powerCap, _battery, _samples;
    private long _ramTotalMb, _ramAvailMinMb = long.MaxValue, _commitPeakPct;
    private readonly List<double> _pagesIn = new();
    private string? _exePath;
    private bool _nvOldNames;

    public LiveSampler(string processName)
    {
        _name = Path.GetFileNameWithoutExtension(processName);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try { await Task.Delay(1500, ct); } catch { return; } // oyun ve sayaçlar otursun
        while (!ct.IsCancellationRequested)
        {
            try { Sample(); } catch { }
            try { await Task.Delay(2000, ct); } catch { break; }
        }
    }

    private void Sample()
    {
        _samples++;
        var pids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(_name))
        {
            try
            {
                pids.Add(p.Id);
                _exePath ??= p.MainModule?.FileName;
            }
            catch { }
            finally { p.Dispose(); }
        }

        // 1) Oyunun 3D motorunu hangi ekran kartı çalıştırıyor
        if (pids.Count > 0)
        {
            foreach (var o in Wmi.Query("SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"))
            {
                var n = Wmi.Str(o, "Name");
                if (!n.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
                var m = EngRx.Match(n);
                if (!m.Success || !pids.Contains(int.Parse(m.Groups[1].Value))) continue;
                var luid = $"{m.Groups[2].Value}_{m.Groups[3].Value}";
                _gpuUtil[luid] = _gpuUtil.GetValueOrDefault(luid) + Wmi.Long(o, "UtilizationPercentage");
            }
        }

        // 2) İşlemci hızı (nominal saat hızının yüzdesi; 100'ün üstü = turbo)
        var cpu = Wmi.Query("SELECT PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'").FirstOrDefault();
        if (cpu != null)
        {
            var v = Wmi.Long(cpu, "PercentProcessorPerformance");
            if (v > 0) _cpuPerf.Add(v);
        }

        // 3) NVIDIA: sıcaklık, saat hızı ve yavaşlatma sebepleri
        Nvidia();

        // 4) Pil
        if (GetSystemPowerStatus(out var ps) && ps.ACLineStatus == 0) _battery++;

        // 5) RAM: en az ne kadar boş kaldı, sanal bellek ne kadar doldu, diskten ne sıklıkla sayfa okundu
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms))
        {
            _ramTotalMb = (long)(ms.ullTotalPhys / (1024 * 1024));
            _ramAvailMinMb = Math.Min(_ramAvailMinMb, (long)(ms.ullAvailPhys / (1024 * 1024)));
            if (ms.ullTotalPageFile > 0)
                _commitPeakPct = Math.Max(_commitPeakPct, (long)Math.Round(100.0 * (ms.ullTotalPageFile - ms.ullAvailPageFile) / ms.ullTotalPageFile));
        }
        var mem = Wmi.Query("SELECT PagesInputPersec FROM Win32_PerfFormattedData_PerfOS_Memory").FirstOrDefault();
        if (mem != null) _pagesIn.Add(Wmi.Long(mem, "PagesInputPersec"));
    }

    private static readonly Regex EngRx = new(@"pid_(\d+)_luid_(0x[0-9A-Fa-f]+)_(0x[0-9A-Fa-f]+)_", RegexOptions.Compiled);

    private static string? NvSmi()
    {
        var a = Shell.Sys("nvidia-smi.exe");
        if (File.Exists(a)) return a;
        var b = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvidia-smi.exe");
        return File.Exists(b) ? b : null;
    }

    private void Nvidia()
    {
        var exe = NvSmi();
        if (exe is null) return;
        string pre = _nvOldNames ? "clocks_throttle_reasons" : "clocks_event_reasons";
        var r = Shell.Run(exe, $"--query-gpu=temperature.gpu,clocks.sm,clocks.max.sm,{pre}.hw_thermal_slowdown,{pre}.sw_thermal_slowdown,{pre}.sw_power_cap --format=csv,noheader,nounits", 5000);
        if (!r.Ok && !_nvOldNames)
        {
            _nvOldNames = true;
            Nvidia();
            return;
        }
        if (!r.Ok) return;
        var line = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (line is null) return;
        var c = line.Split(',').Select(x => x.Trim()).ToArray();
        if (c.Length < 6) return;
        _nvSamples++;
        if (double.TryParse(c[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) _gpuTemp.Add(t);
        if (double.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var clk)
            && double.TryParse(c[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var max) && max > 0)
            _gpuClockPct.Add(clk / max * 100);
        if (Active(c[3]) || Active(c[4])) _thermal++;
        if (Active(c[5])) _powerCap++;
    }

    private static bool Active(string s) => s.Equals("Active", StringComparison.OrdinalIgnoreCase);

    /// <summary>Toplananları ölçüm özetine yazar.</summary>
    public void Apply(RunSummary s)
    {
        s.HwSamples = _samples;
        s.ExePath = _exePath;
        if (_gpuUtil.Count > 0)
        {
            var top = _gpuUtil.OrderByDescending(kv => kv.Value).First();
            var a = Gpus.ByLuid(top.Key);
            if (a != null && top.Value > 0)
            {
                s.GpuName = a.Name;
                s.GpuIntegrated = a.Integrated;
            }
        }
        if (_cpuPerf.Count > 0)
        {
            s.CpuPerfAvg = Math.Round(_cpuPerf.Average());
            s.CpuPerfMin = _cpuPerf.Min();
        }
        if (_gpuTemp.Count > 0) s.GpuTempMax = _gpuTemp.Max();
        if (_gpuClockPct.Count > 0) s.GpuClockPct = Math.Round(_gpuClockPct.Average());
        if (_nvSamples > 0)
        {
            s.ThermalPct = Math.Round(100.0 * _thermal / _nvSamples);
            s.PowerCapPct = Math.Round(100.0 * _powerCap / _nvSamples);
        }
        if (_samples > 0) s.OnBattery = _battery * 2 >= _samples;
        if (_ramTotalMb > 0)
        {
            s.RamTotalMb = _ramTotalMb;
            s.RamAvailMinMb = _ramAvailMinMb;
            s.CommitPeakPct = _commitPeakPct;
        }
        if (_pagesIn.Count > 0) s.PagesInAvg = Math.Round(_pagesIn.Average());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
}
