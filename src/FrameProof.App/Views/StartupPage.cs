using System.Windows;
using System.Windows.Controls;
using FrameProof.Core.Startup;
using FrameProof.Core.Util;

namespace FrameProof.App.Views;

public sealed class StartupPage : Page
{
    public StartupPage(ShellView shell) : base(shell, "Başlangıç uygulamaları", "Bilgisayar açılınca kendiliğinden başlayan programlar. Kapatmak programı silmez; Görev Yöneticisi ile aynı yöntem.")
    {
        Actions.Children.Add(Ui.Btn("Yenile", ReloadAsync));
    }

    protected override async Task LoadAsync()
    {
        Loading("Liste okunuyor…");
        var items = await Task.Run(StartupManager.List);
        Body.Children.Clear();
        int on = items.Count(i => i.Enabled);
        Body.Children.Add(Ui.Card(Ui.V(
            Ui.T($"{on} açık · {items.Count - on} kapalı", 14, weight: FontWeights.SemiBold),
            Ui.Muted("Oyun FPS'ine etkisi genelde küçüktür, ama açılış süresi ve arka planda yenen RAM/işlemci azalır. " +
                     "Ekran kartı sürücüsü, ses ve dokunmatik yüzey yazılımlarını açık bırak.", 12, new Thickness(0, 4, 0, 0))), padding: 14));

        foreach (var g in items.OrderByDescending(i => i.Enabled).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
            Body.Children.Add(Item(g));
        if (items.Count == 0) Body.Children.Add(Ui.Card(Ui.Muted("Başlangıçta açılan program yok.")));
    }

    private UIElement Item(StartupItem it)
    {
        var info = Ui.V(
            Ui.H(Ui.T(it.Name, 14, it.Enabled ? "Text" : "Muted", FontWeights.SemiBold), Ui.Badge(it.Source, "Muted")),
            Ui.Muted(it.Command, 11, new Thickness(0, 3, 0, 0)));
        ((TextBlock)info.Children[1]).TextTrimming = TextTrimming.CharacterEllipsis;
        ((TextBlock)info.Children[1]).TextWrapping = TextWrapping.NoWrap;
        ((TextBlock)info.Children[1]).ToolTip = it.Command;

        var locked = it.NeedsAdmin && !Admin.IsElevated;
        var btn = Ui.Btn(it.Enabled ? "Kapat" : "Aç", async () =>
        {
            await Task.Run(() => StartupManager.SetEnabled(it, !it.Enabled));
            Shell.Invalidate("scan");
            Shell.Invalidate("journal");
            await ReloadAsync();
        });
        if (locked) { btn.IsEnabled = false; btn.ToolTip = "Tüm kullanıcılar için kayıtlı; yönetici izni gerekiyor."; }
        return Ui.Card(Ui.Row(info, btn), padding: 12);
    }
}
