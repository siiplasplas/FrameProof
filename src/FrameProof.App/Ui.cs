using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FrameProof.Core.Scan;

namespace FrameProof.App;

/// <summary>Kod ile arayüz kurmak için küçük yardımcılar.</summary>
public static class Ui
{
    public static Brush B(string key) => (Brush)Application.Current.Resources[key];

    public static TextBlock T(string text, double size = 13, string brush = "Text", FontWeight? weight = null, Thickness? margin = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = B(brush),
        FontWeight = weight ?? FontWeights.Normal,
        Margin = margin ?? new Thickness(0),
    };

    public static TextBlock Muted(string text, double size = 12, Thickness? margin = null) => T(text, size, "Muted", margin: margin);

    public static TextBlock Section(string text) =>
        T(text.ToUpper(Core.Util.Fmt.Tr), 11, "Muted", FontWeights.Bold, new Thickness(2, 22, 0, 10));

    public static Border Card(UIElement child, Thickness? margin = null, double padding = 16) => new()
    {
        Background = B("CardFill"),
        BorderBrush = B("CardEdge"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(padding + 2, padding, padding + 2, padding),
        Margin = margin ?? new Thickness(0, 0, 0, 10),
        Child = Free(child),
    };

    /// <summary>Simge yazı tipinden tek karakter (Segoe Fluent Icons, Windows 10'da Segoe MDL2 Assets).</summary>
    public static TextBlock Icon(string glyph, double size = 16, string brush = "Muted") => new()
    {
        Text = glyph,
        FontFamily = (FontFamily)Application.Current.Resources["IconFont"],
        FontSize = size,
        Foreground = B(brush),
        TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Kart içinde ince ayraç.</summary>
    public static Border Divider(double top = 12, double bottom = 8) =>
        new() { Height = 1, Background = B("Line"), Margin = new Thickness(0, top, 0, bottom) };

    /// <summary>
    /// Sayfalar yeniden yüklenirken alan olarak tutulan öğeler (özet yazısı, düğme) hâlâ eski panele bağlı olur;
    /// WPF bir öğeyi iki yere koymaya izin vermez ("zaten başka bir öğenin mantıksal alt öğesidir"). Önce eski yerinden ayır.
    /// </summary>
    public static T Free<T>(T e) where T : UIElement
    {
        switch ((e as FrameworkElement)?.Parent)
        {
            case Panel p: p.Children.Remove(e); break;
            case Decorator d when d.Child == e: d.Child = null; break;
            case ContentControl c when c.Content == e: c.Content = null; break;
        }
        return e;
    }

    public static StackPanel V(params UIElement?[] items)
    {
        var s = new StackPanel();
        foreach (var i in items) if (i != null) s.Children.Add(Free(i));
        return s;
    }

    public static StackPanel H(params UIElement?[] items)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) if (i != null) s.Children.Add(Free(i));
        return s;
    }

    /// <summary>Solda esnek içerik, sağda sabit genişlikli öğe.</summary>
    public static Grid Row(UIElement left, UIElement? right, double gap = 16)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Free(left));
        if (right != null)
        {
            Free(right);
            if (right is FrameworkElement fe) { fe.Margin = new Thickness(gap, 0, 0, 0); fe.VerticalAlignment = VerticalAlignment.Center; }
            Grid.SetColumn(right, 1);
            g.Children.Add(right);
        }
        return g;
    }

    public static Button Btn(string text, Func<Task> onClick, bool primary = false)
    {
        var b = new Button { Content = text };
        if (primary) b.Style = (Style)Application.Current.Resources["Primary"];
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await onClick(); }
            catch (Exception ex) { Error(ex); }
            finally { b.IsEnabled = true; }
        };
        return b;
    }

    public static Button Btn(string text, Action onClick, bool primary = false) =>
        Btn(text, () => { onClick(); return Task.CompletedTask; }, primary);

    public static string SevKey(Severity s) => s switch
    {
        Severity.Good => "Accent",
        Severity.Warn => "Warn",
        Severity.Bad => "Bad",
        _ => "Info",
    };

    public static Ellipse Dot(string brush, double size = 8) => new()
    {
        Width = size,
        Height = size,
        Fill = B(brush),
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 6, 12, 0),
    };

    public static Border Badge(string text, string brush)
    {
        var tb = T(text, 11, brush, FontWeights.SemiBold);
        tb.TextWrapping = TextWrapping.NoWrap;
        return new Border
        {
            Background = Application.Current.Resources[brush + "Soft"] as Brush ?? B("TextSoft"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2, 8, 3),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = tb,
        };
    }

    public static void Error(Exception ex)
    {
        FrameProof.Core.Util.ErrorLog.Write("Ui.Error", ex);
        Dialog.Show(ex.Message, "Bir sorun oldu", Dialog.Kind.Warning, confirm: false);
    }

    public static void Info(string text) => Dialog.Show(text, "FrameProof", Dialog.Kind.Info, confirm: false);

    /// <summary>Evet/Hayır. Varsayılan Hayır: Enter da Esc de Hayır demek.</summary>
    public static bool Confirm(string text, string title = "Emin misin?") => Dialog.Show(text, title, Dialog.Kind.Question, confirm: true);
}

/// <summary>Uygulamanın koyu temasında MessageBox yerine geçen modal pencere.</summary>
internal sealed class Dialog : Window
{
    public enum Kind { Info, Question, Warning }

    private bool _yes;

    public static bool Show(string text, string title, Kind kind, bool confirm)
    {
        var app = Application.Current;
        var main = app?.MainWindow;
        var d = new Dialog(text, title, kind, confirm);
        // WPF ilk açılan pencereyi ana pencere sayar; ana pencereden önce çıkan bir uyarı kapanınca uygulama da kapanmasın.
        if (app != null && app.MainWindow == d) app.MainWindow = main;
        if (main is { IsVisible: true } owner)
        {
            d.Owner = owner;
            d.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        d.ShowDialog();
        return d._yes;
    }

    private Dialog(string text, string title, Kind kind, bool confirm)
    {
        Title = title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var (glyph, brush) = kind switch
        {
            Kind.Question => ("", "Info"),
            Kind.Warning => ("", "Warn"),
            _ => ("", "Accent"),
        };
        var icon = Ui.Icon(glyph, 16, brush);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var badge = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(18),
            Background = Ui.B(brush + "Soft"), VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 14, 0), Child = icon,
        };
        var body = Ui.T(text, 13, margin: new Thickness(0, 8, 0, 0));
        body.LineHeight = 20;
        var scroll = new ScrollViewer { Content = body, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var head = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left);
        head.Children.Add(badge);
        head.Children.Add(Ui.V(Ui.T(title, 16, weight: FontWeights.SemiBold, margin: new Thickness(0, 6, 0, 0)), scroll));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (confirm)
        {
            var yes = new Button { Content = "Evet", MinWidth = 96, Style = (Style)Application.Current.Resources["Primary"] };
            yes.Click += (_, _) => { _yes = true; Close(); };
            var no = new Button { Content = "Hayır", MinWidth = 96, IsDefault = true, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
            no.Click += (_, _) => Close();
            buttons.Children.Add(yes);
            buttons.Children.Add(no);
            Loaded += (_, _) => no.Focus();
        }
        else
        {
            var ok = new Button { Content = "Tamam", MinWidth = 96, IsDefault = true, IsCancel = true };
            ok.Click += (_, _) => Close();
            buttons.Children.Add(ok);
            Loaded += (_, _) => ok.Focus();
        }
        var footer = new Border
        {
            Background = Ui.B("Side"), BorderBrush = Ui.B("Line"), BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(0, 0, 12, 12), Padding = new Thickness(22, 14, 22, 14), Child = buttons,
        };
        var content = new Border { Padding = new Thickness(22, 20, 26, 20), Child = head };
        DockPanel.SetDock(footer, Dock.Bottom);
        var dock = new DockPanel();
        dock.Children.Add(footer);
        dock.Children.Add(content);

        Content = new Border
        {
            Margin = new Thickness(20),
            Width = 480,
            Background = Ui.B("Panel"),
            BorderBrush = Ui.B("LineStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = dock,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.6, Color = Colors.Black },
        };
        MouseLeftButtonDown += (_, e) => { try { DragMove(); } catch (InvalidOperationException) { } };
    }
}
