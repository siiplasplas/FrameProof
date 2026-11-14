using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FrameProof.Core.Downloads;

public sealed record DownloadRequest(string Id, Uri Url, string Sha256, long SizeBytes);
public sealed record DownloadProgress(long ReceivedBytes, long TotalBytes);

/// <summary>Streams verified HTTPS packages into a content-addressed cache. Caller owns HttpClient.</summary>
public sealed class HttpPackageDownloader
{
    private readonly HttpClient _client;
    private readonly string _root;
    public string CacheRoot => _root;
    private sealed record ResumeState(string SourceFingerprint, string? ETag);

    public HttpPackageDownloader(HttpClient client, string cacheRoot)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _root = Path.GetFullPath(cacheRoot);
        NoLinks(_root);
        Directory.CreateDirectory(_root);
    }

    public async Task<string> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default,
        IProgress<DownloadProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Id) || request.Url is null || !request.Url.IsAbsoluteUri ||
            request.Url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(request.Url.UserInfo) ||
            request.SizeBytes <= 0 || !Regex.IsMatch(request.Sha256 ?? "", "\\A[0-9a-fA-F]{64}\\z"))
            throw new ArgumentException("İndirme için HTTPS adresi, SHA256 ve pozitif dosya boyutu gerekli.", nameof(request));
        var key = request.Sha256!.ToLowerInvariant() + "-" + request.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var ready = Path.Combine(_root, key + ".bin");
        var partial = Path.Combine(_root, key + ".part");
        var metadata = Path.Combine(_root, key + ".resume.json");
        using var lease = await AcquireAsync(Path.Combine(_root, key + ".lock"), cancellationToken);
        NoLinks(ready); NoLinks(partial); NoLinks(metadata);
        if (File.Exists(ready))
        {
            if (await MatchesAsync(ready, request, cancellationToken))
            {
                progress?.Report(new(request.SizeBytes, request.SizeBytes));
                return ready;
            }
            File.Delete(ready);
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Url.AbsoluteUri)));
        ResumeState? state = null;
        if (File.Exists(metadata))
        {
            try { state = JsonSerializer.Deserialize<ResumeState>(await File.ReadAllTextAsync(metadata, cancellationToken)); }
            catch (JsonException) { }
        }
        long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset == request.SizeBytes)
        {
            if (await MatchesAsync(partial, request, cancellationToken))
            {
                File.Move(partial, ready, overwrite: true);
                File.Delete(metadata);
                progress?.Report(new(offset, request.SizeBytes));
                return ready;
            }
            DeletePartial(partial, metadata);
            throw new InvalidDataException("İndirilen paketin SHA256 doğrulaması başarısız.");
        }
        if (offset > request.SizeBytes || state?.SourceFingerprint != fingerprint || !StrongETag(state?.ETag, out _))
        {
            DeletePartial(partial, metadata);
            offset = 0;
            state = null;
        }
        using var message = new HttpRequestMessage(HttpMethod.Get, request.Url);
        message.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        if (offset > 0)
        {
            message.Headers.Range = new RangeHeaderValue(offset, null);
            message.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(state!.ETag!));
        }
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is not null && (!finalUri.IsAbsoluteUri || finalUri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidDataException("İndirme HTTPS dışındaki bir adrese yönlendirildi.");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentEncoding.Any(x => !x.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Sıkıştırılmış HTTP içerik aktarımı desteklenmiyor.");
            var etag = response.Headers.ETag;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (offset == 0 || range is null || range.Unit != "bytes" || range.From != offset ||
                    range.To != request.SizeBytes - 1 || range.Length != request.SizeBytes ||
                    etag is null || etag.IsWeak || etag.ToString() != state?.ETag)
                    throw new InvalidDataException("Sunucunun devam yanıtı veya dosya sürümü geçersiz.");
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                // Range ignored or If-Range failed: discard old bytes before recording the new validator.
                offset = 0;
                DeletePartial(partial, metadata);
            }
            else throw new InvalidDataException("Sunucu beklenmeyen bir indirme yanıtı gönderdi.");
            if (response.Content.Headers.ContentLength is long length && length != request.SizeBytes - offset)
                throw new InvalidDataException("Sunucudaki dosya boyutu katalogla eşleşmiyor.");
            await WriteStateAsync(metadata, new(fingerprint, etag is { IsWeak: false } ? etag.ToString() : null), cancellationToken);
            NoLinks(partial);
            using (var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                if (output.Length != offset) throw new InvalidDataException("Kısmi indirme başka bir işlem tarafından değiştirildi.");
                output.Position = offset;
                using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[81920];
                progress?.Report(new(offset, request.SizeBytes));
                while (true)
                {
                    var count = await input.ReadAsync(buffer, cancellationToken);
                    if (count == 0) break;
                    if (count > request.SizeBytes - offset) throw new InvalidDataException("İndirme beklenen dosya boyutunu aştı.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    offset += count;
                    progress?.Report(new(offset, request.SizeBytes));
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            if (offset != request.SizeBytes) throw new IOException("İndirme yarıda kesildi; yeniden denenince devam edilecek.");
            if (!await MatchesAsync(partial, request, cancellationToken))
                throw new InvalidDataException("İndirilen paketin SHA256 doğrulaması başarısız.");
            cancellationToken.ThrowIfCancellationRequested();
            NoLinks(ready);
            File.Move(partial, ready, overwrite: true);
            File.Delete(metadata);
            return ready;
        }
        catch (InvalidDataException)
        {
            DeletePartial(partial, metadata);
            throw;
        }
    }

    private static bool StrongETag(string? value, out EntityTagHeaderValue? tag) =>
        EntityTagHeaderValue.TryParse(value, out tag) && !tag.IsWeak && tag.Tag != "*";

    private static async Task<bool> MatchesAsync(string path, DownloadRequest request, CancellationToken token)
    {
        NoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        if (stream.Length != request.SizeBytes) return false;
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).Equals(request.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<FileStream> AcquireAsync(string path, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            NoLinks(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { await Task.Delay(100, token); }
        }
    }

    private static async Task WriteStateAsync(string path, ResumeState state, CancellationToken token)
    {
        var temp = path + ".tmp";
        NoLinks(path); NoLinks(temp);
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state), token);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void DeletePartial(string partial, string metadata)
    {
        NoLinks(partial); NoLinks(metadata);
        File.Delete(partial);
        File.Delete(metadata);
    }

    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("İndirme önbelleği bağlantı veya junction içeremez.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
