namespace FrameProof.Core.Util;

/// <summary>Beklenmeyen hataların tam metnini veri klasöründeki hata.log'a yazar (en fazla ~256 KB tutar).</summary>
public static class ErrorLog
{
    private static readonly object Gate = new();

    public static string File => Path.Combine(AppPaths.Data, "hata.log");

    public static void Write(string where, Exception ex)
    {
        try
        {
            lock (Gate)
            {
                var f = File;
                if (System.IO.File.Exists(f) && new FileInfo(f).Length > 256 * 1024)
                {
                    var keep = System.IO.File.ReadAllText(f);
                    System.IO.File.WriteAllText(f, keep[^(64 * 1024)..]);
                }
                var ver = typeof(ErrorLog).Assembly.GetName().Version;
                System.IO.File.AppendAllText(f, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{ver} {where}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch { /* log yazılamazsa uygulama yine çalışsın */ }
    }
}
