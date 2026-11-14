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
        T(text.ToUpper(Core.Util.Fmt.Tr), 11, "Muted", FontWeights.SemiBold, new Thickness(2, 18, 0, 8));

    public static Border Card(UIElement child, Thickness? margin = null, double padding = 16) => new()
    {
        Background = B("Panel"),
        BorderBrush = B("Line"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(padding),
        Margin = margin ?? new Thickness(0, 0, 0, 10),
        Child = Free(child),
    };

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

    public static Border Badge(string text, string brush) => new()
    {
        BorderBrush = B(brush),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(8, 1, 8, 2),
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = T(text, 11, brush),
    };

    public static void Error(Exception ex)
    {
        FrameProof.Core.Util.ErrorLog.Write("Ui.Error", ex);
        MessageBox.Show(Application.Current?.MainWindow!, ex.Message, "FrameProof", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public static void Info(string text) =>
        MessageBox.Show(Application.Current?.MainWindow!, text, "FrameProof", MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(string text, string title = "Emin misin?") =>
        MessageBox.Show(Application.Current?.MainWindow!, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
}
