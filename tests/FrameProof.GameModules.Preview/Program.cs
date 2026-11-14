using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FrameProof.App;
using FrameProof.App.Views;
using FrameProof.Core.Library;
using FrameProof.Core.Downloads;
using FrameProof.Core.Installation;
using FrameProof.Core.Packages;

namespace FrameProof.GameModules.Preview;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length is < 2 or > 3) throw new ArgumentException("Usage: Preview <isolated-library.json> <output.png> [--install]");
        // Do not call App.Run(): production OnStartup elevates and starts updates.
        var app = new FrameProof.App.App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        FrameProof.App.Page page = args.ElementAtOrDefault(2) == "--install"
            ? CreateInstallPage(Path.GetFullPath(args[0]))
            : new GamesPage(new ShellView(), new GameLibraryStore(Path.GetFullPath(args[0])));
        page.Background = Ui.B("Bg");
        var window = new Window { Title = "FrameProof — Oyunlarım önizleme", Width = 960, Height = 720,
            Background = Ui.B("Bg"), Content = page, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        app.MainWindow = window;
        window.Show();
        _ = window.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await page.EnsureLoadedAsync();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)page.ActualWidth, (int)page.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(page);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.GetFullPath(args[1]));
                encoder.Save(file);
                Console.WriteLine(Path.GetFullPath(args[1]));
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            finally { window.Close(); app.Shutdown(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
        });
        Dispatcher.Run();
    }

    private static GameInstallPage CreateInstallPage(string libraryPath)
    {
        var root = Path.Combine(Path.GetDirectoryName(libraryPath)!, "install-preview");
        var downloader = new HttpPackageDownloader(new HttpClient(new PreviewOnlyHandler()), Path.Combine(root, "cache"));
        var installer = new LocalPackageInstaller(Path.Combine(root, "transactions"), _ => throw new InvalidOperationException("Önizleme kurulum yapmaz."));
        var coordinator = new GameInstallCoordinator(downloader, installer, new GameLibraryStore(libraryPath), Path.Combine(root, "jobs"));
        return new GameInstallPage(new ShellView(), new[]
        {
            new GameInstallRecipe
            {
                CatalogId = "ornek-oyun", Name = "Örnek oyun", GameVersion = "1.0", FixVersion = "1.0-coop",
                GamePackage = new("sample-game", new Uri("https://example.test/game.zip"), new string('a', 64), 100),
                FixPackage = new("sample-fix", new Uri("https://example.test/fix.zip"), new string('b', 64), 50),
                LaunchRelativePath = "game.exe", MaximumGameExpandedBytes = 1000, MaximumFixExpandedBytes = 500,
            },
        }, coordinator, Path.Combine(root, "games"), new GameLibraryStore(libraryPath));
    }

    private sealed class PreviewOnlyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Önizleme ağ isteği yapmaz.");
    }
}
