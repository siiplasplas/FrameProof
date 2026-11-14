using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FrameProof.Core.Downloads;
using FrameProof.Core.Installation;

namespace FrameProof.Core.Catalog;

public sealed record GameCatalog
{
    public int SchemaVersion { get; init; }
    public IReadOnlyList<GameInstallRecipe> Games { get; init; } = Array.Empty<GameInstallRecipe>();
}

/// <summary>Loads only an explicitly supplied catalog. The caller owns HttpClient and source trust.</summary>
public sealed class GameCatalogClient
{
    public const int MaximumCatalogBytes = 2 * 1024 * 1024;
    public const int MaximumGames = 1000;
    private const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024 * 1024;
    private const long MaximumExpandedBytes = 4L * 1024 * 1024 * 1024 * 1024;
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    public GameCatalogClient(HttpClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<GameCatalog> LoadAsync(Uri source, CancellationToken cancellationToken = default)
    {
        ValidateUrl(source);
        using var response = await _client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final) ValidateUrl(final);
        if (response.Content.Headers.ContentLength > MaximumCatalogBytes)
            throw new InvalidDataException("Oyun kataloğu boyut sınırını aşıyor.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[16 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > MaximumCatalogBytes)
                throw new InvalidDataException("Oyun kataloğu boyut sınırını aşıyor.");
            buffer.Write(bytes, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Parse(buffer.ToArray());
    }

    public static GameCatalog Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaximumCatalogBytes) throw new InvalidDataException("Oyun kataloğu boyut sınırını aşıyor.");
        GameCatalog catalog;
        try { catalog = JsonSerializer.Deserialize<GameCatalog>(utf8Json, Json) ?? throw new InvalidDataException("Oyun kataloğu boş."); }
        catch (JsonException ex) { throw new InvalidDataException("Oyun kataloğu JSON biçimi geçersiz.", ex); }
        if (catalog.SchemaVersion != 1) throw new InvalidDataException("Oyun kataloğu sürümü desteklenmiyor.");
        if (catalog.Games is null || catalog.Games.Count > MaximumGames) throw new InvalidDataException("Oyun kataloğu kayıt sınırını aşıyor.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packages = new Dictionary<string, DownloadRequest>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipe in catalog.Games)
        {
            ValidateRecipe(recipe);
            if (!ids.Add(recipe.CatalogId)) throw new InvalidDataException("Oyun kataloğunda yinelenen oyun kimliği var.");
            foreach (var package in new[] { recipe.GamePackage, recipe.FixPackage }.OfType<DownloadRequest>())
            {
                if (packages.TryGetValue(package.Id, out var prior) && prior != package)
                    throw new InvalidDataException("Aynı paket kimliği farklı içerikler için kullanılıyor.");
                packages[package.Id] = package;
            }
        }
        return catalog;
    }

    public static void ValidateRecipe(GameInstallRecipe recipe)
    {
        if (recipe is null) throw new InvalidDataException("Oyun tarifi boş.");
        if (!Regex.IsMatch(recipe.CatalogId ?? "", "\\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,79}\\z") || IsDeviceName(recipe.CatalogId!))
            throw new InvalidDataException("Oyun kimliği geçersiz.");
        Text(recipe.Name, 160, "Oyun adı");
        Text(recipe.GameVersion, 80, "Oyun sürümü");
        ValidatePackage(recipe.GamePackage);
        Expanded(recipe.MaximumGameExpandedBytes);
        if (recipe.FixPackage is not null)
        {
            Text(recipe.FixVersion, 80, "Fix sürümü");
            ValidatePackage(recipe.FixPackage);
            Expanded(recipe.MaximumFixExpandedBytes);
        }
        else if (!string.IsNullOrEmpty(recipe.FixVersion)) throw new InvalidDataException("Fix sürümü için paket tanımlanmalı.");
        RelativeExe(recipe.LaunchRelativePath);
        if (recipe.Arguments is null || recipe.Arguments.Length > 32 || recipe.Arguments.Any(a => a is null || a.Length > 2048 || a.IndexOf('\0') >= 0))
            throw new InvalidDataException("Başlatma parametreleri geçersiz.");
    }

    private static void ValidatePackage(DownloadRequest request)
    {
        if (request is null) throw new InvalidDataException("Oyun paketi eksik.");
        Text(request.Id, 160, "Paket kimliği");
        ValidateUrl(request.Url);
        if (!Regex.IsMatch(request.Sha256 ?? "", "\\A[0-9a-fA-F]{64}\\z")) throw new InvalidDataException("Paket SHA256 değeri geçersiz.");
        if (request.SizeBytes <= 0 || request.SizeBytes > MaximumPackageBytes) throw new InvalidDataException("Paket boyutu geçersiz.");
    }

    private static void Expanded(long size)
    {
        if (size <= 0 || size > MaximumExpandedBytes) throw new InvalidDataException("Açılmış paket boyut sınırı geçersiz.");
    }

    private static void ValidateUrl(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("Katalog ve paket adresleri HTTPS kullanmalı ve kullanıcı bilgisi içermemeli.");
    }

    private static void Text(string? value, int maximum, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new InvalidDataException(label + " geçersiz.");
    }

    private static void RelativeExe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || Path.IsPathRooted(value) || !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Başlatma dosyası oyun içindeki göreli bir EXE yolu olmalı.");
        foreach (var part in value.Replace('\\', '/').Split('/'))
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(part))
                throw new InvalidDataException("Başlatma dosyası yolu geçersiz.");
    }

    private static bool IsDeviceName(string value) => Regex.IsMatch(value.Split('.')[0], "\\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\\z", RegexOptions.IgnoreCase);
}
