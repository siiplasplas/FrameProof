using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FrameProof.Core.Scan;
using FrameProof.Core.Tweaks;
using FrameProof.Core.Util;

namespace FrameProof.App.Views;

public sealed class ScanPage : Page
{
    public ScanPage(ShellView shell) : base(shell, "Sistem taraması", "Bilgisayarını oyun açısından kontrol eder. Tarama hiçbir şeyi değiştirmez.")
    {
        Actions.Children.Add(Ui.Btn("Yeniden tara", ReloadAsync));
    }

    protected override async Task LoadAsync()
    {
        Loading("Sistem taranıyor…");
        var report = await Task.Run(SystemScanner.Run);
        Render(report);
    }

    private void Render(ScanReport r)
    {
        Body.Children.Clear();

        // Özet şeridi
        var sum = new UniformGrid4();
        sum.Add(Stat(r.Count(Severity.Bad).ToString(), "kritik", "Bad"));
        sum.Add(Stat(r.Count(Severity.Warn).ToString(), "uyarı", "Warn"));
        sum.Add(Stat(r.Count(Severity.Info).ToString(), "bilgi", "Info"));
        sum.Add(Stat(r.Count(Severity.Good).ToString(), "iyi", "Accent"));
        Body.Children.Add(sum.Element);

        var guide = BiosGuide.Build(r);
        if (guide.Steps.Count > 0) AddBiosGuide(guide);

        var order = new[] { Severity.Bad, Severity.Warn, Severity.Info, Severity.Good };
        var titles = new Dictionary<Severity, string>
        {
            [Severity.Bad] = "Hemen düzelt",
            [Severity.Warn] = "Bakmaya değer",
            [Severity.Info] = "Bilgi",
            [Severity.Good] = "Yolunda",
        };
        foreach (var sev in order)
        {
            var list = r.Findings.Where(f => f.Severity == sev).ToList();
            if (list.Count == 0) continue;
            Body.Children.Add(Ui.Section(titles[sev]));
            foreach (var f in list) Body.Children.Add(FindingCard(f, Shell, ReloadAsync));
        }
    }

    private void AddBiosGuide(BiosGuideInfo g)
    {
        Body.Children.Add(Ui.Section("BIOS rehberi"));
        var box = Ui.V(
            Ui.T(string.IsNullOrWhiteSpace(g.Model) ? g.Vendor : g.Model.StartsWith(g.Vendor, StringComparison.OrdinalIgnoreCase) ? g.Model : $"{g.Vendor} · {g.Model}", 14, weight: FontWeights.SemiBold),
            Ui.Muted("Bu ayarlar Windows'tan değil BIOS'tan yapılır; FrameProof onları senin yerine değiştirmez, sadece nerede olduklarını gösterir. " +
                     $"BIOS'a girmek için bilgisayar açılırken {g.EnterKey} tuşuna art arda bas. Değişiklikten sonra F10 ile kaydedip çık.", 12, new Thickness(0, 4, 0, 6)));
        int n = 1;
        foreach (var s in g.Steps)
        {
            box.Children.Add(Ui.T($"{n++}. {s.Title}", 13, weight: FontWeights.SemiBold, margin: new Thickness(0, 10, 0, 0)));
            box.Children.Add(Ui.Muted(s.Why, 12, new Thickness(16, 2, 0, 0)));
            var where = Ui.T("Nerede: " + s.Where, 12, "Text", margin: new Thickness(16, 4, 0, 0));
            where.TextWrapping = TextWrapping.Wrap;
            box.Children.Add(where);
        }
        foreach (var t in box.Children.OfType<TextBlock>()) t.TextWrapping = TextWrapping.Wrap;

        UIElement? btn = g.CanRebootToFirmware
            ? Ui.Btn("BIOS'a yeniden başlat", () =>
            {
                if (!Admin.IsElevated) { Ui.Info("Bunun için yönetici izni gerekiyor. Üstteki 'Yönetici olarak aç' düğmesini kullan."); return; }
                if (!Ui.Confirm("Bilgisayar HEMEN yeniden başlayıp doğrudan BIOS ekranına girecek.\n\n" +
                                "Açık oyunları ve kaydedilmemiş işleri önce kapat. Bu listeyi telefonunla fotoğraflamak işini kolaylaştırır.\n\n" +
                                "BIOS'ta işin bitince F10 ile kaydedip çık. Şimdi yeniden başlatılsın mı?", "BIOS'a yeniden başlat")) return;
                var err = Device.RebootToFirmware();
                if (err != null) Ui.Info(err + $"\n\nElle girmek için: yeniden başlat ve açılırken {g.EnterKey} tuşuna bas.");
            })
            : null;
        Body.Children.Add(Ui.Card(Ui.Row(box, btn), padding: 16));
    }

    private static Border Stat(string n, string label, string brush)
    {
        var num = Ui.T(n, 28, n == "0" ? "Dim" : "Text", FontWeights.Bold);
        num.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        var tile = SevTile(brush, 38);
        tile.Margin = new Thickness(0, 0, 14, 0);
        var text = Ui.V(num, Ui.Muted(label, 12));
        text.VerticalAlignment = VerticalAlignment.Center;
        num.Margin = new Thickness(0, -4, 0, -2);
        return Ui.Card(Ui.H(tile, text), new Thickness(0, 0, 10, 4), 14);
    }

    /// <summary>Önem derecesine göre yumuşak renkli kare içinde simge.</summary>
    internal static Border SevTile(string brush, double size = 34)
    {
        int code = brush switch { "Bad" or "Warn" => 0xE7BA, "Info" => 0xE946, _ => 0xE73E };
        var icon = Ui.Icon(((char)code).ToString(), size * 0.42, brush);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28),
            Background = Ui.B(brush + "Soft"), VerticalAlignment = VerticalAlignment.Center, Child = icon,
        };
    }

    /// <summary>Tarama bulgusu kartı; ana ekran da kullanır.</summary>
    internal static UIElement FindingCard(Finding f, ShellView shell, Func<Task> reload)
    {
        var head = Ui.H(Ui.T(f.Title, 14, weight: FontWeights.SemiBold));
        if (!string.Equals(f.Category, f.Title, StringComparison.OrdinalIgnoreCase))
            head.Children.Add(Ui.Badge(f.Category, "Muted"));
        var text = Ui.V(head, Ui.T(f.Value, 13, "Text", margin: new Thickness(0, 4, 0, 0)));
        if (!string.IsNullOrWhiteSpace(f.Advice))
            text.Children.Add(Ui.Muted(f.Advice, 12, new Thickness(0, 4, 0, 0)));

        UIElement? action = null;
        if (f.FixTweakId != null && f.Severity != Severity.Good && TweakCatalog.Find(f.FixTweakId) is { } tw)
        {
            action = Ui.Btn("Düzelt", async () =>
            {
                if (tw.NeedsAdmin && !Admin.IsElevated) { Ui.Info("Bu ayar için yönetici izni gerekiyor. Üstteki 'Yönetici olarak aç' düğmesini kullan."); return; }
                if (tw.Risk != null && !Ui.Confirm($"{tw.Title}\n\n{tw.WhatItDoes}\n\nDikkat: {tw.Risk}\n\nİstediğin an 'Geri al' sekmesinden geri alabilirsin. Devam edilsin mi?")) return;
                var e = await Task.Run(tw.Apply);
                shell.Invalidate("tweaks");
                shell.Invalidate("journal");
                shell.Invalidate("home");
                shell.Invalidate("scan");
                if (e.NeedsReboot) Ui.Info("Uygulandı. Bilgisayarı yeniden başlatınca etkin olur.");
                await reload();
            }, primary: f.Severity == Severity.Bad);
        }
        else if (f.Action == "cleanup")
            action = Ui.Btn("Temizliğe git", () => shell.ShowAsync("clean"));
        else if (f.Action == "tweaks")
            action = Ui.Btn("Ayarlara git", () => shell.ShowAsync("tweaks"));
        else if (f.Action == "startup")
            action = Ui.Btn("Başlangıca git", () => shell.ShowAsync("startup"));
        else if (f.Action == "bios")
            action = Ui.Btn("Nasıl yapılır?", () => Ui.Info(BiosGuide.HowTo(f.Id)));

        var bar = SevTile(Ui.SevKey(f.Severity));
        bar.VerticalAlignment = VerticalAlignment.Top;
        bar.Margin = new Thickness(0, 0, 14, 0);
        var row = new DockPanel();
        DockPanel.SetDock(bar, Dock.Left);
        row.Children.Add(bar);
        row.Children.Add(Ui.Row(text, action));
        return Ui.Card(row, padding: 14);
    }

    /// <summary>Dört eşit sütun.</summary>
    private sealed class UniformGrid4
    {
        public readonly System.Windows.Controls.Primitives.UniformGrid Element = new() { Columns = 4, Margin = new Thickness(0, 0, -10, 0) };
        public void Add(UIElement e) => Element.Children.Add(e);
    }
}
