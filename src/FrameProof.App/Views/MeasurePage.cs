using FrameProof.Core.Games;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FrameProof.Core.Measure;
using FrameProof.Core.Util;

namespace FrameProof.App.Views;

/// <summary>
/// PresentMon ile oyunun gerçek kare sürelerini ölçer. Akış: oyunu seç → 10 sn geri sayım (oyuna geç) → ölç → özet.
/// Önce/sonra etiketleriyle en az 3'er ölçüm alınınca istatistikle karşılaştırır.
/// </summary>
public sealed class MeasurePage : Page
{
    private readonly ListBox _procs = new() { Height = 170, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox _seconds = new() { Text = "60", Width = 70 };
    private readonly TextBox _label = new() { Text = "önce", Width = 140 };
    private readonly TextBlock _status = Ui.Muted("", 13, new Thickness(0, 10, 0, 0));
    private readonly ProgressBar _bar = new() { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly StackPanel _result = new();
    private CancellationTokenSource? _cts;

    public MeasurePage(ShellView shell) : base(shell, "FPS ölçümü", "Bir ayarın gerçekten işe yarayıp yaramadığını ölçerek gösterir. Tahmin yok, sayı var.")
    {
    }

    protected override async Task LoadAsync()
    {
        Body.Children.Clear();
        var pm = PresentMon.Locate();
        Body.Children.Add(PresentMonCard(pm));
        if (pm != null)
        {
            Body.Children.Add(CaptureCard(pm));
            Body.Children.Add(_result);
        }
        Body.Children.Add(await RunsSection());
    }

    // ---------- PresentMon ----------

    private UIElement PresentMonCard(string? pm)
    {
        if (pm != null)
            return Ui.Card(Ui.Row(
                Ui.V(Ui.T("PresentMon hazır", 14, "Accent", FontWeights.SemiBold), Ui.Muted(pm, 11, new Thickness(0, 3, 0, 0))),
                Ui.Muted(Admin.IsElevated ? "" : "Ölçüm için yönetici izni gerekir", 12)), padding: 14);

        var info = Ui.V(
            Ui.T("Ölçüm aracı (PresentMon) gerekli", 14, weight: FontWeights.SemiBold),
            Ui.Muted("PresentMon, Intel'in açık kaynak kare ölçüm aracı. Oyuna dokunmaz, Windows'un kendi olay kaydından okur; anti-cheat ile sorun çıkarmaz. " +
                     "Bir kere indirilir, Intel imzası doğrulanır.", 12, new Thickness(0, 4, 0, 0)));
        var bar = new ProgressBar { Margin = new Thickness(0, 10, 0, 0), Maximum = 1, Visibility = Visibility.Collapsed };
        info.Children.Add(bar);
        var btn = Ui.Btn("İndir", async () =>
        {
            var asset = await PresentMon.FindLatestAsync(CancellationToken.None);
            if (!Ui.Confirm(
                    $"Şu dosya indirilecek:\n\n{asset.Name} ({asset.Version})\nBoyut: {Fmt.Bytes(asset.Size)}\nKaynak: github.com/GameTechDev/PresentMon\n\n" +
                    $"Konum: {AppPaths.Tools}\nİndirilsin mi?", "PresentMon indir")) return;
            bar.Visibility = Visibility.Visible;
            await PresentMon.DownloadAsync(asset, new Progress<double>(p => bar.Value = p), CancellationToken.None);
            await ReloadAsync();
        }, primary: true);
        return Ui.Card(Ui.Row(info, btn), padding: 16);
    }

    // ---------- Ölçüm ----------

    private UIElement CaptureCard(string pm)
    {
        FillProcs();
        var start = Ui.Btn("Ölçümü başlat", () => CaptureAsync(pm), primary: true);
        var refresh = Ui.Btn("Listeyi yenile", FillProcs);

        var opts = Ui.H(
            Ui.Muted("Süre (sn)", 12, new Thickness(0, 0, 8, 0)), _seconds,
            Ui.Muted("Etiket", 12, new Thickness(20, 0, 8, 0)), _label);
        foreach (var c in opts.Children.OfType<FrameworkElement>()) c.VerticalAlignment = VerticalAlignment.Center;

        var hint = Ui.Muted("Etiket örneği: ayarı değiştirmeden önce \"önce\", sonra \"sonra\". Her etiket için aynı yerde, aynı sürede en az 3 ölçüm al.", 12, new Thickness(0, 8, 0, 0));
        return Ui.Card(Ui.V(
            Ui.Row(Ui.V(Ui.T("1. Oyunu aç, sonra listeden seç", 14, weight: FontWeights.SemiBold),
                        Ui.Muted("Oyun pencere modunda ya da arka planda açık olabilir.", 12, new Thickness(0, 3, 0, 0))), refresh),
            _procs,
            Ui.T("2. Ayarlar", 14, weight: FontWeights.SemiBold, margin: new Thickness(0, 16, 0, 8)),
            opts, hint,
            Ui.Row(Ui.V(_status, _bar), start)), padding: 16);
    }

    private void FillProcs()
    {
        var sel = (_procs.SelectedItem as ListBoxItem)?.Tag as string;
        _procs.Items.Clear();
        foreach (var (exe, title) in PresentMon.WindowedProcesses())
        {
            var item = new ListBoxItem { Tag = exe, Content = Ui.H(Ui.T(exe, 13, weight: FontWeights.SemiBold), Ui.Muted("  " + title, 12)) };
            _procs.Items.Add(item);
            if (exe == sel) item.IsSelected = true;
        }
    }

    private async Task CaptureAsync(string pm)
    {
        if (_procs.SelectedItem is not ListBoxItem { Tag: string exe }) { Ui.Info("Önce listeden oyunu seç."); return; }
        if (!int.TryParse(_seconds.Text, out var secs) || secs < 10 || secs > 600) { Ui.Info("Süre 10 ile 600 saniye arasında olmalı."); return; }
        var label = _label.Text.Trim();
        if (label.Length == 0) { Ui.Info("Bir etiket yaz (ör. önce)."); return; }

        _cts = new CancellationTokenSource();
        var stop = Ui.Btn("İptal", () => _cts?.Cancel());
        Actions.Children.Add(stop);
        _result.Children.Clear();
        try
        {
            // Oyuna geçmek için geri sayım; son 3 saniyede bip.
            _bar.Visibility = Visibility.Visible;
            _bar.IsIndeterminate = false;
            _bar.Maximum = secs;
            for (int i = 10; i > 0; i--)
            {
                _status.Text = $"Oyuna geç! Ölçüm {i} sn sonra başlıyor…";
                if (i <= 3) Beep(660);
                await Task.Delay(1000, _cts.Token);
            }
            Beep(990);

            var t0 = DateTime.Now;
            var task = PresentMon.CaptureAsync(pm, exe, secs, label, _cts.Token);
            while (!task.IsCompleted)
            {
                var el = (DateTime.Now - t0).TotalSeconds;
                _bar.Value = Math.Min(el, secs);
                _status.Text = el < secs ? $"Ölçülüyor: {exe} · {Math.Max(0, secs - (int)el)} sn kaldı" : "Sonuç hesaplanıyor…";
                await Task.WhenAny(task, Task.Delay(250));
            }
            var r = await task;
            Beep(990);
            _status.Text = $"Bitti: {r.Frames:N0} kare kaydedildi.";
            _result.Children.Add(ResultCard(r));
            var runs = await RunsSection();
            Body.Children.RemoveAt(Body.Children.Count - 1);
            Body.Children.Add(runs);
        }
        catch (OperationCanceledException) { _status.Text = "İptal edildi."; }
        catch (Exception ex) { _status.Text = ""; Ui.Error(ex); }
        finally
        {
            Actions.Children.Remove(stop);
            _bar.Visibility = Visibility.Collapsed;
            _cts = null;
        }
    }

    private static void Beep(int hz)
    {
        try { Task.Run(() => Console.Beep(hz, 120)); } catch { }
    }

    private UIElement ResultCard(RunSummary r)
    {
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 12, 0, 0) };
        grid.Children.Add(Metric(Fmt.Num(r.AvgFps, 0), "Ortalama FPS", "Text"));
        grid.Children.Add(Metric(Fmt.Num(r.Low1Fps, 0), "1% low FPS", "Text"));
        grid.Children.Add(Metric(Fmt.Num(r.Low01Fps, 0), "0,1% low FPS", "Muted"));
        grid.Children.Add(Metric("%" + Fmt.Num(r.StutterPct), "Takılma", r.StutterPct > 2 ? "Warn" : "Text"));
        var v = Ui.V(
            Ui.H(Ui.T($"Sonuç · {r.Label}", 15, weight: FontWeights.SemiBold), Ui.Badge(r.Process, "Muted")),
            grid,
            Ui.Muted($"Darboğaz: {r.Bottleneck}" + (r.GpuBusyRatio is double g ? $" (ekran kartı meşguliyeti %{Fmt.Num(g * 100, 0)})" : ""), 12, new Thickness(0, 10, 0, 0)),
            Ui.Muted("1% low: en kötü %1'lik karelerin ortalaması. Akıcılığı ortalama FPS'ten daha iyi anlatır. Takılma: ortanca kare süresinin 2 katından uzun kareler.", 11, new Thickness(0, 4, 0, 0)));
        if (r.HwSamples > 0) v.Children.Add(Hardware(r));
        return Ui.Card(v, padding: 16);
    }

    /// <summary>Ölçüm sırasında okunan donanım bilgisi. Yalnız gerçekten okunabilenler gösterilir.</summary>
    private UIElement Hardware(RunSummary r)
    {
        var v = Ui.V(new Border { Height = 1, Background = Ui.B("Line"), Margin = new Thickness(0, 12, 0, 10) },
            Ui.T("Donanım (ölçüm sırasında)", 13, weight: FontWeights.SemiBold));
        void Add(string brush, string text) => v.Children.Add(Ui.H(Ui.Dot(brush), Ui.T(text, 12)));

        if (r.GpuName != null)
        {
            if (r.GpuIntegrated == true)
            {
                Add("Bad", $"Oyun zayıf (işlemciye gömülü) kartta çalıştı: {r.GpuName}. FPS'in büyük kısmı burada kayboluyor.");
                if (r.ExePath != null && Gpus.IsHybrid)
                    v.Children.Add(Ui.H(Ui.Btn("Bu oyunu güçlü karta sabitle", async () =>
                    {
                        await Task.Run(() => GameGpu.Pin(r.ExePath));
                        Shell.Invalidate("journal"); Shell.Invalidate("home"); Shell.Invalidate("scan");
                        Ui.Info("Sabitlendi. Oyunu kapatıp yeniden aç, sonra tekrar ölç.");
                    }, primary: true)));
            }
            else Add("Accent", $"Oyun şu kartta çalıştı: {r.GpuName}");
        }
        if (r.CpuPerfAvg is double ca && r.CpuPerfMin is double cm)
        {
            // Ekran kartı darboğazındaysa işlemcinin yavaşlaması normaldir (işi yok); uyarı yalnız işlemci sınırındaki ölçümde.
            bool cpuBound = r.GpuBusyRatio is < 0.75;
            bool warn = cm < 70 && cpuBound;
            Add(warn ? "Warn" : "Muted", $"İşlemci hızı: ortalama %{Fmt.Num(ca, 0)}, en düşük %{Fmt.Num(cm, 0)} (normal hızına göre; 100'ün üstü turbo)" +
                (warn ? ". Oyun işlemciyi beklerken işlemci yavaşladı: güç planına, pile ve ısınmaya bak." : ""));
        }
        if (r.GpuTempMax is double t)
            Add(t >= 87 ? "Warn" : "Accent", $"Ekran kartı en yüksek {Fmt.Num(t, 0)} °C" + (r.GpuClockPct is double c ? $", saat hızı ortalama azami hızının %{Fmt.Num(c, 0)}'i" : ""));
        if (r.ThermalPct is double th && th >= 10)
            Add("Warn", $"Ölçümün %{Fmt.Num(th, 0)}'inde ekran kartı ısıdan yavaşladı. Laptop altını açık tut, fan ızgaralarını temizle.");
        if (r.PowerCapPct is double pc && pc >= 50)
            v.Children.Add(Ui.Muted($"Ekran kartı ölçümün %{Fmt.Num(pc, 0)}'inde güç sınırındaydı. Yük altında bu normaldir; laptopta prize takılı ve 'Performans' modunda (ASUS Armoury Crate: Turbo) olmasına dikkat et.", 11, new Thickness(0, 4, 0, 0)));
        if (r.OnBattery == true)
            Add("Bad", "Ölçüm pildeyken yapıldı. Laptop pilde çok daha yavaş çalışır; prize takıp tekrar ölç.");
        if (r.RamShort is bool shortRam && r.RamAvailMinMb is long avail)
        {
            string free = Fmt.Bytes(avail * 1024 * 1024);
            if (shortRam)
                Add("Warn", $"RAM yetmemiş görünüyor: en az {free} boş kaldı" +
                    (r.PagesInAvg is double pi ? $", Windows saniyede ortalama {Fmt.Num(pi, 0)} sayfayı diskten okudu" : "") +
                    (r.CommitPeakPct is long cp ? $", sanal bellek %{cp} doldu" : "") +
                    ". Oyundan önce Genel bakış'taki 'Oyuna hazırla' ile tarayıcıyı ve diğer programları kapat, sonra tekrar ölç. Bu bir tahmindir.");
            else
                Add("Accent", $"RAM yetti: ölçüm boyunca en az {free} boş kaldı" +
                    (r.RamTotalMb is long tot ? $" ({Fmt.Bytes(tot * 1024 * 1024)} toplam)" : "") + ". RAM temizleyici gerekmez.");
        }
        if (v.Children.Count == 2) v.Children.Add(Ui.Muted("Okunabilen bir donanım bilgisi olmadı.", 12));
        return v;
    }

    private static UIElement Metric(string value, string label, string brush) =>
        Ui.V(Ui.T(value, 26, brush, FontWeights.SemiBold), Ui.Muted(label, 12));

    // ---------- Kayıtlar ve karşılaştırma ----------

    private async Task<UIElement> RunsSection()
    {
        var runs = await Task.Run(RunStore.Load);
        var panel = new StackPanel();
        panel.Children.Add(Ui.Section("Kayıtlı ölçümler"));
        if (runs.Count == 0)
        {
            panel.Children.Add(Ui.Card(Ui.Muted("Henüz ölçüm yok.", 13)));
            return panel;
        }

        var labels = runs.Select(r => r.Label).Distinct().ToList();
        if (labels.Count >= 2)
        {
            var a = ChipRow("A", labels, labels[0]);
            var b = ChipRow("B", labels, labels[1]);
            var outp = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var cmp = Ui.Btn("Karşılaştır", () =>
            {
                outp.Children.Clear();
                var la = Picked(a); var lb = Picked(b);
                if (la == null || lb == null || la == lb) { outp.Children.Add(Ui.Muted("İki farklı etiket seç.", 12)); return; }
                foreach (var c in RunStore.CompareLabels(la, lb))
                {
                    var brush = c.Verdict switch { Verdict.Better => "Accent", Verdict.Worse => "Bad", Verdict.NoDifference => "Muted", _ => "Warn" };
                    outp.Children.Add(Ui.H(Ui.Dot(brush), Ui.T(c.Text, 13)));
                }
                outp.Children.Add(Ui.Muted($"{la}: {runs.Count(r => r.Label == la)} ölçüm · {lb}: {runs.Count(r => r.Label == lb)} ölçüm. " +
                                           "\"Fark yok\" çıkıyorsa o ayar senin sisteminde işe yaramıyor demektir; bu da değerli bir bilgi.", 11, new Thickness(0, 6, 0, 0)));
            }, primary: true);
            panel.Children.Add(Ui.Card(Ui.V(
                Ui.T("Önce / sonra karşılaştır", 14, weight: FontWeights.SemiBold),
                Ui.Muted("A = eski durum, B = yeni durum. %95 güven aralığı ile hesaplanır.", 12, new Thickness(0, 3, 0, 10)),
                a.Panel, b.Panel, Ui.H(cmp), outp), padding: 16));
        }

        foreach (var r in Enumerable.Reverse(runs).Take(40))
        {
            var info = Ui.V(
                Ui.H(Ui.T(r.Label, 13, weight: FontWeights.SemiBold), Ui.Muted($"   {r.Process} · {r.At:dd.MM HH:mm} · {Fmt.Num(r.Seconds, 0)} sn", 12)),
                Ui.T($"Ort. {Fmt.Num(r.AvgFps, 0)} FPS · 1% low {Fmt.Num(r.Low1Fps, 0)} · takılma %{Fmt.Num(r.StutterPct)}", 12, margin: new Thickness(0, 2, 0, 0)));
            var del = Ui.Btn("Sil", async () =>
            {
                await Task.Run(() => RunStore.Delete(r.Id));
                var fresh = await RunsSection();
                Body.Children.RemoveAt(Body.Children.Count - 1);
                Body.Children.Add(fresh);
            });
            panel.Children.Add(Ui.Card(Ui.Row(info, del), new Thickness(0, 0, 0, 6), 10));
        }
        return panel;
    }

    private sealed record Chips(StackPanel Panel, List<RadioButton> Buttons);

    private static Chips ChipRow(string name, List<string> labels, string selected)
    {
        var wrap = new WrapPanel();
        var list = new List<RadioButton>();
        var group = "cmp" + name + Guid.NewGuid().ToString("N")[..4];
        foreach (var l in labels)
        {
            var rb = new RadioButton { Content = l, GroupName = group, Style = (Style)Application.Current.Resources["Chip"], IsChecked = l == selected };
            list.Add(rb);
            wrap.Children.Add(rb);
        }
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var lab = Ui.T(name, 13, "Muted", FontWeights.SemiBold, new Thickness(0, 3, 12, 0));
        lab.Width = 18;
        DockPanel.SetDock(lab, Dock.Left);
        row.Children.Add(lab);
        row.Children.Add(wrap);
        return new Chips(Ui.V(row), list);
    }

    private static string? Picked(Chips c) => c.Buttons.FirstOrDefault(b => b.IsChecked == true)?.Content as string;
}
