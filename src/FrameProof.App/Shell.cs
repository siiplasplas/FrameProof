using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using FrameProof.App.Views;
using FrameProof.Core.Update;
using FrameProof.Core.Util;

namespace FrameProof.App;

/// <summary>
/// Kendi başlık çubuğu olan pencere: Windows'un beyaz çubuğu yerine uygulamanın koyu üst şeridi.
/// Sürükleme, çift tıkla büyütme, kenardan boyutlandırma ve Aero Snap Windows'ta kalır (WindowChrome).
/// </summary>
public sealed class MainWindow : Window
{
    private readonly ShellView _shell;

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
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = ShellView.TitleBarHeight,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        _shell = new ShellView();
        Content = _shell;
        SourceInitialized += (_, _) => Dwm.Style(this);
        StateChanged += (_, _) => FitMaximized();
        DpiChanged += (_, _) => FitMaximized();
        Loaded += async (_, _) =>
        {
            if (Environment.GetCommandLineArgs().Contains("--updated")) _shell.ShowUpdatedNote();
            var home = _shell.ShowAsync("home");
            await Task.Delay(2500);
            await _shell.CheckUpdatesAsync(manual: false);
            await home;
        };
    }

    /// <summary>Büyütülmüş pencere ekranın her kenarından çerçeve kalınlığı kadar taşar; içerik kırpılmasın diye içeri çek.</summary>
    private void FitMaximized()
    {
        if (WindowState != WindowState.Maximized) { _shell.Margin = new Thickness(0); return; }
        var dpi = VisualTreeHelper.GetDpi(this);
        var px = Dwm.FrameOverhang((uint)Math.Round(dpi.PixelsPerInchX));
        _shell.Margin = new Thickness(px / dpi.DpiScaleX);
    }

    private static class Dwm
    {
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);

        /// <summary>Koyu pencere kenarlığı ve yuvarlak köşe (Windows 11). Windows 10'da sessizce yok sayılır.</summary>
        public static void Style(Window w)
        {
            var h = new WindowInteropHelper(w).Handle;
            try
            {
                int dark = 1; DwmSetWindowAttribute(h, 20, ref dark, 4);          // DWMWA_USE_IMMERSIVE_DARK_MODE
                int round = 2; DwmSetWindowAttribute(h, 33, ref round, 4);        // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND
                int border = 0x00332822; DwmSetWindowAttribute(h, 34, ref border, 4); // DWMWA_BORDER_COLOR (#222833, BGR)
            }
            catch { }
        }

        public static double FrameOverhang(uint dpi)
        {
            try { return GetSystemMetricsForDpi(32, dpi) + GetSystemMetricsForDpi(92, dpi); } // SM_CXSIZEFRAME + SM_CXPADDEDBORDER
            catch { return 8 * dpi / 96.0; }
        }
    }
}

/// <summary>Sol menü + üst şerit + sayfa alanı.</summary>
public sealed class ShellView : Grid
{
    public const double TitleBarHeight = 40;

    /// <summary>Sol üst marka: yalnız yazı, "Frame" beyaz, "Proof" yeşil. Altında ince çizgi.</summary>
    private static FrameworkElement Brand()
    {
        var t = new TextBlock
        {
            FontSize = 21, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), VerticalAlignment = VerticalAlignment.Center,
        };
        t.Inlines.Add(new System.Windows.Documents.Run("Frame") { Foreground = Ui.B("Text") });
        t.Inlines.Add(new System.Windows.Documents.Run("Proof") { Foreground = Ui.B("Accent") });
        AutomationProperties.SetName(t, "FrameProof");
        return new Border
        {
            Height = 64, Padding = new Thickness(22, 0, 12, 0), Margin = new Thickness(0, 0, 0, 12),
            BorderBrush = Ui.B("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Child = t,
        };
    }

    private readonly ContentControl _host = new();
    private readonly Dictionary<string, Page> _pages = new();
    private readonly Dictionary<string, RadioButton> _nav = new();
    private readonly TextBlock _maxGlyph = new();

    private static readonly (string Id, string Label, string Glyph)[] Items =
    {
        ("home", "Genel bakış", ""),
        ("scan", "Tarama", ""),
        ("clean", "Temizlik", ""),
        ("tweaks", "Ayarlar", ""),
        ("startup", "Başlangıç", ""),
        ("measure", "FPS Ölçümü", ""),
        ("journal", "Geri al", ""),
    };

    public ShellView()
    {
        Background = Ui.B("Bg");
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- Sol menü ----
        var side = new DockPanel { LastChildFill = false };
        var logo = Brand();

        var nav = new StackPanel { Margin = new Thickness(12, 0, 12, 0) };
        foreach (var (id, label, glyph) in Items)
        {
            var rb = new RadioButton { Content = label, Tag = glyph, GroupName = "nav", Style = (Style)Application.Current.Resources["Nav"] };
            rb.Checked += async (_, _) => await ShowAsync(id);
            _nav[id] = rb;
            nav.Children.Add(rb);
        }
        var top = Ui.V(logo, nav);
        DockPanel.SetDock(top, Dock.Top);
        side.Children.Add(top);
        var bottom = StatusCard();
        DockPanel.SetDock(bottom, Dock.Bottom);
        side.Children.Add(bottom);
        var sideBorder = new Border { Background = Ui.B("Side"), BorderBrush = Ui.B("Line"), BorderThickness = new Thickness(0, 0, 1, 0), Child = side };
        Children.Add(sideBorder);

        // ---- Sağ taraf: üst şerit, güncelleme notu, sayfa ----
        var right = new DockPanel();
        var bar = TitleBar();
        DockPanel.SetDock(bar, Dock.Top);
        right.Children.Add(bar);

        _updateBar.Background = Ui.B("AccentSoft");
        _updateBar.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x3D, 0xDC, 0x97));
        _updateBar.BorderThickness = new Thickness(1);
        _updateBar.CornerRadius = new CornerRadius(10);
        _updateBar.Padding = new Thickness(16, 10, 12, 10);
        _updateBar.Margin = new Thickness(36, 4, 36, 4);
        _updateBar.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_updateBar, Dock.Top);
        right.Children.Add(_updateBar);
        right.Children.Add(_host);
        SetColumn(right, 1);
        Children.Add(right);

        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } w)
            {
                w.StateChanged += (_, _) => UpdateMaxGlyph(w);
                UpdateMaxGlyph(w);
            }
        };
    }

    /// <summary>Menünün altında: çalışma modu, sürüm, güncelleme denetimi.</summary>
    private UIElement StatusCard()
    {
        var elevated = Admin.IsElevated;
        var mode = Ui.H(
            Ui.Icon(elevated ? "" : "", 14, elevated ? "Accent" : "Warn"),
            Ui.T(elevated ? "Yönetici modu" : "Sınırlı mod", 12, elevated ? "Accent" : "Warn", FontWeights.SemiBold, new Thickness(8, 0, 0, 0)));
        var card = new Border
        {
            Background = Ui.B("Panel"),
            BorderBrush = Ui.B("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(12, 0, 12, 14),
            Child = Ui.V(
                mode,
                Ui.T($"v{Updater.CurrentText} · açık kaynak · MIT", 11, "Dim", margin: new Thickness(0, 6, 0, 0)),
                LinkButton("Güncellemeleri denetle", () => CheckUpdatesAsync(manual: true))),
        };
        return card;
    }

    /// <summary>Pencerenin üst şeridi: sürüklenebilir boşluk, gerekiyorsa yönetici uyarısı, pencere düğmeleri.</summary>
    private UIElement TitleBar()
    {
        var dock = new DockPanel { Height = TitleBarHeight, LastChildFill = false, Background = Brushes.Transparent };

        var caps = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        caps.Children.Add(Caption("", "Simge durumuna küçült", w => SystemCommands.MinimizeWindow(w)));
        _maxGlyph.Text = "";
        var max = Caption("", "Ekranı kapla", w =>
        {
            if (w.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(w);
            else SystemCommands.MaximizeWindow(w);
        });
        max.Content = CaptionGlyph(_maxGlyph);
        caps.Children.Add(max);
        var close = Caption("", "Kapat", w => SystemCommands.CloseWindow(w));
        close.Style = (Style)Application.Current.Resources["CaptionClose"];
        caps.Children.Add(close);
        DockPanel.SetDock(caps, Dock.Right);
        dock.Children.Add(caps);

        if (!Admin.IsElevated)
        {
            var open = Ui.Btn("Yönetici olarak aç", () =>
            {
                if (App.Relaunch(Environment.GetCommandLineArgs().Skip(1).ToArray())) Application.Current.Shutdown();
            });
            open.MinHeight = 0;
            open.Padding = new Thickness(10, 3, 10, 4);
            open.FontSize = 12;
            open.Background = Ui.B("Warn");
            open.BorderBrush = Ui.B("Warn");
            open.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x16, 0x04));
            WindowChrome.SetIsHitTestVisibleInChrome(open, true);
            var text = Ui.T("Sınırlı mod: bazı temizlik ve ayarlar kapalı, FPS ölçümü çalışmaz", 12, "Warn");
            text.TextWrapping = TextWrapping.NoWrap;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.VerticalAlignment = VerticalAlignment.Center;
            var pill = new Border
            {
                Background = Ui.B("WarnSoft"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 3, 3, 3),
                Margin = new Thickness(0, 6, 10, 0),
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = "FrameProof yönetici izni olmadan çalışıyor. Sistem temizliği, Windows ayarları ve FPS ölçümü için yönetici izni gerekir.",
            };
            var row = new DockPanel();
            DockPanel.SetDock(open, Dock.Right);
            open.Margin = new Thickness(10, 0, 0, 0);
            row.Children.Add(open);
            var icon = Ui.Icon("", 13, "Warn");
            icon.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(text);
            pill.Child = row;
            DockPanel.SetDock(pill, Dock.Right);
            dock.Children.Add(pill);
        }
        return dock;
    }

    private static Button Caption(string glyph, string tip, Action<Window> act)
    {
        var b = new Button { Style = (Style)Application.Current.Resources["Caption"], ToolTip = tip };
        b.Content = CaptionGlyph(new TextBlock { Text = glyph });
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        b.Click += (s, _) => { if (Window.GetWindow((DependencyObject)s) is { } w) act(w); };
        return b;
    }

    /// <summary>
    /// Örtük TextBlock stili yazı tipini Segoe UI'ye çeker; simge yazı tipini açıkça ver,
    /// rengi düğmeye bağla ki üstüne gelince stil tetikleyicisi rengi değiştirebilsin.
    /// </summary>
    private static TextBlock CaptionGlyph(TextBlock t)
    {
        t.FontFamily = (FontFamily)Application.Current.Resources["IconFont"];
        t.FontSize = 10;
        t.TextWrapping = TextWrapping.NoWrap;
        t.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1),
        });
        return t;
    }

    private void UpdateMaxGlyph(Window w) =>
        _maxGlyph.Text = w.WindowState == WindowState.Maximized ? "" : "";

    // ---- Güncelleme ----

    private readonly Border _updateBar = new();
    private bool _updateBusy;

    private static Button LinkButton(string text, Func<Task> onClick)
    {
        var b = Ui.Btn(text, onClick);
        b.Style = null;
        b.Background = Brushes.Transparent;
        b.BorderThickness = new Thickness(0);
        b.FontSize = 11;
        b.Padding = new Thickness(0);
        b.Margin = new Thickness(0, 6, 0, 0);
        b.HorizontalAlignment = HorizontalAlignment.Left;
        b.Cursor = System.Windows.Input.Cursors.Hand;
        b.Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        var tb = new TextBlock { Text = text, FontSize = 11, Foreground = Ui.B("Muted"), TextDecorations = TextDecorations.Underline };
        b.MouseEnter += (_, _) => tb.Foreground = Ui.B("Text");
        b.MouseLeave += (_, _) => tb.Foreground = Ui.B("Muted");
        b.Content = tb;
        return b;
    }

    private static UIElement UpdateIcon() { var i = Ui.Icon("", 15, "Accent"); i.Margin = new Thickness(0, 0, 12, 0); return i; }

    public void ShowUpdatedNote()
    {
        _updateBar.Child = Ui.Row(
            Ui.H(UpdateIcon(), Ui.T($"FrameProof v{Updater.CurrentText} sürümüne güncellendi.", 13, "Accent", FontWeights.SemiBold)),
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
        var left = new DockPanel();
        var icon = UpdateIcon();
        DockPanel.SetDock(icon, Dock.Left);
        left.Children.Add(icon);
        left.Children.Add(text);
        _updateBar.Child = Ui.Row(left, Ui.Btn("Güncelle", () => InstallAsync(info, text), primary: true));
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
        Enter(page);
        if (page.ReloadOnShow) await page.ReloadAsync();
        else await page.EnsureLoadedAsync();
    }

    /// <summary>Sayfa geçişi: kısa bir solma + 8 px yukarı kayma. Ekran görüntüsü modunda yok (yarım kare çıkmasın).</summary>
    private static void Enter(FrameworkElement page)
    {
        if (Screenshots.Active) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var shift = new TranslateTransform(0, 8);
        page.RenderTransform = shift;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
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
    protected readonly StackPanel Actions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    protected readonly TextBlock Subtitle;
    private Task? _load;
    private bool _stale;

    public virtual bool ReloadOnShow => _stale;

    public const double MaxContent = 1100;

    /// <summary>Öğeyi en fazla max genişliğe uzatır, fazlası sağda boş kalır (MaxWidth tek başına ortalardı).</summary>
    private static Grid Capped(UIElement e, double max)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = max });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Auto) });
        g.Children.Add(e);
        return g;
    }

    protected Page(ShellView shell, string title, string subtitle)
    {
        Shell = shell;
        Subtitle = Ui.Muted(subtitle, 13, new Thickness(0, 5, 0, 0));
        // İçerik genişliği sınırlı: ekranı kaplayınca satırlar 1900 piksele yayılıp okunmaz olmasın.
        var header = new DockPanel { Margin = new Thickness(36, 6, 36, 18) };
        DockPanel.SetDock(Actions, Dock.Right);
        Actions.Margin = new Thickness(24, 0, 0, 0);
        header.Children.Add(Actions);
        var titleText = new TextBlock { Text = title, FontSize = 28, FontWeight = FontWeights.Bold, Foreground = Ui.B("Text"), FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") };
        header.Children.Add(Ui.V(titleText, Subtitle));

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = Capped(Body, MaxContent),
            Padding = new Thickness(36, 0, 30, 32),
        };
        var dock = new DockPanel();
        var head = Capped(header, MaxContent + 72);
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
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
        var spinner = Ui.Icon("", 16, "Accent");
        spinner.Margin = new Thickness(0, 0, 10, 0);
        Body.Children.Add(Ui.Card(Ui.V(
            Ui.H(spinner, Ui.T(text, 14, weight: FontWeights.SemiBold)),
            new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 14, 0, 0) }), padding: 18));
    }
}
