namespace FrameProof.Core.Cleanup;

public sealed class CleanStats
{
    public long Bytes;
    public int Files;
    public int Skipped;
    public bool AccessDenied;
}

/// <summary>
/// Güvenli silme: sadece verilen klasörün İÇİNİ temizler, kök klasörü silmez,
/// sembolik bağlantı/junction'ların içine asla girmez, yeni dosyalara (cutoff) dokunmaz,
/// kilitli dosyaları atlar.
/// </summary>
public static class SafeDelete
{
    private static readonly string[] Forbidden = BuildForbidden();

    private static string[] BuildForbidden()
    {
        var list = new List<string>();
        void Add(string? p) { if (!string.IsNullOrWhiteSpace(p)) list.Add(Norm(p)); }
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(Environment.SystemDirectory);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(Path.Combine(profile, "Downloads"));
        Add(Path.GetDirectoryName(profile));
        return list.ToArray();
    }

    private static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\').ToLowerInvariant();

    /// <summary>İçeriği toplu silinebilecek bir klasör mü? Sürücü kökü ve sistem/kullanıcı ana klasörleri yasak.</summary>
    public static bool IsSafeRoot(string path)
    {
        string n;
        try { n = Norm(path); } catch { return false; }
        if (n.Length <= 3) return false;
        return !Forbidden.Contains(n);
    }

    private static bool HasWildcard(string pattern) => pattern.Contains('*') || pattern.Contains('?');

    public static void Measure(string root, string pattern, bool recursive, DateTime cutoffUtc, CleanStats s) =>
        Walk(root, pattern, recursive, cutoffUtc, s, delete: false, CancellationToken.None);

    public static void Delete(string root, string pattern, bool recursive, DateTime cutoffUtc, CleanStats s, CancellationToken ct) =>
        Walk(root, pattern, recursive, cutoffUtc, s, delete: true, ct);

    private static void Walk(string root, string pattern, bool recursive, DateTime cutoffUtc, CleanStats s, bool delete, CancellationToken ct)
    {
        DirectoryInfo rootDir;
        try
        {
            rootDir = new DirectoryInfo(root);
            if (!rootDir.Exists) return;
            if (rootDir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return; // kök bir bağlantıysa hiç girme
        }
        catch (UnauthorizedAccessException) { s.AccessDenied = true; return; }
        catch { return; }

        // Toplu (joker/özyinelemeli) silmede kök klasör güvenli listede olmalı. Tek dosya adı (örn. MEMORY.DMP) her yerde serbest.
        if ((recursive || HasWildcard(pattern)) && !IsSafeRoot(rootDir.FullName))
            throw new InvalidOperationException("Güvenlik: bu klasörün içi toplu silinemez: " + rootDir.FullName);

        var visited = new List<DirectoryInfo>();
        var stack = new Stack<DirectoryInfo>();
        stack.Push(rootDir);
        bool first = true;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            if (!first) visited.Add(dir);

            try
            {
                foreach (var f in dir.EnumerateFiles(pattern))
                {
                    ct.ThrowIfCancellationRequested();
                    HandleFile(f, cutoffUtc, s, delete);
                }
            }
            catch (UnauthorizedAccessException) { if (first) s.AccessDenied = true; else s.Skipped++; }
            catch (IOException) { s.Skipped++; }

            if (recursive)
            {
                try
                {
                    foreach (var d in dir.EnumerateDirectories())
                    {
                        if (d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            // Bağlantının içine girme. Silme modunda yalnızca bağlantının kendisini kaldır (hedefe dokunmaz).
                            if (delete)
                            {
                                try { d.Delete(false); } catch { s.Skipped++; }
                            }
                            continue;
                        }
                        stack.Push(d);
                    }
                }
                catch (UnauthorizedAccessException) { if (first) s.AccessDenied = true; else s.Skipped++; }
                catch (IOException) { s.Skipped++; }
            }
            first = false;
        }

        if (!delete) return;
        // Boş kalan eski alt klasörleri sil (en derinden başlayarak). Kök klasör kalır.
        foreach (var d in visited.OrderByDescending(d => d.FullName.Length))
        {
            try
            {
                d.Refresh();
                if (!d.Exists || d.LastWriteTimeUtc > cutoffUtc) continue;
                if (!d.EnumerateFileSystemInfos().Any()) d.Delete(false);
            }
            catch { }
        }
    }

    private static void HandleFile(FileInfo f, DateTime cutoffUtc, CleanStats s, bool delete)
    {
        try
        {
            var age = f.LastWriteTimeUtc > f.CreationTimeUtc ? f.LastWriteTimeUtc : f.CreationTimeUtc;
            if (age > cutoffUtc) return;
            bool link = f.Attributes.HasFlag(FileAttributes.ReparsePoint);
            long len = link ? 0 : f.Length;
            if (!delete)
            {
                s.Bytes += len;
                s.Files++;
                return;
            }
            if (f.IsReadOnly) f.IsReadOnly = false;
            f.Delete(); // bağlantıysa sadece bağlantı silinir
            s.Bytes += len;
            s.Files++;
        }
        catch
        {
            s.Skipped++;
        }
    }
}
