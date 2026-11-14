using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FrameProof.Core.Library;
using FrameProof.Core.Util;
using Microsoft.Win32;

namespace FrameProof.App.Views;

/// <summary>Local library UI. Shell registration is intentionally left to the integration step.</summary>
public sealed class GamesPage : Page
{
    private readonly GameLibraryStore _store;
    private readonly StackPanel _list = new();
    private readonly TextBox _search = new();
    private readonly TextBlock _summary = Ui.T("", 14, weight: FontWeights.SemiBold);
    private IReadOnlyList<GameEntry> _games = Array.Empty<GameEntry>();
    private bool _busy;

    public GamesPage(ShellView shell) : this(shell, new GameLibraryStore()) { }

    public GamesPage(ShellView shell, GameLibraryStore store)
        : base(shell, "Oyunlarım", "Bilgisayarındaki oyunları tek yerde topla ve başlat.")
    {
        _store = store;
        Subtitle.TextWrapping = TextWrapping.Wrap;
        Actions.Children.Add(Ui.Btn("Oyun ekle", AddAsync, primary: true));
        _search.MinWidth = 192;
        _search.ToolTip = "Oyun adına veya kaynağına göre ara";
        AutomationProperties.SetName(_search, "Oyun ara");
        _search.TextChanged += (_, _) => RenderList();
    }

    public override bool ReloadOnShow => true;

    protected override async Task LoadAsync()
    {
        Loading("Oyun kütüphanesi okunuyor…");
        var token = LoadToken;
        _games = await Task.Run(_store.Load, token);
        token.ThrowIfCancellationRequested();
        Body.Children.Clear();
        if (Admin.IsElevated)
            Body.Children.Add(Ui.Card(Ui.T("Oyun başlatmak için FrameProof'u yönetici olmadan aç. Kütüphaneyi bu pencerede düzenleyebilirsin.", 12, "Warn"), padding: 12));
        var find = Ui.Btn("Kurulu oyunları bul", DiscoverAsync);
        var refresh = Ui.Btn("Yenile", ReloadAsync);
        refresh.Margin = new Thickness(8, 0, 0, 0);
        Body.Children.Add(Ui.Row(Ui.V(_summary,
            Ui.Muted("Steam ve Epic kurulumlarını bulabilir veya bir oyun dosyası seçebilirsin.", 12, new Thickness(0, 4, 0, 0))), Ui.H(find, refresh)));
        Body.Children.Add(Ui.V(Ui.T("Oyun ara", 12, "Muted", margin: new Thickness(0, 16, 0, 4)), _search));
        _list.Margin = new Thickness(0, 16, 0, 0);
        Body.Children.Add(Ui.Free(_list));
        RenderList();
    }

    private void RenderList()
    {
        _summary.Text = $"{_games.Count} oyun · {_games.Count(g => g.CanLaunch)} başlatmaya hazır";
        _list.Children.Clear();
        var query = _search.Text.Trim();
        var shown = _games.Where(g => g.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || g.Source.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        if (shown.Length == 0)
        {
            _list.Children.Add(Ui.Card(Ui.V(
                Ui.T(_games.Count == 0 ? "İlk oyununu ekle" : "Eşleşen oyun bulunamadı", 16, weight: FontWeights.SemiBold),
                Ui.Muted(_games.Count == 0 ? "Kurulu oyunları bul veya sağ üstteki Oyun ekle düğmesinden oyunun EXE dosyasını seç."
                    : "Başka bir ad dene veya aramayı temizle.", 12, new Thickness(0, 8, 0, 0)))));
            return;
        }
        foreach (var game in shown) _list.Children.Add(GameRow(game));
    }

    private UIElement GameRow(GameEntry game)
    {
        var path = Ui.Muted(game.InstallDirectory, 12, new Thickness(0, 4, 0, 0));
        path.TextWrapping = TextWrapping.NoWrap;
        path.TextTrimming = TextTrimming.CharacterEllipsis;
        path.ToolTip = game.InstallDirectory;
        var status = game.InstallationPending ? "Kurulum devam ediyor" : !game.IsInstalled ? "Klasör bulunamadı" : game.CanLaunch ? "Hazır" : "Başlatma dosyası gerekli";
        var info = Ui.V(Ui.T(game.Name, 16, weight: FontWeights.SemiBold), path,
            Ui.T($"{(game.Source == "Manual" ? "Elle eklendi" : game.Source)} · {status}", 12,
                game.CanLaunch ? "Accent" : "Warn", margin: new Thickness(0, 8, 0, 0)));
        var play = Ui.Btn(game.CanLaunch ? "Oyna" : !game.IsInstalled ? "Oyunu bul" : "EXE seç", async () =>
        {
            if (game.CanLaunch)
            {
                var current = _store.Load().SingleOrDefault(g => g.Id == game.Id)
                    ?? throw new InvalidOperationException("Oyun kaydı artık bulunamıyor. Kütüphaneyi yenile.");
                using var process = GameLauncher.Start(current);
            }
            else await SelectExeAsync(game);
        });
        if (game.InstallationPending)
        {
            play.Content = "Hazırlanıyor";
            play.IsEnabled = false;
            play.ToolTip = "Kurulum tamamlanınca oyun başlatılabilir.";
        }
        if (game.CanLaunch && Admin.IsElevated)
        {
            play.IsEnabled = false;
            play.ToolTip = "Oyunları başlatmak için FrameProof'u yönetici olmadan aç.";
        }
        var options = Ui.Btn("Seçenekler", () => { });
        options.Margin = new Thickness(8, 0, 0, 0);
        AutomationProperties.SetName(options, game.Name + " seçenekleri");
        var menu = new ContextMenu { Background = Ui.B("Panel2"), Foreground = Ui.B("Text") };
        var choose = new MenuItem { Header = "Başlatma dosyasını seç…" };
        choose.Click += async (_, _) => { try { await SelectExeAsync(game); } catch (Exception ex) { Ui.Error(ex); } };
        var remove = new MenuItem { Header = "Kütüphaneden kaldır" };
        remove.Click += async (_, _) =>
        {
            try
            {
                if (Ui.Confirm($"{game.Name} kütüphaneden kaldırılsın mı? Oyun dosyaları silinmez.", "Kütüphaneden kaldır"))
                    await ChangeAsync(() => _store.Remove(game.Id));
            }
            catch (Exception ex) { Ui.Error(ex); }
        };
        menu.Items.Add(choose);
        menu.Items.Add(remove);
        options.ContextMenu = menu;
        options.Click += (_, _) => { menu.PlacementTarget = options; menu.IsOpen = true; };
        return Ui.Card(Ui.Row(info, Ui.H(play, options)), padding: 16);
    }

    private async Task AddAsync()
    {
        var dialog = ExeDialog("Oyun ekle");
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            await ChangeAsync(() => _store.AddManual(dialog.FileName));
    }

    private async Task SelectExeAsync(GameEntry game)
    {
        var dialog = ExeDialog(game.Name + " — başlatma dosyası");
        if (Directory.Exists(game.InstallDirectory)) dialog.InitialDirectory = game.InstallDirectory;
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (!game.IsInstalled)
            await ChangeAsync(() => _store.Relocate(game.Id, dialog.FileName));
        else
            await ChangeAsync(() => _store.SetLaunchProfile(game.Id, new GameLaunchProfile { ExecutablePath = dialog.FileName }));
    }

    private Task DiscoverAsync() => ChangeAsync(() => _store.Discover());

    private async Task ChangeAsync(Action change)
    {
        if (_busy) return;
        _busy = true;
        Body.IsEnabled = false;
        Actions.IsEnabled = false;
        try { await Task.Run(change); await ReloadAsync(); }
        finally { _busy = false; Body.IsEnabled = true; Actions.IsEnabled = true; }
    }

    private static OpenFileDialog ExeDialog(string title) => new()
    {
        Title = title,
        Filter = "Oyun uygulaması (*.exe)|*.exe",
        CheckFileExists = true,
        Multiselect = false,
    };
}
