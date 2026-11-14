using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrameProof.Core.Util;

namespace FrameProof.Core.Measure;

/// <summary>
/// Intel'in açık kaynak PresentMon aracı. Oyunların kare zamanlarını ETW ile okur;
/// oyuna enjekte olmaz, anti-cheat'e dokunmaz. Uygulamaya gömülmez, kullanıcı isteyince indirilir.
/// </summary>
public static class PresentMon
{
    public static string? Locate()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppPaths.Tools, "PresentMon.exe"),
            Path.Combine(AppContext.BaseDirectory, "PresentMon.exe"),
        };
        try
        {
            candidates.AddRange(Directory.EnumerateFiles(AppContext.BaseDirectory, "PresentMon*.exe"));
        }
        catch { }
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidates.Add(Path.Combine(pf, @"Intel\PresentMon\PresentMonApplication\PresentMon.exe"));
        candidates.Add(Path.Combine(pf, @"Intel\PresentMon\PresentMon.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    // ---------- İndirme (kullanıcı onayıyla) ----------

    public sealed record ReleaseAsset(string Version, string Name, string Url, long Size);

    private static HttpClient Http()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FrameProof", "0.1"));
        return h;
    }

    /// <summary>GitHub'daki son PresentMon sürümünün konsol exe'sini bulur (indirmez).</summary>
    public static async Task<ReleaseAsset> FindLatestAsync(CancellationToken ct)
    {
        using var h = Http();
        var json = await h.GetStringAsync("https://api.github.com/repos/GameTechDev/PresentMon/releases/latest", ct);
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (Regex.IsMatch(name, @"^PresentMon-[\d.]+-x64\.exe$", RegexOptions.IgnoreCase))
                return new ReleaseAsset(tag, name, a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64());
        }
        throw new InvalidOperationException("Son PresentMon sürümünde x64 konsol exe'si bulunamadı.");
    }

    /// <summary>İndirir, Intel imzasını doğrular, sonra yerine koyar. İmza tutmazsa dosyayı siler.</summary>
    public static async Task<string> DownloadAsync(ReleaseAsset asset, IProgress<double>? progress, CancellationToken ct)
    {
        if (!asset.Url.StartsWith("https://github.com/GameTechDev/PresentMon/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Beklenmeyen indirme adresi: " + asset.Url);

        var tmp = Path.Combine(AppPaths.Tools, "PresentMon.download");
        var dest = Path.Combine(AppPaths.Tools, "PresentMon.exe");
        try
        {
            using (var h = Http())
            using (var resp = await h.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? asset.Size;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var fs = File.Create(tmp);
                var buf = new byte[81920];
                long got = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                    got += n;
                    if (total > 0) progress?.Report((double)got / total);
                }
            }
            var signer = VerifySignature(tmp);
            if (signer is null || !signer.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("İndirilen dosyanın Intel imzası doğrulanamadı; silindi." + (signer is null ? "" : $" (imzalayan: {signer})"));
            File.Move(tmp, dest, true);
            return dest;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    // ---------- Authenticode ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    /// <summary>İmza geçerliyse imzalayanın adını, değilse null döner.</summary>
    public static string? VerifySignature(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            if (WinVerifyTrust(IntPtr.Zero, ref action, ref data) != 0) return null;
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch { return null; }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    // ---------- Ölçüm ----------

    /// <summary>Ekranda penceresi olan süreçler (oyun seçimi için).</summary>
    public static List<(string Exe, string Title)> WindowedProcesses()
    {
        var self = Environment.ProcessId;
        var list = new List<(string, string)>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self || p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                list.Add((p.ProcessName + ".exe", p.MainWindowTitle));
            }
            catch { }
            finally { p.Dispose(); }
        }
        return list.DistinctBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Verilen oyunu <paramref name="seconds"/> saniye ölçer, özeti kaydeder, ham CSV'yi siler.
    /// Yönetici izni (ya da Performance Log Users grubu) gerekir.
    /// </summary>
    public static async Task<RunSummary> CaptureAsync(string exe, string processName, int seconds, string label, CancellationToken ct)
    {
        var csv = Path.Combine(AppPaths.Captures, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}.csv");
        try
        {
            var full = $"--process_name \"{processName}\" --output_file \"{csv}\" --timed {seconds} --terminate_after_timed --session_name FrameProof --stop_existing_session --no_console_stats";
            var minimal = $"--process_name \"{processName}\" --output_file \"{csv}\" --timed {seconds} --terminate_after_timed";

            var sampler = new LiveSampler(processName);
            using var stopSampler = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sampling = Task.Run(() => sampler.RunAsync(stopSampler.Token));
            ShellResult r;
            try
            {
                r = await RunWithTimeout(exe, full, seconds + 45, ct);
                if (!File.Exists(csv) && LooksLikeArgError(r))
                    r = await RunWithTimeout(exe, minimal, seconds + 45, ct);
            }
            finally
            {
                stopSampler.Cancel();
                try { await sampling; } catch { }
            }

            if (!File.Exists(csv))
            {
                var msg = (r.StdErr + "\n" + r.StdOut).Trim();
                if (msg.Contains("access", StringComparison.OrdinalIgnoreCase) || msg.Contains("elevat", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("PresentMon yönetici izni istiyor. FrameProof'u yönetici olarak aç.");
                throw new InvalidOperationException("Hiç kare yakalanmadı. Oyun açık ve ekranda mıydı? " + Tail(msg));
            }

            var parsed = FrameCsv.Parse(csv);
            var sum = Stats.Summarize(parsed, label);
            if (string.IsNullOrEmpty(sum.Process)) sum.Process = processName;
            try { sampler.Apply(sum); } catch { }
            RunStore.Add(sum);
            return sum;
        }
        finally
        {
            try { if (File.Exists(csv)) File.Delete(csv); } catch { }
        }
    }

    private static bool LooksLikeArgError(ShellResult r)
    {
        var t = (r.StdErr + r.StdOut).ToLowerInvariant();
        return t.Contains("unrecognized") || t.Contains("unknown") || t.Contains("invalid") || t.Contains("usage");
    }

    private static string Tail(string s) => s.Length > 300 ? s[^300..] : s;

    private static async Task<ShellResult> RunWithTimeout(string exe, string args, int timeoutSec, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        try
        {
            return await Shell.RunAsync(exe, args, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ShellResult(-1, "", "zaman aşımı");
        }
    }
}
