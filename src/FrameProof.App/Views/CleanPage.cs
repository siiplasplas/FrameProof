using System.IO;
using System.Windows;
using System.Windows.Controls;
using FrameProof.Core.Cleanup;
using FrameProof.Core.Games;
using FrameProof.Core.Tweaks;
using FrameProof.Core.Util;

namespace FrameProof.App.Views;

public sealed class CleanPage : Page
{
    private sealed class Row
    {
        public required CleanupTarget Target;
        public required CheckBox Box;
        public required TextBlock Size;
        public CleanupAnalysis? Analysis;
    }

    private readonly List<Row> _rows = new();
    private readonly TextBlock _total = Ui.T("", 14, weight: FontWeights.SemiBold);
    private readonly Button _run;
    private readonly TextBlock _free = Ui.Muted("", 12, new Thickness(0, 2, 0, 0));
    private CancellationTokenSource? _cts;

    public CleanPage(ShellView shell) : base(shell, "Temizlik", "Windows'un ve programların biriktirdiği gereksiz dosyalar. Kişisel dosyalarına dokunulmaz.")
    {
        _run = Ui.Btn("Seçilenleri temizle", RunAsync, primary: true);
        Actions.Children.Add(Ui.Btn("Yeniden hesapla", ReloadAsync));
    }

    protected override async Task LoadAsync()
    {
        Loading("Boyutlar hesaplanıyor…");
        var targets = await Task.Run(CleanupCatalog.Build);
        _rows.Clear();
        Body.Children.Clear();

        Body.Children.Add(Ui.Card(Ui.Row(Ui.V(_total, _free), _run), padding: 16));
        var gamesBox = new StackPanel();
        Body.Children.Add(gamesBox);
        Body.Children.Add(Ui.Section("Temizlenebilecekler"));

        foreach (var t in targets)
        {
            var size = Ui.T("…", 13, "Muted");
            size.TextAlignment = TextAlignment.Right;
            size.MinWidth = 80;
            var locked = t.NeedsAdmin && !Admin.IsElevated;
            var box = new CheckBox { IsChecked = t.DefaultOn && !locked, IsEnabled = !locked, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0) };
            var desc = Ui.V(Ui.T(t.Name, 14, weight: FontWeights.SemiBold), Ui.Muted(t.Description, 12, new Thickness(0, 3, 0, 0)));
            if (t.Warning != null) desc.Children.Add(Ui.T("⚠ " + t.Warning, 12, "Warn", margin: new Thickness(0, 4, 0, 0)));
            if (locked) desc.Children.Add(Ui.T("Yönetici izni gerekiyor", 12, "Muted", margin: new Thickness(0, 4, 0, 0)));
            var row = new DockPanel();
            DockPanel.SetDock(box, Dock.Left);
            row.Children.Add(box);
            row.Children.Add(Ui.Row(desc, size));
            var r = new Row { Target = t, Box = box, Size = size };
            box.Checked += (_, _) => UpdateTotal();
            box.Unchecked += (_, _) => UpdateTotal();
            _rows.Add(r);
            Body.Children.Add(Ui.Card(row, padding: 14));
        }

        UpdateFree();
        UpdateTotal();
        var ct = LoadToken;
        var gamesTask = AddGamesSectionSafe(gamesBox, ct);
        // Boyutları tek tek, arayüzü kilitlemeden hesapla.
        foreach (var r in _rows.ToList())
        {
            if (ct.IsCancellationRequested) return;
            r.Analysis = await Task.Run(() => { try { return r.Target.Analyze(); } catch (Exception ex) { return new CleanupAnalysis(0, 0, ex.Message); } });
            r.Size.Text = r.Analysis.Unknown ? "?" : Fmt.Bytes(r.Analysis.Bytes);
            r.Size.Foreground = Ui.B(r.Analysis.Bytes > 100L * 1024 * 1024 ? "Text" : "Muted");
            if (r.Analysis.Note != null) r.Size.ToolTip = r.Analysis.Note;
            if (r.Analysis.Note != null && r.Size.Parent is Grid g && g.Children[0] is StackPanel sp)
                sp.Children.Add(Ui.T(r.Analysis.Note, 12, "Info", margin: new Thickness(0, 4, 0, 0)));
            UpdateTotal();
        }
        await gamesTask;
    }

    // ---- Oyunları sıkıştır ----

    private CancellationTokenSource? _compressCts;

    /// <summary>Oyun bölümündeki bir hata (bozuk Steam/Epic kaydı vb.) bütün Temizlik sayfasını düşürmesin.</summary>
    private async Task AddGamesSectionSafe(StackPanel box, CancellationToken ct)
    {
        try { await AddGamesSection(box, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ErrorLog.Write("CleanPage.Games", ex);
            box.Children.Add(Ui.Card(Ui.T("Oyun listesi okunamadı: " + ex.Message, 12, "Warn"), padding: 14));
        }
        catch { }
    }

    private async Task AddGamesSection(StackPanel box, CancellationToken ct)
    {
        var games = await Task.Run(GameLibrary.List);
        if (ct.IsCancellationRequested) return;
        box.Children.Add(Ui.Section("Oyunları sıkıştır (silmeden yer aç)"));
        box.Children.Add(Ui.Muted(
            "Windows'un kendi dosya sıkıştırması (compact). Oyun dosyaları silinmez, oyun onları her zamanki gibi açar. Kazanç oyuna göre değişir: " +
            "çoğu oyunda %10–40, zaten sıkıştırılmış oyunlarda daha az. Steam bir güncellemede değişen dosyaları sıkıştırılmamış yazar; ara sıra 'Yeniden sıkıştır'. " +
            "Geri al sekmesinden ya da buradan geri alınır.", 12, new Thickness(0, 0, 0, 8)));
        if (games.Count == 0)
        {
            box.Children.Add(Ui.Card(Ui.Muted("Steam ya da Epic'te kurulu oyun bulunamadı.", 13), padding: 14));
            return;
        }
        foreach (var g in games)
        {
            var size = await Task.Run(() => { try { return GameCompress.Measure(g.Folder, ct); } catch { return null; } });
            if (ct.IsCancellationRequested) return;
            if (size == null || size.Bytes < 500L * 1024 * 1024) continue; // küçük oyunlarda değmez
            var info = Ui.Muted("", 12, new Thickness(0, 3, 0, 0));
            var slot = new ContentControl();
            box.Children.Add(Ui.Card(Ui.Row(Ui.V(Ui.T(g.Name, 14, weight: FontWeights.SemiBold), info), slot), padding: 14));
            bool recorded = await Task.Run(() => { try { return GameCompress.IsRecorded(g.Folder); } catch { return false; } });
            info.Text = $"{Fmt.Bytes(size.Bytes)} · diskte {Fmt.Bytes(size.OnDisk)}" +
                        (size.Saved > 50L * 1024 * 1024 ? $" · {Fmt.Bytes(size.Saved)} kazanılmış" : "") + $" · {g.Store}";
            if (size.Saved > 50L * 1024 * 1024) info.Foreground = Ui.B("Accent");
            var buttons = Ui.H(Ui.Btn(size.Saved > 50L * 1024 * 1024 ? "Yeniden sıkıştır" : "Sıkıştır", () => CompressAsync(g, size, info), primary: size.Saved < 50L * 1024 * 1024));
            if (recorded)
            {
                var undo = Ui.Btn("Geri al", () => DecompressAsync(g, info));
                undo.Margin = new Thickness(8, 0, 0, 0);
                buttons.Children.Add(undo);
            }
            slot.Content = buttons;
        }
    }

    private async Task CompressAsync(InstalledGame g, FolderSize size, TextBlock info)
    {
        if (_compressCts != null) { Ui.Info("Bir oyun zaten sıkıştırılıyor. Bitmesini bekle."); return; }
        var running = await Task.Run(() => GameCompress.RunningFrom(g.Folder));
        if (running != null) { Ui.Info($"{g.Name} şu an açık. Önce oyunu kapat."); return; }
        if (!Ui.Confirm(
                $"{g.Name} ({Fmt.Bytes(size.Bytes)}) sıkıştırılsın mı?\n\n" +
                "• Dosyalar silinmez, oyun aynen çalışır. Kazanç bitince belli olur (genelde %10–40).\n" +
                "• Oyunun boyutuna göre birkaç dakika ile yarım saat sürer; o sırada oyunu ve Steam güncellemesini açma.\n" +
                "• Yükleme ekranları biraz uzayabilir; maç içi FPS'e etkisi beklenmez.\n" +
                "• exe dosyalarına ve anti-cheat klasörlerine dokunulmaz. Yine de anti-cheat'li bir oyunda sorun görürsen geri al.\n" +
                "• SSD'ye bir kez oyun boyutu kadar yazılır.\n" +
                "• Geri almak için diskte kazanılan kadar boş yer gerekir.", "Oyunu sıkıştır")) return;

        _compressCts = new CancellationTokenSource();
        var stop = Ui.Btn("Sıkıştırmayı durdur", () => _compressCts?.Cancel());
        Actions.Children.Add(stop);
        var progress = new Progress<(long Done, long Total)>(p =>
            info.Text = $"Sıkıştırılıyor… %{(p.Total == 0 ? 100 : p.Done * 100 / p.Total)} ({Fmt.Bytes(p.Done)} / {Fmt.Bytes(p.Total)})");
        try
        {
            var ct = _compressCts.Token;
            var r = await Task.Run(() => GameCompress.Compress(g, progress, ct));
            Shell.Invalidate("journal");
            UpdateFree();
            var after = await Task.Run(() => GameCompress.Measure(g.Folder));
            var msg = (r.Cancelled ? "Durduruldu. Yapılan kısım kaldı (geri alınabilir).\n" : "Bitti.\n") +
                      $"{g.Name}: bu sefer {Fmt.Bytes(r.Saved)} açıldı, toplam {Fmt.Bytes(after.Saved)} kazanılmış (diskte {Fmt.Bytes(after.OnDisk)}).";
            if (r.Failed > 0)
                msg += $"\n{r.Failed:N0} dosya sıkıştırılamadı" + (Admin.IsElevated ? " (kullanımda olabilir)." : ". Program Files altındaki oyunlar için üstten 'Yönetici olarak aç'.");
            if (r.Saved < 100L * 1024 * 1024 && !r.Cancelled && r.Failed == 0)
                msg += "\nBu oyunun dosyaları zaten sıkıştırılmış; fazla yer çıkmadı. İstersen geri al.";
            Ui.Info(msg);
        }
        catch (Exception ex) { Ui.Error(ex); }
        finally
        {
            Actions.Children.Remove(stop);
            _compressCts = null;
        }
        await ReloadAsync();
    }

    private async Task DecompressAsync(InstalledGame g, TextBlock info)
    {
        if (_compressCts != null) { Ui.Info("Bir oyun sıkıştırılıyor. Bitmesini bekle."); return; }
        if (!Ui.Confirm($"{g.Name} eski haline (sıkıştırılmamış) döndürülsün mü?\nKazanılan yer geri gider; birkaç dakika sürebilir.", "Sıkıştırmayı geri al")) return;
        info.Text = "Geri alınıyor…";
        try
        {
            await Task.Run(() =>
            {
                var full = Path.GetFullPath(g.Folder).TrimEnd('\\');
                foreach (var e in Journal.Load().Where(e => e.Changes.Any(c => c.Type == "wof" && string.Equals(c.Name, full, StringComparison.OrdinalIgnoreCase))))
                    Journal.Undo(e.Id);
            });
            Shell.Invalidate("journal");
        }
        catch (Exception ex) { Ui.Error(ex); }
        await ReloadAsync();
    }

    private void UpdateFree()
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            _free.Text = $"C: sürücüsünde boş alan: {Fmt.Bytes(d.AvailableFreeSpace)} / {Fmt.Bytes(d.TotalSize)}";
        }
        catch { _free.Text = ""; }
    }

    private void UpdateTotal()
    {
        var sel = _rows.Where(r => r.Box.IsChecked == true).ToList();
        long total = sel.Where(r => r.Analysis is { Unknown: false }).Sum(r => r.Analysis!.Bytes);
        bool unknown = sel.Any(r => r.Analysis is null or { Unknown: true });
        _total.Text = sel.Count == 0 ? "Hiçbir şey seçilmedi" : $"Seçilenler: {Fmt.Bytes(total)}{(unknown ? " + hesaplanamayanlar" : "")}";
        _run.IsEnabled = sel.Count > 0;
    }

    private async Task RunAsync()
    {
        var sel = _rows.Where(r => r.Box.IsChecked == true).ToList();
        if (sel.Count == 0) return;
        var names = string.Join("\n", sel.Select(r => "• " + r.Target.Name + (r.Analysis is { Unknown: false } a ? $" ({Fmt.Bytes(a.Bytes)})" : "")));
        if (!Ui.Confirm($"Şunlar silinecek:\n\n{names}\n\nBu dosyalar geri dönüşüm kutusuna gitmez. Devam edilsin mi?", "Temizlik")) return;

        _cts = new CancellationTokenSource();
        var cancel = Ui.Btn("Durdur", () => _cts.Cancel());
        Actions.Children.Add(cancel);
        foreach (var r in _rows) r.Box.IsEnabled = false;
        long freed = 0;
        int files = 0, skipped = 0;
        var notes = new List<string>();
        try
        {
            foreach (var r in sel)
            {
                if (_cts.IsCancellationRequested) break;
                _total.Text = $"Temizleniyor: {r.Target.Name}…";
                r.Size.Text = "temizleniyor…";
                try
                {
                    var res = await r.Target.CleanAsync(_cts.Token);
                    freed += res.FreedBytes; files += res.Files; skipped += res.Skipped;
                    r.Size.Text = "✓ " + Fmt.Bytes(res.FreedBytes);
                    r.Size.Foreground = Ui.B("Accent");
                    if (res.Note != null) notes.Add($"{r.Target.Name}: {res.Note}");
                }
                catch (OperationCanceledException) { r.Size.Text = "durduruldu"; break; }
                catch (Exception ex)
                {
                    r.Size.Text = "hata";
                    r.Size.Foreground = Ui.B("Bad");
                    notes.Add($"{r.Target.Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            Actions.Children.Remove(cancel);
            _cts = null;
        }

        UpdateFree();
        _total.Text = $"Açılan alan: {Fmt.Bytes(freed)} ({files:N0} dosya)";
        var msg = $"Temizlik bitti. {Fmt.Bytes(freed)} alan açıldı.";
        if (skipped > 0) msg += $"\n{skipped:N0} dosya kullanımda olduğu için atlandı (normal).";
        if (notes.Count > 0) msg += "\n\n" + string.Join("\n", notes);
        Ui.Info(msg);
        Shell.Invalidate("scan");
        await ReloadAsync();
    }
}
