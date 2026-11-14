using System.Windows;
using FrameProof.Core.Tweaks;

namespace FrameProof.App.Views;

public sealed class JournalPage : Page
{
    public JournalPage(ShellView shell) : base(shell, "Geri al", "FrameProof'un yaptığı her değişiklik eski değeriyle birlikte burada. Tek tıkla eski haline döner.")
    {
        Actions.Children.Add(Ui.Btn("Hepsini geri al", async () =>
        {
            if (Journal.Load().Count == 0) return;
            if (!Ui.Confirm("FrameProof'un yaptığı tüm değişiklikler geri alınacak. Devam edilsin mi?")) return;
            await Task.Run(Journal.UndoAll);
            InvalidateOthers();
            await ReloadAsync();
        }));
    }

    public override bool ReloadOnShow => true;

    protected override async Task LoadAsync()
    {
        var list = await Task.Run(Journal.Load);
        Body.Children.Clear();
        if (list.Count == 0)
        {
            Body.Children.Add(Ui.Card(Ui.V(
                Ui.T("Henüz hiçbir değişiklik yapılmadı.", 14),
                Ui.Muted("Ayarlar ya da Başlangıç sekmesinden bir şey değiştirince burada görünür. Temizlikte silinen dosyalar burada olmaz; onlar gereksiz dosyalardır.", 12, new Thickness(0, 4, 0, 0)))));
            return;
        }
        foreach (var e in Enumerable.Reverse(list))
        {
            var info = Ui.V(
                Ui.T(e.Title, 14, weight: FontWeights.SemiBold),
                Ui.Muted($"{e.AppliedAt:dd.MM.yyyy HH:mm} · {e.Changes.Count} değişiklik{(e.NeedsReboot ? " · yeniden başlatma gerekir" : "")}", 12, new Thickness(0, 3, 0, 0)));
            var btn = Ui.Btn("Geri al", async () =>
            {
                await Task.Run(() => Journal.Undo(e.Id));
                if (e.NeedsReboot) Ui.Info("Geri alındı. Bilgisayarı yeniden başlatınca etkin olur.");
                InvalidateOthers();
                await ReloadAsync();
            });
            Body.Children.Add(Ui.Card(Ui.Row(info, btn), padding: 14));
        }
    }

    private void InvalidateOthers()
    {
        Shell.Invalidate("scan");
        Shell.Invalidate("tweaks");
        Shell.Invalidate("startup");
    }
}
