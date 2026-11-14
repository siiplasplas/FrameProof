using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FrameProof.App.Views;
using FrameProof.Core.Update;
using FrameProof.Core.Util;

namespace FrameProof.App;

public sealed class MainWindow : Window
{
    public MainWindow()
    {
        Title = "FrameProof";
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/icon.ico"));
        Width = 1180;
        Height = 780;
        MinWidth = 940;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.B("Bg");
        var shell = new ShellView();
        Content = shell;
        Loaded += async (_, _) =>
        {
            if (Environment.GetCommandLineArgs().Contains("--updated")) shell.ShowUpdatedNote();
            var home = shell.ShowAsync("home");
            await Task.Delay(2500);
            await shell.CheckUpdatesAsync(manual: false);
            await home;
        };
    }
}

/// <summary>Sol menü + sayfa alanı.</summary>
public sealed class ShellView : Grid
{
    /// <summary>Logo işareti (site/logo.svg ile aynı çizim): yeşil kare içinde yuvarlak hatlı F.</summary>
    private static FrameworkElement LogoMark(double size)
    {
        var c = new Canvas { Width = 64, Height = 64 };
        c.Children.Add(new System.Windows.Shapes.Rectangle { Width = 64, Height = 64, RadiusX = 16, RadiusY = 16, Fill = Ui.B("Accent") });
        c.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M24,47 V25 A7,7 0 0 1 31,18 H44 M24,33 H37"),
            Stroke = new SolidColorBrush(Color.FromRgb(0x0D, 0x0F, 0x13)), StrokeThickness = 8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        });
        return new Viewbox { Width = size, Height = size, Child = c, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    }

    private readonly ContentControl _host = new();
    private readonly Dictionary<string, Page> _pages = new();
    private readonly Dictionary<string, RadioButton> _nav = new();

    private static readonly (string Id, string Label)[] Items =
    {
        ("home", "Genel bakış"),
        ("scan", "Tarama"),
        ("clean", "Temizlik"),
        ("tweaks", "Ayarlar"),
        ("startup", "Başlangıç"),
        ("measure", "FPS Ölçümü"),
        ("journal", "Geri al"),
    };

    public ShellView()
    {
        Background = Ui.B("Bg");
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Sol menü
        var side = new DockPanel { Background = Ui.B("Panel"), LastChildFill = false };
        var word = new TextBlock { Text = "FrameProof", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Ui.B("Text"), VerticalAlignment = VerticalAlignment.Center };
        var logo = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 22, 0, 4) };
        logo.Children.Add(LogoMark(28));
        logo.Children.Add(word);
        var top = Ui.V(logo, Ui.Muted("Ölç, düzelt, kanıtla", 12, new Thickness(22, 0, 0, 22)));
        var nav = new StackPanel { Margin = new Thickness(10, 0, 10, 0) };
        foreach (var (id, label) in Items)
        {
            var rb = new RadioButton { Content = label, GroupName = "nav", Style = (Style)Application.Current.Resources["Nav"] };
            rb.Checked += async (_, _) => await ShowAsync(id);
            _nav[id] = rb;
            nav.Children.Add(rb);
        }
        top.Children.Add(nav);
        DockPanel.SetDock(top, Dock.Top);
        side.Children.Add(top);

        var bottom = Ui.V(
            Ui.T(Admin.IsElevated ? "● Yönetici modu" : "● Sınırlı mod", 12, Admin.IsElevated ? "Accent" : "Warn"),
            Ui.Muted($"v{Updater.CurrentText} · açık kaynak · MIT", 11, new Thickness(0, 4, 0, 0)),
            LinkButton("Güncellemeleri denetle", () => CheckUpdatesAsync(manual: true)));
        bottom.Margin = new Thickness(22, 0, 0, 20);
        DockPanel.SetDock(bottom, Dock.Bottom);
        side.Children.Add(bottom);
        Children.Add(side);

        // Sağ taraf
        var right = new DockPanel();
        if (!Admin.IsElevated)
        {
            var banner = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x22, 0x10)),
                BorderBrush = Ui.B("Warn"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(32, 10, 32, 10),
                Child = Ui.Row(
                    Ui.T("Yönetici izni olmadan çalışıyor. Bazı temizlik ve ayarlar kapalı, FPS ölçümü çalışmaz.", 13, "Warn"),
                    Ui.Btn("Yönetici olarak aç", () =>
                    {
                        if (App.Relaunch(Environment.GetCommandLineArgs().Skip(1).ToArray())) Application.Current.Shutdown();
                    })),
            };
            DockPanel.SetDock(banner, Dock.Top);
            right.Children.Add(banner);
        }
        _updateBar.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x2A, 0x20));
        _updateBar.BorderBrush = Ui.B("Accent");
        _updateBar.BorderThickness = new Thickness(0, 0, 0, 1);
        _updateBar.Padding = new Thickness(32, 10, 32, 10);
        _updateBar.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_updateBar, Dock.Top);
        right.Children.Add(_updateBar);
        right.Children.Add(_host);
        SetColumn(right, 1);
        Children.Add(right);
    }

    // ---- Güncelleme ----

    private readonly Border _updateBar = new();
    private bool _updateBusy;

    private static Button LinkButton(string text, Func<Task> onClick)
    {
        var b = Ui.Btn(text, onClick);
        b.Style = null;
        b.Background = Brushes.Transparent;
        b.BorderThickness = new Thickness(0);
        b.Foreground = Ui.B("Muted");
        b.FontSize = 11;
        b.Padding = new Thickness(0);
        b.Margin = new Thickness(0, 4, 0, 0);
        b.HorizontalAlignment = HorizontalAlignment.Left;
        b.Cursor = System.Windows.Input.Cursors.Hand;
        b.Content = new TextBlock { Text = text, TextDecorations = TextDecorations.Underline };
        return b;
    }

    public void ShowUpdatedNote()
    {
        _updateBar.Child = Ui.Row(
            Ui.T($"FrameProof v{Updater.CurrentText} sürümüne güncellendi.", 13, "Accent"),
            Ui.Btn("Tamam", () => { _updateBar.Visibility = Visibility.Collapsed; return Task.CompletedTask; }));
        _updateBar.Visibility = Visibility.Visible;
    }

    /// <summary>Sitedeki sürüme bakar. Açılışta sessizce, menüden elle çağrılınca sonucu söyler.</summary>
    public async Task CheckUpdatesAsync(bool manual)
    {
        if (_updateBusy) return;
        var r = await Updater.CheckAsync();
        if (r.Info == null)
        {
            if (manual) Ui.Info(r.Error != null ? "Güncelleme denetlenemedi: " + r.Error : $"FrameProof güncel (v{Updater.CurrentText}).");
            return;
        }
        var info = r.Info;
        var text = Ui.T($"Yeni sürüm var: v{info.Version.TrimStart('v')} (sende v{Updater.CurrentText})." +
                        (string.IsNullOrWhiteSpace(info.Notes) ? "" : "  " + info.Notes), 13, "Accent");
        text.TextWrapping = TextWrapping.Wrap;
        _updateBar.Child = Ui.Row(text, Ui.Btn("Güncelle", () => InstallAsync(info, text), primary: true));
        _updateBar.Visibility = Visibility.Visible;
    }

    private async Task InstallAsync(UpdateInfo info, TextBlock text)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        try
        {
            var prog = new Progress<(long Done, long Total)>(p => text.Text = $"İndiriliyor… {Fmt.Bytes(p.Done)} / {Fmt.Bytes(p.Total)}");
            var path = await Task.Run(() => Updater.DownloadAsync(info, prog, CancellationToken.None));
            text.Text = "Yeni sürüm indirildi ve doğrulandı.";
            if (!Ui.Confirm("Yeni sürüm indirildi ve doğrulandı. FrameProof kapanıp yeni sürümle yeniden açılacak.\n\nŞimdi güncellensin mi?", "Güncelleme"))
            {
                text.Text = "Güncelleme ertelendi. İstediğin zaman 'Güncelle'ye bas.";
                return;
            }
            Updater.ApplyAndRestart(path, keepLimitedMode: !Admin.IsElevated);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Update", ex);
            text.Text = "Güncellenemedi: " + ex.Message + "  Yeni sürümü siteden de indirebilirsin: getframeproof.pages.dev";
        }
        finally { _updateBusy = false; }
    }

    private Page Create(string id) => id switch
    {
        "home" => new HomePage(this),
        "scan" => new ScanPage(this),
        "clean" => new CleanPage(this),
        "tweaks" => new TweaksPage(this),
        "startup" => new StartupPage(this),
        "measure" => new MeasurePage(this),
        "journal" => new JournalPage(this),
        _ => throw new ArgumentException(id),
    };

    public async Task ShowAsync(string id)
    {
        if (!_pages.TryGetValue(id, out var page))
            _pages[id] = page = Create(id);
        if (_nav[id].IsChecked != true) _nav[id].IsChecked = true; // Checked tekrar bu metodu çağırır; sorun değil
        if (_host.Content == page) { await page.EnsureLoadedAsync(); return; }
        _host.Content = page;
        if (page.ReloadOnShow) await page.ReloadAsync();
        else await page.EnsureLoadedAsync();
    }

    /// <summary>Test için: sayfayı hemen yeniden yükler (yükleme sürerken çağrılabilir).</summary>
    public Task ReloadPage(string id) => _pages.TryGetValue(id, out var p) ? p.ReloadAsync() : Task.CompletedTask;

    /// <summary>Başka bir sayfadaki değişiklikten sonra o sayfanın yeniden yüklenmesini ister.</summary>
    public void Invalidate(string id)
    {
        if (_pages.TryGetValue(id, out var p)) p.MarkStale();
        // Ana ekran her şeyi özetler: başka bir sekmedeki her değişiklik onu da eskitir.
        if (id != "home" && _pages.TryGetValue("home", out var h)) h.MarkStale();
    }
}

/// <summary>Başlık + sağ üstte eylemler + kaydırılabilir gövde.</summary>
public abstract class Page : UserControl
{
    protected readonly ShellView Shell;
    protected readonly StackPanel Body = new();
    protected readonly StackPanel Actions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
    protected readonly TextBlock Subtitle;
    private Task? _load;
    private bool _stale;

    public virtual bool ReloadOnShow => _stale;

    protected Page(ShellView shell, string title, string subtitle)
    {
        Shell = shell;
        Subtitle = Ui.Muted(subtitle, 13, new Thickness(0, 4, 0, 0));
        var header = new DockPanel { Margin = new Thickness(32, 26, 32, 14) };
        DockPanel.SetDock(Actions, Dock.Right);
        header.Children.Add(Actions);
        header.Children.Add(Ui.V(Ui.T(title, 24, weight: FontWeights.SemiBold), Subtitle));

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = Body,
            Padding = new Thickness(32, 0, 32, 28),
        };
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(scroll);
        Content = dock;
    }

    public Task EnsureLoadedAsync() => _load ??= SafeLoad(null);

    public Task ReloadAsync()
    {
        _stale = false;
        return _load = SafeLoad(_load);
    }

    public void MarkStale() => _stale = true;

    /// <summary>Yüklemenin sürdüğü sırada yeni bir yükleme istenirse bu iptal edilir. Sayfalar uzun döngülerde kontrol eder.</summary>
    protected CancellationToken LoadToken { get; private set; }
    private CancellationTokenSource? _loadCts;

    /// <summary>
    /// Yüklemeler asla üst üste binmez: yenisi eskisini iptal eder, bitmesini bekler, sonra başlar.
    /// (Eskiden ikisi aynı anda Body'yi ve satır listesini değiştiriyor, sayfa "Yüklenemedi" diyordu.)
    /// </summary>
    private async Task SafeLoad(Task? previous)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        if (previous != null) { try { await previous; } catch { } }
        if (cts.IsCancellationRequested) return; // bu arada daha yeni bir yükleme istendi; o devralır
        LoadToken = cts.Token;
        try { await LoadAsync(); }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            ErrorLog.Write($"{GetType().Name}.LoadAsync", ex);
            Body.Children.Clear();
            Body.Children.Add(Ui.Card(Ui.Row(
                Ui.V(Ui.T("Yüklenemedi: " + ex.Message, 13, "Bad"),
                     Ui.Muted("Ayrıntı şu dosyaya yazıldı: " + ErrorLog.File, 11, new Thickness(0, 4, 0, 0))),
                Ui.Btn("Tekrar dene", ReloadAsync)), padding: 14));
        }
    }

    protected abstract Task LoadAsync();

    protected void Loading(string text)
    {
        Body.Children.Clear();
        Body.Children.Add(Ui.Card(Ui.V(
            Ui.T(text, 14),
            new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 12, 0, 0) })));
    }
}
