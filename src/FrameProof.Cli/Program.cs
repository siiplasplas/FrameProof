using System.Text;
using System.Text.Json;
using FrameProof.Core.Cleanup;
using FrameProof.Core.Games;
using FrameProof.Core.Optimize;
using FrameProof.Core.Processes;
using FrameProof.Core.Measure;
using FrameProof.Core.Scan;
using FrameProof.Core.Startup;
using FrameProof.Core.Tweaks;
using FrameProof.Core.Util;

Console.OutputEncoding = Encoding.UTF8;
var a = args.ToList();
string cmd = a.Count > 0 ? a[0].ToLowerInvariant() : "help";
bool Has(string flag) => a.Contains(flag, StringComparer.OrdinalIgnoreCase);
string? Opt(string flag)
{
    int i = a.FindIndex(x => x.Equals(flag, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < a.Count ? a[i + 1] : null;
}

try
{
    switch (cmd)
    {
        case "scan": Scan(); break;
        case "clean": await Clean(); break;
        case "tweaks": Tweaks(); break;
        case "tweak": ApplyTweak(); break;
        case "journal": ShowJournal(); break;
        case "undo": Undo(); break;
        case "startup": StartupCmd(); break;
        case "measure": await Measure(); break;
        case "parse": Parse(); break;
        case "runs": Runs(); break;
        case "compare": CompareCmd(); break;
        case "games": Games(); break;
        case "apps": Apps(); break;
        case "space": Space(); break;
        case "optimize": Optimize(); break;
        case "selftest": return SelfTest.Run(a.ElementAtOrDefault(1) ?? Path.Combine(Path.GetTempPath(), "frameproof-selftest"));
        default: Help(); break;
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("HATA: " + ex.Message);
    return 1;
}

void Help() => Console.WriteLine("""
FrameProof komut satırı (test ve ileri kullanım)
  scan [--json]                      Sistemi tara (hiçbir şeyi değiştirmez)
  clean                              Temizlenebilir alanı hesapla (silmez)
  clean --run id1,id2 --yes          Seçilenleri temizle
  tweaks                             Ayarları ve durumlarını listele
  tweak <id>                         Ayarı uygula (geri alınabilir)
  journal                            Yapılan değişiklikler
  undo <id|all>                      Değişikliği geri al
  startup                            Başlangıç uygulamaları
  startup off|on "<ad>"              Başlangıç öğesini kapat/aç
  measure <oyun.exe> <saniye> <etiket>   PresentMon ile ölç
  parse <dosya.csv> [etiket]         PresentMon CSV'sini özetle (kaydetmez)
  runs                               Kayıtlı ölçümler
  compare <etiketA> <etiketB>        İki etiketi istatistikle karşılaştır
  games                              Ekran kartları ve oyunların kart tercihi (değiştirmez)
  apps                               Arka planda açık programlar (kapatmaz)
  space                              Kurulu oyunlar, boyutları ve sıkıştırma durumu (değiştirmez)
  optimize                           'Optimize et' planını göster (uygulamaz)
""");

void Space()
{
    foreach (var g in GameLibrary.List())
    {
        var s = GameCompress.Measure(g.Folder);
        Console.WriteLine($"{g.Name,-32} {g.Store,-6} {Fmt.Bytes(s.Bytes),10}  diskte {Fmt.Bytes(s.OnDisk),10}  kazanılmış {Fmt.Bytes(s.Saved),9}  {g.Folder}");
    }
}

void Scan()
{
    var r = SystemScanner.Run();
    if (Has("--json"))
    {
        Console.WriteLine(JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        return;
    }
    foreach (var g in r.Findings.GroupBy(f => f.Category))
    {
        Console.WriteLine($"\n== {g.Key} ==");
        foreach (var f in g)
        {
            var mark = f.Severity switch { Severity.Good => "[ OK ]", Severity.Warn => "[UYAR]", Severity.Bad => "[SORUN]", _ => "[BİLGİ]" };
            Console.WriteLine($"{mark} {f.Title}: {f.Value}");
            if (!string.IsNullOrEmpty(f.Advice) && f.Severity != Severity.Good) Console.WriteLine($"        → {f.Advice}");
            if (f.FixTweakId != null) Console.WriteLine($"        (düzelt: tweak {f.FixTweakId})");
        }
    }
    Console.WriteLine($"\nSorun: {r.Count(Severity.Bad)} · Uyarı: {r.Count(Severity.Warn)} · Yönetici: {(Admin.IsElevated ? "evet" : "hayır")}");
}

async Task Clean()
{
    var targets = CleanupCatalog.Build();
    var run = Opt("--run");
    if (run is null)
    {
        long total = 0;
        foreach (var t in targets)
        {
            var an = t.Analyze();
            if (!an.Unknown && t.DefaultOn) total += an.Bytes;
            Console.WriteLine($"{(t.DefaultOn ? "[x]" : "[ ]")} {t.Id,-16} {Fmt.Bytes(an.Bytes),10}  {t.Name}{(an.Note != null ? "  (" + an.Note + ")" : "")}");
        }
        Console.WriteLine($"\nVarsayılan seçimle kazanılacak: {Fmt.Bytes(total)}");
        Console.WriteLine("Temizlemek için: clean --run user_temp,thumbs --yes");
        return;
    }
    if (!Has("--yes")) throw new InvalidOperationException("Silmek için --yes ekle.");
    var ids = run.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    long freed = 0;
    foreach (var id in ids)
    {
        var t = targets.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException("Bilinmeyen hedef: " + id);
        if (t.NeedsAdmin && !Admin.IsElevated) { Console.WriteLine($"{id}: yönetici izni gerekiyor, atlandı"); continue; }
        var r = await t.CleanAsync(CancellationToken.None);
        freed += r.FreedBytes;
        Console.WriteLine($"{id}: {Fmt.Bytes(r.FreedBytes)} ({r.Files} dosya){(r.Note != null ? " · " + r.Note : "")}");
    }
    Console.WriteLine($"Toplam açılan alan: {Fmt.Bytes(freed)}");
}

void Tweaks()
{
    foreach (var t in TweakCatalog.All)
    {
        Console.WriteLine($"{t.Id,-16} {t.StateText,-26} {t.Title}{(t.NeedsAdmin ? " [yönetici]" : "")}{(t.NeedsReboot ? " [yeniden başlat]" : "")}");
        Console.WriteLine($"                 {t.Effect}");
    }
}

void ApplyTweak()
{
    var id = a.ElementAtOrDefault(1) ?? throw new ArgumentException("tweak <id>");
    var t = TweakCatalog.Find(id) ?? throw new ArgumentException("Bilinmeyen ayar: " + id);
    var e = t.Apply();
    Console.WriteLine(e.Changes.Count == 0 ? "Zaten uygulanmış, değişiklik yok." : $"Uygulandı (geri almak için: undo {e.Id}){(e.NeedsReboot ? " · yeniden başlatınca etkin olur" : "")}");
}

void ShowJournal()
{
    var j = Journal.Load();
    if (j.Count == 0) { Console.WriteLine("Değişiklik yok."); return; }
    foreach (var e in j) Console.WriteLine($"{e.Id}  {e.AppliedAt:dd.MM.yyyy HH:mm}  {e.Title}  ({e.Changes.Count} değişiklik)");
}

void Undo()
{
    var id = a.ElementAtOrDefault(1) ?? throw new ArgumentException("undo <id|all>");
    if (id == "all") Journal.UndoAll(); else Journal.Undo(id);
    Console.WriteLine("Geri alındı.");
}

void StartupCmd()
{
    var items = StartupManager.List();
    var sub = a.ElementAtOrDefault(1);
    if (sub is "on" or "off")
    {
        var name = a.ElementAtOrDefault(2) ?? throw new ArgumentException("startup off \"<ad>\"");
        var it = items.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("Bulunamadı: " + name);
        var e = StartupManager.SetEnabled(it, sub == "on");
        Console.WriteLine($"{e.Title} (geri almak için: undo {e.Id})");
        return;
    }
    foreach (var i in items) Console.WriteLine($"{(i.Enabled ? "[açık]  " : "[kapalı]")} {i.Name,-30} {i.Source}");
}

async Task Measure()
{
    var proc = a.ElementAtOrDefault(1) ?? throw new ArgumentException("measure <oyun.exe> <saniye> <etiket>");
    int sec = int.Parse(a.ElementAtOrDefault(2) ?? "60");
    var label = a.ElementAtOrDefault(3) ?? "ölçüm";
    var pm = PresentMon.Locate() ?? throw new InvalidOperationException("PresentMon bulunamadı. Uygulamadaki FPS Ölçümü sekmesinden indir.");
    Console.WriteLine($"{proc} {sec} sn ölçülüyor…");
    var r = await PresentMon.CaptureAsync(pm, proc, sec, label, CancellationToken.None);
    Print(r);
}

void Parse()
{
    var path = a.ElementAtOrDefault(1) ?? throw new ArgumentException("parse <dosya.csv>");
    Print(Stats.Summarize(FrameCsv.Parse(path), a.ElementAtOrDefault(2) ?? "deneme"));
}

void Runs()
{
    foreach (var r in RunStore.Load())
        Console.WriteLine($"{r.Id}  {r.At:dd.MM HH:mm}  {r.Label,-14} {r.Process,-18} ort {Fmt.Num(r.AvgFps)} · 1% {Fmt.Num(r.Low1Fps)} · takılma %{Fmt.Num(r.StutterPct)}");
}

void CompareCmd()
{
    var x = a.ElementAtOrDefault(1) ?? throw new ArgumentException("compare <A> <B>");
    var y = a.ElementAtOrDefault(2) ?? throw new ArgumentException("compare <A> <B>");
    foreach (var c in RunStore.CompareLabels(x, y)) Console.WriteLine(c.Text);
}

static void Print(RunSummary r)
{
    Console.WriteLine($"Süreç: {r.Process} · {Fmt.Num(r.Seconds)} sn · {r.Frames} kare");
    Console.WriteLine($"Ortalama FPS: {Fmt.Num(r.AvgFps)}   1% low: {Fmt.Num(r.Low1Fps)}   0.1% low: {Fmt.Num(r.Low01Fps)}");
    Console.WriteLine($"Medyan kare süresi: {Fmt.Num(r.MedianMs, 2)} ms   Takılma: %{Fmt.Num(r.StutterPct, 2)}   Darboğaz: {r.Bottleneck}");
}

void Games()
{
    foreach (var g in Gpus.List())
        Console.WriteLine($"GPU  {g.Name,-40} vendor={g.VendorId:X4} vram={Fmt.Bytes(g.DedicatedMemory)} luid={g.LuidKey} {(g.Integrated ? "dahili" : "harici")}");
    Console.WriteLine($"Hibrit: {Gpus.IsHybrid}");
    foreach (var g in GameGpu.List())
        Console.WriteLine($"{g.Pref,-16} {g.Name,-30} {g.Path}");
}

void Apps()
{
    foreach (var g in BackgroundApps.List().Take(25))
        Console.WriteLine($"{(g.Closable ? "kapatılabilir" : "-"),-14} {Fmt.Bytes(g.RamBytes),10}  x{g.Count,-3} {g.Name}");
}

void Optimize()
{
    var p = Optimizer.Build();
    Console.WriteLine($"Temizlik ({Fmt.Bytes(p.Bytes)}):");
    foreach (var c in p.Clean) Console.WriteLine($"  {Fmt.Bytes(c.Bytes),10}  {c.Target.Name}");
    Console.WriteLine("Ayarlar:");
    foreach (var t in p.Tweaks) Console.WriteLine($"  {t.Id,-18} {t.Title}");
    if (p.SkippedNoAdmin.Count > 0) Console.WriteLine("Yönetici izni olmadığı için atlanır: " + string.Join(", ", p.SkippedNoAdmin));
    Console.WriteLine("(Hiçbir şey uygulanmadı; bu yalnızca plan.)");
}
