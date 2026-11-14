using System.Windows;
using System.Windows.Controls;
using FrameProof.Core.Tweaks;
using FrameProof.Core.Util;

namespace FrameProof.App.Views;

public sealed class TweaksPage : Page
{
    public TweaksPage(ShellView shell) : base(shell, "Ayarlar", "Sadece etkisi ölçülmüş ayarlar. Her birinin ne yaptığı ve ne kazandıracağı yazıyor; hepsi geri alınabilir.")
    {
    }

    protected override async Task LoadAsync()
    {
        Loading("Ayarlar okunuyor…");
        var states = await Task.Run(() => TweakCatalog.All.Select(t =>
        {
            TweakState s;
            try { s = t.GetState(); } catch { s = TweakState.NotApplicable; }
            return (t, s);
        }).ToList());
        var journal = await Task.Run(Journal.Load);

        Body.Children.Clear();
        Body.Children.Add(Ui.Card(Ui.Muted(
            "Burada olmayanlar bilerek yok: HPET kapatma, zamanlayıcı çözünürlüğü, Prefetch/SysMain kapatma, ağ kısıtlama anahtarları, " +
            "\"RAM temizleyici\" gibi şeyler ya ölçülebilir fark yaratmıyor ya da işleri kötüleştiriyor. Bunları vaat eden programlara dikkat.", 12), padding: 14));

        foreach (var g in states.GroupBy(x => x.t.Group))
        {
            Body.Children.Add(Ui.Section(g.Key));
            if (g.Key == RegValuesTweak.GroupName)
                Body.Children.Add(Ui.Muted("Bilerek dokunmadıklarımız: Defender, Windows Update, Güvenlik Duvarı ve arama dizini. Bunları kapatmak FPS getirmez; " +
                                           "bilgisayarı korumasız bırakır ya da güncellemeleri bozar.", 12, new Thickness(0, 0, 0, 8)));
            if (g.Key == HardMode.GroupName)
                Body.Children.Add(Ui.Muted("Her birinin küçük bir bedeli var (aşağıda 'Dikkat' kısmında yazıyor). Optimize et bunları yapmaz; tek tek sen seçersin. " +
                                           "Edge silinmez: silinen Edge'i Windows Update geri kurar, WebView2 kullanan uygulamalar bozulur.", 12, new Thickness(0, 0, 0, 8)));
            foreach (var (t, s) in g)
                Body.Children.Add(Card(t, s, journal.LastOrDefault(e => e.TweakId == t.Id)));
        }
    }

    private UIElement Card(Tweak t, TweakState s, JournalEntry? entry)
    {
        var stateBrush = s switch
        {
            TweakState.Applied => "Accent",
            TweakState.PendingReboot => "Info",
            TweakState.NotApplicable => "Muted",
            _ => "Warn",
        };
        var head = Ui.H(Ui.T(t.Title, 15, weight: FontWeights.SemiBold), Ui.Badge(t.StateText, stateBrush));
        if (t.NeedsAdmin) head.Children.Add(Ui.Badge("yönetici", "Muted"));
        if (t.NeedsReboot) head.Children.Add(Ui.Badge("yeniden başlatma", "Muted"));

        var body = Ui.V(head,
            Ui.T(t.WhatItDoes, 13, margin: new Thickness(0, 6, 0, 0)),
            Labeled("Etkisi: ", t.Effect, "Accent", 6));
        if (t.Risk != null) body.Children.Add(Labeled("Dikkat: ", t.Risk, "Warn", 4));

        UIElement? action = null;
        if (s == TweakState.NotApplied)
        {
            action = Ui.Btn("Uygula", async () =>
            {
                if (t.NeedsAdmin && !Admin.IsElevated) { Ui.Info("Bu ayar için yönetici izni gerekiyor. Üstteki 'Yönetici olarak aç' düğmesini kullan."); return; }
                if (t.Risk != null && !Ui.Confirm($"{t.Title}\n\nDikkat: {t.Risk}\n\nDevam edilsin mi?")) return;
                var e = await Task.Run(t.Apply);
                if (e.NeedsReboot) Ui.Info("Uygulandı. Bilgisayarı yeniden başlatınca etkin olur.");
                Shell.Invalidate("scan");
                Shell.Invalidate("journal");
                await ReloadAsync();
            }, primary: true);
        }
        else if (entry != null)
        {
            action = Ui.Btn("Geri al", async () =>
            {
                await Task.Run(() => Journal.Undo(entry.Id));
                if (entry.NeedsReboot) Ui.Info("Geri alındı. Bilgisayarı yeniden başlatınca etkin olur.");
                Shell.Invalidate("scan");
                Shell.Invalidate("journal");
                await ReloadAsync();
            });
        }
        return Ui.Card(Ui.Row(body, action), padding: 16);
    }

    /// <summary>Renkli kalın etiket + soluk, sarılabilir açıklama.</summary>
    private static TextBlock Labeled(string label, string text, string brush, double top)
    {
        var tb = new TextBlock { FontSize = 12, Margin = new Thickness(0, top, 0, 0) };
        tb.Inlines.Add(new System.Windows.Documents.Run(label) { Foreground = Ui.B(brush), FontWeight = FontWeights.SemiBold });
        tb.Inlines.Add(new System.Windows.Documents.Run(text) { Foreground = Ui.B("Muted") });
        return tb;
    }
}
