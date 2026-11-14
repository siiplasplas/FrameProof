using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FrameProof.App;

/// <summary>--shot &lt;klasör&gt;: pencere açmadan her sekmeyi PNG'ye çizer. Test ve README görselleri için.</summary>
public static class Screenshots
{
    public static async Task Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var shell = new ShellView();
        const int w = 1180;
        // home_full: ana ekranın tamamı (kontrol için uzun çizim)
        foreach (var (name, h) in new[] { ("home", 780), ("home_full", 2700), ("scan", 780), ("clean", 780), ("clean_full", 1100), ("tweaks", 780), ("startup", 780), ("measure", 780), ("journal", 780) })
        {
            var id = name.EndsWith("_full") ? name[..^5] : name;
            if (name == "clean" && Environment.GetEnvironmentVariable("FRAMEPROOF_STRESS") == "1")
            {
                // Yükleme sürerken üst üste yeniden yükleme: eskiden "Yüklenemedi" ile biterdi.
                var first = shell.ShowAsync(id);
                await Task.Delay(30);
                var a = shell.ReloadPage(id);
                await Task.Delay(700);
                var b = shell.ReloadPage(id);
                var c = shell.ReloadPage(id);
                await Task.WhenAll(first, a, b, c);
            }
            await shell.ShowAsync(id);
            await Task.Delay(300);
            shell.Measure(new Size(w, h));
            shell.Arrange(new Rect(0, 0, w, h));
            shell.UpdateLayout();
            await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle(Ui.B("Bg"), null, new Rect(0, 0, w, h));
            bmp.Render(bg);
            bmp.Render(shell);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            await using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }
    }
}
