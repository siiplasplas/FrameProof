using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FrameProof.Core.Util;

namespace FrameProof.Core.Update;

/// <summary>Sitedeki version.json. Exe'nin kendisi GitHub Releases'te durur (Pages 25 MB üstü dosya almıyor).</summary>
public sealed record UpdateInfo(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("notes")] string? Notes)
{
    [JsonIgnore] public Version Parsed => System.Version.TryParse(Version.TrimStart('v'), out var v) ? v : new Version(0, 0);
}

/// <summary>
/// Kendini güncelleme. Kurallar:
/// - Sürüm bilgisi yalnız tek bir sabit HTTPS adresinden okunur; indirme yalnız izinli sunuculardan yapılır.
/// - İndirilen dosyanın SHA-256'sı ve boyutu version.json ile tutmazsa hiçbir şey değişmez.
/// - Hiçbir şey kendiliğinden kurulmaz; kullanıcı "Güncelle"ye basar.
/// - Değiştirme: çalışan exe ".old" olarak yeniden adlandırılır (Windows buna izin verir), yenisi yerine konur,
///   uygulama yeniden başlar; ".old" bir sonraki açılışta silinir.
/// </summary>
public static class Updater
{
    public const string ManifestUrl = "https://getframeproof.pages.dev/version.json";

    private static readonly string[] AllowedHosts =
    {
        "getframeproof.pages.dev", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    /// <summary>Test için yerel sunucu: FRAMEPROOF_UPDATE_URL=http://127.0.0.1:port/version.json (yalnız localhost kabul edilir).</summary>
    private static string? TestUrl
    {
        get
        {
            var u = Environment.GetEnvironmentVariable("FRAMEPROOF_UPDATE_URL");
            return u != null && Uri.TryCreate(u, UriKind.Absolute, out var x) && IsLocal(x) ? u : null;
        }
    }

    private static bool IsLocal(Uri u) => u.IsLoopback && (u.Scheme == "http" || u.Scheme == "https");

    private static bool Allowed(Uri u) =>
        (u.Scheme == Uri.UriSchemeHttps && AllowedHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase))
        || (TestUrl != null && IsLocal(u));

    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    private static HttpClient Client()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"FrameProof/{CurrentText}");
        return c;
    }

    /// <summary>Denetim sonucu: Info yeni sürüm (yoksa null), Error okunamadıysa nedeni.</summary>
    public sealed record CheckResult(UpdateInfo? Info, string? Error);

    /// <summary>Yeni sürüm varsa bilgisini döner; yoksa ya da internet yoksa Info null. Hata fırlatmaz.</summary>
    public static async Task<CheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var c = Client();
            c.Timeout = TimeSpan.FromSeconds(10);
            var url = TestUrl ?? ManifestUrl;
            // Önbellekten eski dosya gelmesin
            var json = await c.GetStringAsync(url + (url.Contains('?') ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks, ct);
            var info = JsonSerializer.Deserialize<UpdateInfo>(json);
            if (info == null || !IsValid(info)) return new(null, "Sürüm bilgisi geçersiz.");
            return new(info.Parsed > Current ? info : null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new(null, ex is HttpRequestException or TaskCanceledException ? "Siteye ulaşılamadı (internet bağlantısını kontrol et)." : ex.Message);
        }
    }

    public static bool IsValid(UpdateInfo i) =>
        Uri.TryCreate(i.Url, UriKind.Absolute, out var u) && Allowed(u)
        && i.Sha256 is { Length: 64 } && i.Sha256.All(Uri.IsHexDigit)
        && i.Size is > 1_000_000 and < 500_000_000;

    private static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Uygulamanın yolu bulunamadı.");
    private static string NewPath => ExePath + ".new";
    private static string OldPath => ExePath + ".old";

    /// <summary>Yeni exe'yi uygulamanın yanına indirir ve doğrular. Doğrulanmış dosyanın yolunu döner.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        if (!IsValid(info)) throw new InvalidOperationException("Güncelleme bilgisi geçersiz.");
        var part = NewPath + ".part";
        try
        {
            using var c = Client();
            c.Timeout = TimeSpan.FromMinutes(15);
            using var resp = await c.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var final = resp.RequestMessage?.RequestUri;
            if (final == null || !Allowed(final)) throw new InvalidOperationException($"İndirme beklenmeyen bir adrese yönlendi ({final?.Host}). Güncelleme yapılmadı.");

            using var sha = SHA256.Create();
            long done = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    done += n;
                    if (done > info.Size) throw new InvalidOperationException("İndirilen dosya beklenenden büyük. Güncelleme yapılmadı.");
                    sha.TransformBlock(buf, 0, n, null, 0);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    progress?.Report((done, info.Size));
                }
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var hash = Convert.ToHexString(sha.Hash!);
            if (done != info.Size || !hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("İndirilen dosya doğrulanamadı (bozuk ya da değiştirilmiş). Güncelleme yapılmadı.");

            File.Move(part, NewPath, overwrite: true);
            return NewPath;
        }
        finally
        {
            TryDelete(part);
        }
    }

    /// <summary>
    /// Çalışan exe'yi doğrulanmış yenisiyle değiştirir ve yenisini başlatır. Başarılıysa çağıran uygulamayı kapatmalı.
    /// Bir adım başarısız olursa eski exe yerine geri konur.
    /// </summary>
    public static void ApplyAndRestart(string verifiedNew, bool keepLimitedMode)
    {
        var exe = ExePath;
        TryDelete(OldPath);
        File.Move(exe, OldPath);
        try
        {
            File.Move(verifiedNew, exe);
        }
        catch
        {
            File.Move(OldPath, exe); // geri koy
            throw;
        }
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        psi.ArgumentList.Add("--updated");
        if (keepLimitedMode) psi.ArgumentList.Add("--no-elevate");
        Process.Start(psi);
    }

    /// <summary>Açılışta: önceki güncellemeden kalan .old/.new dosyalarını siler.</summary>
    public static void CleanupLeftovers()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            // Exe kendisi ".old" adıyla çalışıyorsa (olmamalı) hiçbir şeye dokunma
            if (exe.EndsWith(".old", StringComparison.OrdinalIgnoreCase)) return;
            // Eski süreç kapanırken dosya birkaç saniye kilitli kalabilir: arka planda sabırla dene.
            Task.Run(() =>
            {
                TryDelete(exe + ".old", 20);
                TryDelete(exe + ".new", 3);
                TryDelete(exe + ".new.part", 3);
            });
        }
        catch { }
    }

    private static void TryDelete(string f, int attempts = 5)
    {
        for (int i = 0; i < attempts; i++)
        {
            try { if (File.Exists(f)) File.Delete(f); return; }
            catch { Thread.Sleep(500); }
        }
    }
}
