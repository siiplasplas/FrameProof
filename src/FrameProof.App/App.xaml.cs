using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using FrameProof.Core.Util;

namespace FrameProof.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            Ui.Error(ex.Exception);
            ex.Handled = true;
        };

        var args = e.Args;
        int shot = Array.IndexOf(args, "--shot");
        if (shot >= 0)
        {
            // Ekran görüntüsü modu: pencere göstermeden her sekmeyi PNG'ye çizer (test/README için).
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await Screenshots.Run(args.ElementAtOrDefault(shot + 1) ?? "shots"); }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            Shutdown();
            return;
        }

        FrameProof.Core.Update.Updater.CleanupLeftovers();

        if (!Admin.IsElevated && !args.Contains("--no-elevate") && Relaunch(args))
        {
            Shutdown();
            return;
        }

        var w = new MainWindow();
        MainWindow = w;
        w.Show();
    }

    /// <summary>Yönetici olarak yeniden başlatır. Kullanıcı UAC'yi iptal ederse false döner.</summary>
    public static bool Relaunch(string[] args)
    {
        try
        {
            var a = args.Where(x => x != "--no-elevate").Select(x => x.Contains(' ') ? $"\"{x}\"" : x).Append("--no-elevate");
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, string.Join(" ", a)) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // iptal edildi: sınırlı modda devam
        }
        catch
        {
            return false;
        }
    }
}
