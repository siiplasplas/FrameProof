using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FrameProof.Core.Games;
using FrameProof.Core.Optimize;
using FrameProof.Core.Processes;
using FrameProof.Core.Scan;
using FrameProof.Core.Tweaks;
using FrameProof.Core.Util;

namespace FrameProof.App.Views;

/// <summary>Ana ekran: puan, tek tuşla optimize, laptop ekran kartı, Windows'un arka planı, açık programlar.</summary>
public sealed class HomePage : Page
{
    private OptimizeResult? _last;

    public HomePage(ShellView shell) : base(shell, "Genel bakış", "Bilgisayarının oyun için durumu. 'Optimize et'e basmadan hiçbir şey değişmez.")
    {
        Actions.Children.Add(Ui.Btn("Yeniden tara", ReloadAsync));
    }

    private sealed record Data(ScanReport Report, OptimizePlan Plan, List<GameExe> Games, List<(Tweak T, TweakState S)> Extras, List<(Tweak T, TweakState S)> Hard, List<AppGroup> Apps, (long Used, long Total) Ram);

    protected override async Task LoadAsync()
    {
        Loading("Bilgisayarın inceleniyor…");
        var d = await Task.Run(() =>
        {
            var report = SystemScanner.Run();
            var plan = Optimizer.Build();
            var games = Gpus.IsHybrid ? GameGpu.List() : new List<GameExe>();
            List<(Tweak T, TweakState S)> States(string group) => TweakCatalog.All.Where(t => t.Group == group)
                .Select(t => { TweakState s; try { s = t.GetState(); } catch { s = TweakState.NotApplicable; } return (t, s); })
                .Where(x => x.s != TweakState.NotApplicable).ToList();
            var extras = States(RegValuesTweak.GroupName);
            var hard = States(HardMode.GroupName);
            List<AppGroup> apps;
            try { apps = BackgroundApps.List(); } catch { apps = new(); }
            return new Data(report, plan, games, extras, hard, apps, Ram());
        });
        Render(d);
    }

    private void Render(Data d)
    {
        Body.Children.Clear();
        var r = d.Report;
        Body.Children.Add(Hero(r, d.Plan));
        if (_last != null) Body.Children.Add(ResultCard(_last));

        var problems = r.Findings.Where(f => f.Severity is Severity.Bad or Severity.Warn)
            .OrderByDescending(f => f.Severity).ToList();
        if (problems.Count > 0)
        {
            Body.Children.Add(Ui.Section("Sorunlar"));
            foreach (var f in problems) Body.Children.Add(ScanPage.FindingCard(f, Shell, ReloadAsync));
        }
        var infos = r.Findings.Where(f => f.Severity == Severity.Info).ToList();
        if (infos.Count > 0)
        {
            var row = Ui.Row(Ui.Muted($"Ayrıca {infos.Count} bilgi notu var: {string.Join(", ", infos.Select(f => f.Title).Take(4))}. Ayrıntısı Tarama sekmesinde.", 12),
                Ui.Btn("Taramaya git", () => Shell.ShowAsync("scan")));
            row.Margin = new Thickness(0, 4, 0, 0);
            Body.Children.Add(row);
        }

        if (d.Games.Count > 0) Body.Children.Add(GamesSection(d.Games));
        if (d.Extras.Count > 0) Body.Children.Add(ExtrasSection(d.Extras));
        Body.Children.Add(AppsSection(d.Apps, d.Ram));
        if (d.Hard.Count > 0) Body.Children.Add(HardSection(d.Hard));

        var good = r.Findings.Where(f => f.Severity == Severity.Good).ToList();
        if (good.Count > 0)
        {
            Body.Children.Add(Ui.Section("Zaten doğru olanlar"));
            var wrap = new WrapPanel();
            foreach (var f in good) wrap.Children.Add(Chip(f.Value.Length is > 0 and <= 28 ? $"{f.Title}: {f.Value}" : f.Title));
            Body.Children.Add(wrap);
        }
    }

    // ---------- Puan + Optimize et ----------

    public static int Score(ScanReport r) =>
        Math.Clamp(100 - 15 * r.Count(Severity.Bad) - 6 * r.Count(Severity.Warn), 5, 100);

    private UIElement Hero(ScanReport r, OptimizePlan plan)
    {
        int score = Score(r);
        var (label, brush) = score >= 85 ? ("İyi", "Accent") : score >= 65 ? ("Orta", "Warn") : ("Düzeltilmeli", "Bad");
        int bad = r.Count(Severity.Bad), warn = r.Count(Severity.Warn);

        var ring = Ring(score, brush);
        var summary = bad + warn == 0
            ? "Oyun açısından önemli bir sorun yok."
            : string.Join(", ", new[] { bad > 0 ? $"{bad} sorun" : null, warn > 0 ? $"{warn} uyarı" : null }.Where(x => x != null)) + " bulundu.";
        var headline = Ui.T(label, 26, brush, FontWeights.Bold);
        headline.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        var left = Ui.V(
            Ui.T("OYUN HAZIRLIĞI", 11, "Dim", FontWeights.Bold),
            headline,
            Ui.T(summary, 14, margin: new Thickness(0, 2, 0, 0)),
            Ui.Muted($"Son tarama {r.At:HH:mm}", 12, new Thickness(0, 4, 0, 0)));
        left.VerticalAlignment = VerticalAlignment.Center;

        // Plan önizlemesi
        var planList = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        if (plan.IsEmpty)
            planList.Children.Add(Ui.Muted("Tek tıkla yapılacak bir şey kalmadı. Aşağıdaki sorunlar elle karar vermen gereken şeyler.", 12));
        else
        {
            planList.Children.Add(Ui.T("OPTİMİZE ET ŞUNLARI YAPACAK", 11, "Dim", FontWeights.Bold, new Thickness(0, 0, 0, 6)));
            if (plan.Clean.Count > 0)
                planList.Children.Add(Line($"{Fmt.Bytes(plan.Bytes)} gereksiz dosya silinecek ({string.Join(", ", plan.Clean.Select(c => c.Target.Name))})"));
            foreach (var t in plan.Tweaks) planList.Children.Add(Line(t.Title));
        }
        if (plan.SkippedNoAdmin.Count > 0)
            planList.Children.Add(Ui.Muted($"Yönetici izni olmadığı için {plan.SkippedNoAdmin.Count} madde atlanacak. Üstteki 'Yönetici olarak aç' ile hepsi yapılır.", 11, new Thickness(0, 6, 0, 0)));

        var go = Ui.Btn(plan.IsEmpty ? "Her şey yolunda" : "Optimize et", () => RunOptimize(plan), primary: !plan.IsEmpty);
        go.FontSize = 16;
        go.FontWeight = FontWeights.SemiBold;
        go.Padding = new Thickness(34, 13, 34, 13);
        go.IsEnabled = !plan.IsEmpty;
        var note = Ui.Muted("Her ayar 'Geri al' sekmesinden geri alınır.", 11, new Thickness(0, 8, 0, 0));
        note.TextAlignment = TextAlignment.Center;
        var right = Ui.V(go, note);
        right.VerticalAlignment = VerticalAlignment.Center;
        right.MaxWidth = 230;

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(ring);
        left.Margin = new Thickness(24, 0, 16, 0);
        Grid.SetColumn(left, 1);
        top.Children.Add(left);
        Grid.SetColumn(right, 2);
        top.Children.Add(right);

        var content = Ui.V(top, Ui.Divider(20, 16), planList);
        if (Specs(r) is { } specs) { content.Children.Add(Ui.Divider(18, 0)); content.Children.Add(specs); }
        content.Margin = new Thickness(26, 24, 26, 20);

        // Puanın arkasında rengine göre hafif bir ışık: kartı düz bir kutu olmaktan çıkarır.
        var c = ((SolidColorBrush)Ui.B(brush)).Color;
        var glow = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new RadialGradientBrush
            {
                Center = new Point(0.1, 0.22), GradientOrigin = new Point(0.1, 0.22), RadiusX = 0.42, RadiusY = 0.75,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x30, c.R, c.G, c.B), 0),
                    new GradientStop(Color.FromArgb(0x10, c.R, c.G, c.B), 0.5),
                    new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
                },
            },
        };
        var layers = new Grid();
        layers.Children.Add(glow);
        layers.Children.Add(content);
        return new Border
        {
            Background = Ui.B("CardFill"),
            BorderBrush = Ui.B("CardEdge"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(0, 0, 0, 10),
            Child = layers,
        };
    }

    /// <summary>Kartın altındaki donanım şeridi: işlemci, ekran kartı, RAM, monitör. Taramadaki bilgi bulgularından.</summary>
    private static UIElement? Specs(ScanReport r)
    {
        Finding? F(string id, Func<Finding, bool>? pick = null) =>
            r.Findings.Where(f => f.Id == id).OrderByDescending(f => pick?.Invoke(f) ?? false).FirstOrDefault();
        static string Head(string v) => v.Split(" · ")[0];
        static string Tail(string v) { var p = v.Split(" · "); return p.Length > 1 ? string.Join(" · ", p.Skip(1)) : ""; }
        static string CleanCpu(string n)
        {
            foreach (var junk in new[] { "(R)", "(TM)", "(tm)", " CPU", " Processor" }) n = n.Replace(junk, "");
            var w = n.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
            if (w > 0) n = n[..w];
            var at = n.IndexOf(" @ ", StringComparison.Ordinal);
            if (at > 0) n = n[..at];
            return n.Trim();
        }

        var items = new List<(string Glyph, string Label, string Value, string Sub)>();
        if (F("cpu") is { } cpu) items.Add(("E950", "İşlemci", CleanCpu(Head(cpu.Value)), Tail(cpu.Value)));
        if (F("gpu", f => f.Title == "Ekran kartı") is { } gpu)
        {
            var name = Head(gpu.Value);
            foreach (var v in new[] { "NVIDIA ", "AMD ", "Intel(R) " })
                if (name.StartsWith(v, StringComparison.OrdinalIgnoreCase) && name.Length > v.Length + 6) { name = name[v.Length..]; break; }
            items.Add(("E964", "Ekran kartı", name, Tail(gpu.Value)));
        }
        if (F("ram") is { } ram) items.Add(("EEA1", "Bellek", Head(ram.Value), Tail(ram.Value)));
        if (F("refresh", f => f.Title == "Ana monitör") is { } mon)
        {
            var p = mon.Value.Split(" · ");
            items.Add(("E7F4", "Monitör", p.Length > 1 ? p[^1] : mon.Value, p.Length > 1 ? string.Join(" · ", p[..^1]) : ""));
        }
        if (items.Count == 0) return null;

        var g = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        for (int i = 0; i < items.Count; i++)
        {
            var (glyph, label, value, sub) = items[i];
            // İşlemci ve ekran kartı adları uzun: onlara daha geniş sütun.
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(glyph is "E950" or "E964" ? 1.35 : 1, GridUnitType.Star) });
            var icon = Ui.Icon(((char)Convert.ToInt32(glyph, 16)).ToString(), 18, "Accent"); // glyph: simge kodu (onaltılı)
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 2, 12, 0);
            var val = Ui.T(value, 13, weight: FontWeights.SemiBold);
            var subT = Ui.T(sub, 11, "Dim");
            foreach (var t in new[] { val, subT }) { t.TextWrapping = TextWrapping.NoWrap; t.TextTrimming = TextTrimming.CharacterEllipsis; t.ToolTip = t.Text; }
            var text = Ui.V(Ui.T(label.ToUpper(Fmt.Tr), 10, "Dim", FontWeights.Bold), val, subT);
            var dock = new DockPanel { Margin = new Thickness(i == 0 ? 0 : 16, 0, 12, 0) };
            DockPanel.SetDock(icon, Dock.Left);
            dock.Children.Add(icon);
            dock.Children.Add(text);
            var cell = new Border
            {
                BorderBrush = Ui.B("Line"),
                BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0),
                Child = dock,
            };
            Grid.SetColumn(cell, i);
            g.Children.Add(cell);
        }
        return g;
    }

    private async Task RunOptimize(OptimizePlan plan)
    {
        var lines = new List<string>();
        if (plan.Clean.Count > 0) lines.Add($"• {Fmt.Bytes(plan.Bytes)} gereksiz dosya silinecek");
        lines.AddRange(plan.Tweaks.Select(t => "• " + t.Title + (t.Risk != null ? $"\n   ({t.Risk})" : "")));
        if (!Ui.Confirm("Şunlar yapılacak:\n\n" + string.Join("\n", lines) + "\n\nAyarlar 'Geri al' sekmesinden geri alınabilir. Silinen dosyalar geri gelmez (zaten gereksiz dosyalar). Devam edilsin mi?", "Optimize et")) return;

        var status = Ui.T("Optimize ediliyor…", 14);
        Body.Children.Clear();
        Body.Children.Add(Ui.Card(Ui.V(status, new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 12, 0, 0) })));
        var prog = new Progress<string>(s => status.Text = s);
        try { _last = await Task.Run(() => Optimizer.ExecuteAsync(plan, prog, CancellationToken.None)); }
        catch (Exception ex) { Ui.Error(ex); }
        foreach (var id in new[] { "scan", "tweaks", "journal", "clean" }) Shell.Invalidate(id);
        await ReloadAsync();
    }

    private static UIElement ResultCard(OptimizeResult r)
    {
        var parts = new List<string>();
        if (r.FreedBytes > 0) parts.Add($"{Fmt.Bytes(r.FreedBytes)} açıldı");
        if (r.Applied.Count > 0) parts.Add($"{r.Applied.Count} ayar düzeltildi");
        var v = Ui.V(Ui.T(parts.Count > 0 ? "Bitti: " + string.Join(", ", parts) : "Bitti", 16, "Accent", FontWeights.SemiBold));
        if (r.FreeBefore > 0 && r.FreeAfter > 0)
            v.Children.Add(Ui.T($"Sistem diskinde boş alan: {Fmt.Bytes(r.FreeBefore)} → {Fmt.Bytes(r.FreeAfter)}", 13, margin: new Thickness(0, 6, 0, 0)));
        foreach (var a in r.Applied) v.Children.Add(Ui.Muted("✓ " + a, 12, new Thickness(0, 3, 0, 0)));
        if (r.FreeAfter > 0 && r.FreeAfter < 10L * 1024 * 1024 * 1024)
            v.Children.Add(Ui.Muted("Disk hâlâ dolu görünüyor: kalan dolu alan çöp değil; oyunların, programların ve dosyaların. Yer açmak için oynamadığın oyunları Steam'den kaldır " +
                                   "ya da 'Temizlik' sekmesindeki eski Windows seçeneğine ve aşağıdaki Sert mod'daki hazırda bekletmeye bak.", 12, new Thickness(0, 8, 0, 0)));
        foreach (var e in r.Errors) v.Children.Add(Ui.T("Yapılamadı: " + e, 12, "Warn", margin: new Thickness(0, 4, 0, 0)));
        if (r.Applied.Any(a => a.Contains("ekran kartı", StringComparison.OrdinalIgnoreCase)))
            v.Children.Add(Ui.Muted("Ekran kartı ayarı oyun bir sonraki açılışında etkin olur; açık olan oyunu kapatıp aç.", 12, new Thickness(0, 6, 0, 0)));
        return Ui.Card(v, padding: 18);
    }

    // ---------- Laptop: oyunlar hangi kartta ----------

    private UIElement GamesSection(List<GameExe> games)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Section("Oyunlar ve ekran kartı"));
        var dgpu = Gpus.Discrete?.Name ?? "harici kart";
        var list = new StackPanel();
        foreach (var g in games.GroupBy(g => g.Name).Select(x => x.OrderBy(y => y.Pref switch { GpuPref.PowerSaving => 0, GpuPref.Unset => 1, _ => 2 }).First()))
        {
            var (txt, br) = g.Pref switch
            {
                GpuPref.HighPerformance => ("Güçlü kart", "Accent"),
                GpuPref.PowerSaving => ("Zayıf kart", "Bad"),
                _ => ("Windows seçiyor", "Muted"),
            };
            list.Children.Add(Ui.Row(Ui.T(g.Name, 13), Ui.Badge(txt, br)));
            ((FrameworkElement)list.Children[^1]).Margin = new Thickness(0, 3, 0, 3);
        }
        var notPinned = games.Count(g => g.Pref != GpuPref.HighPerformance);
        UIElement? btn = null;
        if (notPinned > 0 && TweakCatalog.Find("gpu_pin") is { } tw)
            btn = Ui.Btn("Hepsini güçlü karta sabitle", async () =>
            {
                await Task.Run(tw.Apply);
                Shell.Invalidate("journal"); Shell.Invalidate("scan"); Shell.Invalidate("tweaks");
                Ui.Info("Oyunlar " + dgpu + " kartına sabitlendi. Şu an açık bir oyun varsa kapatıp yeniden aç.");
                await ReloadAsync();
            }, primary: games.Any(g => g.Pref == GpuPref.PowerSaving));
        var head = Ui.Row(Ui.V(
            Ui.T($"Bu laptopta iki ekran kartı var. Oyunlar {dgpu} ile çalışmalı.", 13),
            Ui.Muted("Windows'un oyun olarak tanıdığı programlar. Kayıt Windows Ayarlar > Ekran > Grafik bölümündekiyle aynıdır.", 12, new Thickness(0, 2, 0, 0))), btn);
        panel.Children.Add(Ui.Card(Ui.V(head, Ui.Divider(12, 8), list), padding: 16));
        return panel;
    }

    // ---------- Windows'un arka planı ----------

    private UIElement ExtrasSection(List<(Tweak T, TweakState S)> extras)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Section("Windows'un arka planı"));
        var list = new StackPanel();
        foreach (var (t, s) in extras)
        {
            var (txt, br) = s switch
            {
                TweakState.Applied => ("Kapalı", "Accent"),
                TweakState.PendingReboot => ("Yeniden başlatınca kapanır", "Info"),
                _ => ("Açık", "Warn"),
            };
            var row = Ui.Row(Ui.V(Ui.T(t.Title, 13), Ui.Muted(t.Effect, 11, new Thickness(0, 1, 0, 0))), Ui.Badge(txt, br));
            row.Margin = new Thickness(0, 4, 0, 4);
            list.Children.Add(row);
        }
        var open = extras.Where(x => x.S == TweakState.NotApplied).Select(x => x.T).ToList();
        UIElement? btn = null;
        if (open.Count > 0)
            btn = Ui.Btn($"Hepsini kapat ({open.Count})", async () =>
            {
                if (open.Any(t => t.NeedsAdmin) && !Admin.IsElevated)
                {
                    Ui.Info("Bunların çoğu için yönetici izni gerekiyor. Üstteki 'Yönetici olarak aç' düğmesini kullan.");
                    return;
                }
                var risks = open.Where(t => t.Risk != null).Select(t => $"• {t.Title}: {t.Risk}");
                if (!Ui.Confirm("Şunlar kapatılacak:\n\n" + string.Join("\n", open.Select(t => "• " + t.Title)) +
                                (risks.Any() ? "\n\nBilmen gerekenler:\n" + string.Join("\n", risks) : "") +
                                "\n\nHepsi 'Geri al' sekmesinden tek tek geri alınabilir. Devam edilsin mi?")) return;
                var errors = new List<string>();
                bool reboot = false;
                await Task.Run(() =>
                {
                    foreach (var t in open)
                    {
                        try { reboot |= t.Apply().NeedsReboot; }
                        catch (Exception ex) { errors.Add($"{t.Title}: {ex.Message}"); }
                    }
                });
                Shell.Invalidate("journal"); Shell.Invalidate("scan"); Shell.Invalidate("tweaks");
                if (errors.Count > 0) Ui.Info("Bazıları yapılamadı:\n\n" + string.Join("\n", errors));
                else if (reboot) Ui.Info("Kapatıldı. Bir kısmı bilgisayarı yeniden başlatınca tam etkin olur.");
                await ReloadAsync();
            });
        var head = Ui.Row(Ui.V(
            Ui.T("Windows'un arkada çalıştırdığı, oyuna bir faydası olmayan şeyler.", 13),
            Ui.Muted("Defender, Windows Update, Güvenlik Duvarı ve arama dizinine bilerek dokunmuyoruz: kapatmak FPS getirmez, bilgisayarı korumasız ya da bozuk bırakır.", 12, new Thickness(0, 2, 0, 0))), btn);
        panel.Children.Add(Ui.Card(Ui.V(head, Ui.Divider(12, 6), list), padding: 16));
        return panel;
    }

    // ---------- Arka planda açık programlar ----------

    private UIElement AppsSection(List<AppGroup> apps, (long Used, long Total) ram)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Section("Arka planda açık"));
        var v = new StackPanel();
        if (ram.Total > 0)
        {
            double pct = (double)ram.Used / ram.Total;
            v.Children.Add(Ui.T($"RAM: {Fmt.Bytes(ram.Used)} / {Fmt.Bytes(ram.Total)} kullanımda", 13, pct > 0.8 ? "Warn" : "Text"));
            var bar = new ProgressBar { Value = pct * 100, Height = 6, Margin = new Thickness(0, 6, 0, 10) };
            v.Children.Add(bar);
        }
        v.Children.Add(Ui.Muted("Oyuna girmeden önce kapatabileceğin programlar. Kapatmak geri alınamaz: kaydedilmemiş bir işin varsa önce kaydet. Program istediğin zaman yeniden açılabilir.", 12, new Thickness(0, 0, 0, 8)));

        var closable = apps.Where(a => a.Closable && a.RamBytes >= 30L * 1024 * 1024).ToList();
        var big = closable.Where(a => a.RamBytes >= 100L * 1024 * 1024).ToList();
        if (big.Count > 0)
        {
            long sum = big.Sum(a => a.RamBytes);
            var prep = Ui.Btn($"Oyuna hazırla ({Fmt.Bytes(sum)} boşalt)", () => PrepareForGame(big), primary: true);
            var head = Ui.Row(Ui.V(Ui.T("Oyuna girmeden önce tek tıkla:", 13, weight: FontWeights.SemiBold),
                Ui.Muted(string.Join(", ", big.Select(a => a.Name)) + " kapanır. Steam, Discord, oyun başlatıcıları, anti-cheat ve sürücü araçlarına asla dokunulmaz.", 12, new Thickness(0, 2, 0, 0))), prep);
            head.Margin = new Thickness(0, 0, 0, 10);
            v.Children.Add(head);
        }
        if (closable.Count == 0)
            v.Children.Add(Ui.T("Kapatılması gereken bir program açık değil.", 13, "Accent"));
        foreach (var a in closable)
        {
            var info = Ui.H(Ui.T(a.Name, 13, weight: FontWeights.SemiBold), Ui.Muted($"   {Fmt.Bytes(a.RamBytes)} RAM" + (a.Count > 1 ? $" · {a.Count} işlem" : ""), 12));
            var btn = Ui.Btn("Kapat", async () =>
            {
                if (!Ui.Confirm($"{a.Name} kapatılacak.\n\nKaydedilmemiş bir işin varsa kaybolabilir; bu işlem geri alınamaz. Devam edilsin mi?")) return;
                var n = await BackgroundApps.CloseAsync(a.Key);
                await ReloadAsync();
            });
            var row = Ui.Row(info, btn);
            row.Margin = new Thickness(0, 3, 0, 3);
            v.Children.Add(row);
        }

        long discord = 0;
        try { discord = BackgroundApps.DiscordRam(); } catch { }
        if (discord > 800L * 1024 * 1024)
            v.Children.Add(Ui.T($"Discord şu an {Fmt.Bytes(discord)} RAM kullanıyor. Düşürmek için Discord ayarlarında Erişilebilirlik'ten GIF'lerin ve animasyonlu emojilerin " +
                                "otomatik oynamasını, kullanmıyorsan Oyun Katmanı'nı kapat. Discord uzun süre açık kalınca şişer: sesli sohbette değilken sistem tepsisinden " +
                                "'Discord'dan çık' deyip yeniden açmak RAM'i sıfırlar.", 12, "Warn", margin: new Thickness(0, 10, 0, 0)));

        var others = apps.Where(a => !a.Closable && a.RamBytes >= 100L * 1024 * 1024).Take(5).ToList();
        if (others.Count > 0)
            v.Children.Add(Ui.Muted("Diğer büyük işlemler (dokunmuyoruz): " + string.Join(", ", others.Select(o => $"{o.Name} {Fmt.Bytes(o.RamBytes)}")), 11, new Thickness(0, 10, 0, 0)));
        panel.Children.Add(Ui.Card(v, padding: 16));
        return panel;
    }

    /// <summary>
    /// "RAM temizleyici"nin dürüst hali: belleği boşaltmak için RAM'i zorla diske itmek yerine, RAM'i gerçekten tutan
    /// programları (tarayıcı, Teams, OneDrive...) kapatır. Yalnız güvenli listedekiler; kapatmadan önce adlarıyla sorar.
    /// </summary>
    private async Task PrepareForGame(List<AppGroup> apps)
    {
        if (!Ui.Confirm("Şu programlar kapatılacak:\n\n" + string.Join("\n", apps.Select(a => $"• {a.Name} ({Fmt.Bytes(a.RamBytes)})")) +
                        "\n\nKaydedilmemiş bir işin (açık sekme formu, belge) varsa kaybolabilir; bu işlem geri alınamaz. Programları sonra istediğin zaman yeniden açabilirsin. Devam edilsin mi?", "Oyuna hazırla")) return;
        var before = Ram();
        foreach (var a in apps)
        {
            try { await BackgroundApps.CloseAsync(a.Key); } catch { }
        }
        await Task.Delay(1500); // Windows belleği geri alsın
        var after = Ram();
        long freed = Math.Max(0, before.Item1 - after.Item1);
        Ui.Info(freed > 0
            ? $"Hazır. {Fmt.Bytes(freed)} RAM boşaldı; şu an {Fmt.Bytes(after.Item2 - after.Item1)} boş. İyi oyunlar."
            : "Programlar kapatıldı. İyi oyunlar.");
        await ReloadAsync();
    }

    // ---------- Sert mod ----------

    private UIElement HardSection(List<(Tweak T, TweakState S)> items)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Section("Sert mod"));
        var list = new StackPanel();
        foreach (var (t, s) in items)
        {
            UIElement right = s switch
            {
                TweakState.Applied => Ui.Badge("Yapıldı", "Accent"),
                TweakState.PendingReboot => Ui.Badge("Yeniden başlatınca", "Info"),
                _ => Ui.Btn("Uygula", () => ApplyHard(t)),
            };
            var left = Ui.V(Ui.T(t.Title, 13, weight: FontWeights.SemiBold), Ui.Muted(t.Effect, 11, new Thickness(0, 1, 0, 0)));
            if (t.Risk != null && s == TweakState.NotApplied)
                left.Children.Add(Ui.T("Bedeli: " + t.Risk, 11, "Warn", margin: new Thickness(0, 2, 0, 0)));
            var row = Ui.Row(left, right);
            row.Margin = new Thickness(0, 6, 0, 6);
            list.Children.Add(row);
        }
        var head = Ui.V(
            Ui.T("Daha sert sadeleştirme. Her birinin küçük bir bedeli var, o yüzden Optimize et bunları yapmaz; tek tek sen seçersin.", 13),
            Ui.Muted("Hepsi 'Geri al' sekmesinden geri alınır. Edge silinmez, Defender ve Windows Update'e dokunulmaz.", 12, new Thickness(0, 2, 0, 0)));
        panel.Children.Add(Ui.Card(Ui.V(head, Ui.Divider(12, 6), list), padding: 16));
        return panel;
    }

    private async Task ApplyHard(Tweak t)
    {
        if (t.NeedsAdmin && !Admin.IsElevated)
        {
            Ui.Info("Bu ayar için yönetici izni gerekiyor. Üstteki 'Yönetici olarak aç' düğmesini kullan.");
            return;
        }
        if (!Ui.Confirm($"{t.Title}\n\n{t.WhatItDoes}\n\nEtkisi: {t.Effect}" + (t.Risk != null ? $"\n\nBedeli: {t.Risk}" : "") +
                        "\n\n'Geri al' sekmesinden geri alınabilir. Uygulansın mı?", "Sert mod")) return;
        try
        {
            var e = await Task.Run(t.Apply);
            if (e.NeedsReboot) Ui.Info("Uygulandı. Tam etkisi bilgisayarı yeniden başlatınca görülür.");
        }
        catch (Exception ex) { Ui.Error(ex); }
        Shell.Invalidate("journal"); Shell.Invalidate("scan"); Shell.Invalidate("tweaks");
        await ReloadAsync();
    }

    // ---------- Yardımcılar ----------

    private static (long, long) Ram()
    {
        var o = Wmi.Query("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem").FirstOrDefault();
        if (o is null) return (0, 0);
        long total = Wmi.Long(o, "TotalVisibleMemorySize") * 1024, free = Wmi.Long(o, "FreePhysicalMemory") * 1024;
        return (total - free, total);
    }

    private static UIElement Line(string text)
    {
        var dot = new Ellipse { Width = 6, Height = 6, Fill = Ui.B("Accent"), Margin = new Thickness(2, 6, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        var t = Ui.T(text, 13);
        var d = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(dot, Dock.Left);
        d.Children.Add(dot);
        d.Children.Add(t);
        return d;
    }

    private static Border Chip(string text)
    {
        var check = Ui.Icon(((char)0xE73E).ToString(), 11, "Accent");
        check.Margin = new Thickness(0, 1, 8, 0);
        return new Border
        {
            Background = Ui.B("AccentSoft"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 5, 12, 6),
            Margin = new Thickness(0, 0, 8, 8),
            Child = Ui.H(check, Ui.T(text, 12)),
        };
    }

    private static UIElement Ring(int score, string brush)
    {
        const double size = 128, stroke = 10;
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Ellipse { Stroke = Ui.B("LineStrong"), StrokeThickness = stroke });
        double r = (size - stroke) / 2, c = size / 2;
        double ang = Math.Min(score, 99.9) / 100 * 2 * Math.PI;
        var start = new Point(c, c - r);
        var end = new Point(c + r * Math.Sin(ang), c - r * Math.Cos(ang));
        var fig = new PathFigure { StartPoint = start };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, ang > Math.PI, SweepDirection.Clockwise, true));
        g.Children.Add(new Path
        {
            Data = new PathGeometry(new[] { fig }),
            Stroke = Ui.B(brush),
            StrokeThickness = stroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = ((SolidColorBrush)Ui.B(brush)).Color, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.55,
            },
        });
        var big = Ui.T(score.ToString(), 40, "Text", FontWeights.Bold);
        big.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        var num = Ui.V(big, Ui.Muted("/ 100", 11));
        num.HorizontalAlignment = HorizontalAlignment.Center;
        num.VerticalAlignment = VerticalAlignment.Center;
        foreach (TextBlock t in num.Children) t.HorizontalAlignment = HorizontalAlignment.Center;
        g.Children.Add(num);
        return g;
    }
}
