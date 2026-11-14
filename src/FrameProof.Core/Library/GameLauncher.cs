using System.Diagnostics;
using FrameProof.Core.Util;

namespace FrameProof.Core.Library;

public static class GameLauncher
{
    public static GameLaunchProfile Validate(GameLaunchProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var exe = LibraryPaths.Normalize(profile.ExecutablePath);
        if (!Path.GetExtension(exe).Equals(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe))
            throw new FileNotFoundException("Oyunun mevcut .exe dosyasını seçin.", exe);
        var directory = string.IsNullOrWhiteSpace(profile.WorkingDirectory)
            ? Path.GetDirectoryName(exe)! : LibraryPaths.Normalize(profile.WorkingDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Oyunun çalışma klasörü bulunamadı: " + directory);
        if (profile.Arguments is null || profile.Arguments.Any(a => a is null || a.Contains('\0')))
            throw new ArgumentException("Başlatma parametreleri geçersiz.", nameof(profile));
        return profile with { ExecutablePath = exe, WorkingDirectory = directory, Arguments = profile.Arguments.ToArray() };
    }

    /// <summary>Never inherits FrameProof's elevated token. A future desktop broker can implement unelevated launch.</summary>
    public static Process Start(GameEntry game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.InstallationPending) throw new InvalidOperationException("Oyun kurulumu veya güncellemesi sürüyor. Önce işlemi tamamlayın ya da geri alın.");
        if (game.Launch is null) throw new InvalidOperationException("Önce oyunun başlatma dosyasını seçin.");
        var profile = Validate(game.Launch);
        if (Admin.IsElevated)
            throw new InvalidOperationException("Oyun yönetici yetkisiyle başlatılmadı. FrameProof'u normal kullanıcı olarak açın veya oyunu kendi başlatıcısından çalıştırın.");
        var info = new ProcessStartInfo(profile.ExecutablePath)
        {
            WorkingDirectory = profile.WorkingDirectory,
            UseShellExecute = false
        };
        foreach (var argument in profile.Arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("Oyun işlemi başlatılamadı.");
    }
}
