using System.IO;
using System.Windows;
using System.Windows.Controls;
using FrameProof.Core.Catalog;
using FrameProof.Core.Installation;
using FrameProof.Core.Library;
using FrameProof.Core.Util;
using Microsoft.Win32;

namespace FrameProof.App.Views;

/// <summary>Installation UI supplied with a validated catalog and coordinator; never chooses a network source.</summary>
public sealed class GameInstallPage : Page
{
    private readonly IReadOnlyList<GameInstallRecipe> _recipes;
    private readonly GameInstallCoordinator _coordinator;
    private readonly GameLibraryStore _store;
    private readonly Dictionary<string, GameEntry> _completed = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _status = Ui.T("Kurmak istediğin oyunu seç.", 14);
    private readonly TextBlock _folder = Ui.Muted("", 12);
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 8, Margin = new Thickness(0, 12, 0, 0) };
    private readonly StackPanel _content = new();
    private readonly Button _cancel;
    private string _installRoot;
    private CancellationTokenSource? _operation;
    public string InstallRoot => _installRoot;
    public event Action<string>? InstallRootChanged;

    public GameInstallPage(ShellView shell, IReadOnlyList<GameInstallRecipe> recipes,
        GameInstallCoordinator coordinator, string installRoot, GameLibraryStore? store = null)
        : base(shell, "Oyun kur", "Oyunu ve eşleşen fix paketini birlikte hazırla.")
    {
        ArgumentNullException.ThrowIfNull(recipes);
        ArgumentNullException.ThrowIfNull(coordinator);
        foreach (var recipe in recipes) GameCatalogClient.ValidateRecipe(recipe);
        if (recipes.Select(r => r.CatalogId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != recipes.Count)
            throw new InvalidDataException("Katalogda yinelenen oyun kimliği var.");
        _recipes = recipes.ToArray();
        _coordinator = coordinator;
        _store = store ?? new GameLibraryStore();
        _installRoot = Path.GetFullPath(installRoot);
        Subtitle.TextWrapping = TextWrapping.Wrap;
        _cancel = Ui.Btn("İptal et", Cancel);
        _cancel.Visibility = Visibility.Collapsed;
        Actions.Children.Add(Ui.Btn("Kurulum klasörü", ChooseFolder));
    }

    protected override Task LoadAsync()
    {
        Body.Children.Clear();
        _folder.Text = _installRoot;
        _folder.ToolTip = _installRoot;
        _folder.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(Ui.Card(Ui.V(Ui.T("Oyunların kurulacağı yer", 12, "Muted"), _folder), padding: 12));
        Body.Children.Add(Ui.Card(Ui.V(Ui.Row(_status, _cancel), _progress), padding: 16));
        Body.Children.Add(Ui.Free(_content));
        Render();
        return Task.CompletedTask;
    }

    private void Render()
    {
        _content.Children.Clear();
        _completed.Clear();
        foreach (var entry in _store.Load().Where(g => !string.IsNullOrWhiteSpace(g.CatalogId)))
            _completed.TryAdd(entry.CatalogId!, entry);
        foreach (var job in _coordinator.GetJobs().Where(j => j.Phase is not "Completed" and not "Failed"))
        {
            var resume = Ui.Btn("Devam et", () => RunAsync(() => _coordinator.RecoverAsync(job.JobId, true, _operation!.Token, Progress())));
            var undo = Ui.Btn("Geri al", () => RunAsync(() => _coordinator.RecoverAsync(job.JobId, false, _operation!.Token, Progress())));
            undo.Margin = new Thickness(8, 0, 0, 0);
            _content.Children.Add(Ui.Card(Ui.Row(Ui.V(Ui.T(job.Name, 16, weight: FontWeights.SemiBold),
                Ui.T("Tamamlanmamış kurulum", 12, "Warn", margin: new Thickness(0, 4, 0, 0))), Ui.H(resume, undo))));
        }
        if (_recipes.Count == 0)
        {
            _content.Children.Add(Ui.Card(Ui.V(Ui.T("Henüz kurulum kataloğu yok", 16, weight: FontWeights.SemiBold),
                Ui.Muted("Bir katalog kaynağı bağlandığında kurulabilen oyunlar burada görünecek.", 12, new Thickness(0, 8, 0, 0)))));
            return;
        }
        foreach (var recipe in _recipes)
        {
            _completed.TryGetValue(recipe.CatalogId, out var installed);
            var ready = installed?.CanLaunch == true && installed.InstalledGameVersion == recipe.GameVersion
                && installed.InstalledFixVersion == recipe.FixVersion;
            var action = Ui.Btn(ready ? "Oyna" : "Kur ve hazırla", async () =>
            {
                if (ready)
                {
                    var current = _store.Load().SingleOrDefault(g => g.Id == installed!.Id)
                        ?? throw new InvalidOperationException("Oyun kaydı artık bulunamıyor.");
                    if (!current.CanLaunch || current.InstalledGameVersion != recipe.GameVersion || current.InstalledFixVersion != recipe.FixVersion)
                        throw new InvalidOperationException("Oyun durumu değişti. Sayfayı yeniden açıp kurulumu kontrol et.");
                    using var launched = GameLauncher.Start(current);
                    return;
                }
                // Catalog validation restricts CatalogId to a portable filename segment.
                await RunAsync(async () => await _coordinator.InstallAsync(recipe, Target(recipe.CatalogId), _operation!.Token, Progress()));
            });
            if (ready && Admin.IsElevated)
            {
                action.IsEnabled = false;
                action.ToolTip = "Oynamak için FrameProof'u yönetici olmadan aç.";
            }
            var description = ready ? "Kurulum tamamlandı" : $"Oyun {recipe.GameVersion}" +
                (recipe.FixPackage is null ? "" : $" · Fix {recipe.FixVersion}");
            _content.Children.Add(Ui.Card(Ui.Row(Ui.V(Ui.T(recipe.Name, 16, weight: FontWeights.SemiBold),
                Ui.T(description, 12, ready ? "Accent" : "Muted", margin: new Thickness(0, 8, 0, 0))), action)));
        }
    }

    private string Target(string catalogId)
    {
        if (string.IsNullOrWhiteSpace(catalogId) || catalogId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || catalogId is "." or "..")
            throw new InvalidDataException("Oyun kimliği klasör adı olarak kullanılamıyor.");
        var target = Path.GetFullPath(Path.Combine(_installRoot, catalogId));
        if (!target.StartsWith(Path.TrimEndingDirectorySeparator(_installRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Kurulum klasörü geçersiz.");
        return target;
    }

    private IProgress<GameInstallProgress> Progress() => new Progress<GameInstallProgress>(p =>
    {
        if (_operation is null) return;
        _status.Text = p.Stage;
        _progress.IsIndeterminate = p.TotalBytes <= 0 && p.TotalFiles <= 0;
        if (p.TotalBytes > 0) _progress.Value = Math.Clamp(100d * p.ReceivedBytes / p.TotalBytes, 0, 100);
        else if (p.TotalFiles > 0) _progress.Value = Math.Clamp(100d * p.CompletedFiles / p.TotalFiles, 0, 100);
    });

    private async Task RunAsync(Func<Task<GameEntry?>> action)
    {
        if (_operation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        _content.IsEnabled = false;
        Actions.IsEnabled = false;
        _cancel.Visibility = Visibility.Visible;
        _cancel.IsEnabled = true;
        _progress.Value = 0;
        _progress.IsIndeterminate = true;
        _status.Text = "Kurulum hazırlanıyor…";
        try
        {
            var result = await action();
            if (result is not null)
            {
                _completed[result.CatalogId!] = result;
                Shell.Invalidate("games");
                _status.Text = result.Name + " hazır.";
                _progress.Value = 100;
            }
            else _status.Text = "Kurulum geri alındı.";
        }
        catch (OperationCanceledException) { _status.Text = "İşlem durduruldu."; }
        catch (Exception ex) { _status.Text = "Kurulum tamamlanamadı: " + ex.Message; ErrorLog.Write("GameInstallPage", ex); }
        finally
        {
            _operation = null;
            _progress.IsIndeterminate = false;
            _cancel.Visibility = Visibility.Collapsed;
            _content.IsEnabled = true;
            Actions.IsEnabled = true;
            Render();
        }
    }

    private void Cancel()
    {
        _operation?.Cancel();
        _cancel.IsEnabled = false;
    }

    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Oyunların kurulacağı klasörü seç", Multiselect = false };
        if (Directory.Exists(_installRoot)) dialog.InitialDirectory = _installRoot;
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _installRoot = Path.GetFullPath(dialog.FolderName);
        _folder.Text = _installRoot;
        _folder.ToolTip = _installRoot;
        InstallRootChanged?.Invoke(_installRoot);
    }
}
