using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FrameProof.Core.Packages;

internal static class PackageTests
{
    public static void Run(Suite suite)
    {
        suite.Run("Package install + undo restores bytes and removes new files", root =>
        {
            var target = Target(root);
            File.WriteAllText(Path.Combine(target, "existing.txt"), "original");
            var (zip, recipe) = Zip(root, ("existing.txt", "replacement"), ("sub/new.txt", "new"));
            var installer = Installer(root);
            var result = installer.Install(zip, target, recipe, "game-1");
            Suite.Check(File.ReadAllText(Path.Combine(target, "existing.txt")) == "replacement", "Not installed");
            installer.Undo(result.TransactionId);
            installer.Undo(result.TransactionId);
            Suite.Check(File.ReadAllText(Path.Combine(target, "existing.txt")) == "original", "Original not restored");
            Suite.Check(!File.Exists(Path.Combine(target, "sub/new.txt")), "New file left behind");
        });
        suite.Run("Package hash/version mismatch never touches target", root =>
        {
            var target = Target(root);
            var (zip, recipe) = Zip(root, ("file.txt", "new"));
            var installer = Installer(root);
            Suite.Throws<InvalidDataException>(() => installer.Install(zip, target, recipe with { Sha256 = new string('0', 64) }, "game-1"));
            Suite.Throws<InvalidDataException>(() => installer.Install(zip, target, recipe, "game-2"));
            Suite.Check(!Directory.EnumerateFileSystemEntries(target).Any(), "Target changed");
        });
        suite.Run("Traversal and case-duplicate archives rejected before mutation", root =>
        {
            var target = Target(root);
            var installer = Installer(root);
            var (zip, recipe) = Zip(root, ("good.txt", "fine"), ("../escaped.txt", "bad"));
            Suite.Throws<InvalidDataException>(() => installer.Install(zip, target, recipe, "game-1"));
            Suite.Check(!File.Exists(Path.Combine(root, "escaped.txt")) && !Directory.EnumerateFileSystemEntries(target).Any(), "Traversal wrote files");
            var duplicate = Zip(root, ("same.txt", "one"), ("SAME.txt", "two"));
            Suite.Throws<InvalidDataException>(() => installer.Install(duplicate.Item1, target, duplicate.Item2, "game-1"));
        });
        suite.Run("Expanded byte limit enforced", root =>
        {
            var (zip, recipe) = Zip(root, ("large.txt", new string('x', 1000)));
            Suite.Throws<InvalidDataException>(() => Installer(root).Install(zip, Target(root), recipe with { MaximumExpandedBytes = 5 }, "game-1"));
        });
        suite.Run("Cancellation after mutation rolls back originals", root =>
        {
            var target = Target(root);
            File.WriteAllText(Path.Combine(target, "first.txt"), "original");
            var (zip, recipe) = Zip(root, ("first.txt", "replacement"), ("second.txt", "new"));
            using var cancel = new CancellationTokenSource();
            var progress = new InlineProgress(p => { if (p.Stage == "Kuruluyor") cancel.Cancel(); });
            var installer = Installer(root);
            Suite.Throws<OperationCanceledException>(() => installer.Install(zip, target, recipe, "game-1", cancel.Token, progress));
            Suite.Check(File.ReadAllText(Path.Combine(target, "first.txt")) == "original" && !File.Exists(Path.Combine(target, "second.txt")), "Cancellation left changes");
            Suite.Check(installer.FindInterruptedTransactions().Count == 0, "Rollback unfinished");
        });
        suite.Run("Undo refuses external modifications without overwriting", root =>
        {
            var target = Target(root);
            File.WriteAllText(Path.Combine(target, "file.txt"), "original");
            var (zip, recipe) = Zip(root, ("file.txt", "replacement"));
            var installer = Installer(root);
            var result = installer.Install(zip, target, recipe, "game-1");
            File.WriteAllText(Path.Combine(target, "file.txt"), "user edit");
            Suite.Throws<PackageConflictException>(() => installer.Undo(result.TransactionId));
            Suite.Check(File.ReadAllText(Path.Combine(target, "file.txt")) == "user edit", "User edit lost");
        });
        suite.Run("Persisted interrupted transaction recovers in new instance", root =>
        {
            var target = Target(root);
            File.WriteAllText(Path.Combine(target, "file.txt"), "original");
            var (zip, recipe) = Zip(root, ("file.txt", "replacement"));
            var result = Installer(root).Install(zip, target, recipe, "game-1");
            var manifest = Path.Combine(root, "transactions", result.TransactionId, "transaction.json");
            var tx = JsonSerializer.Deserialize<LocalPackageInstaller.Transaction>(File.ReadAllText(manifest))!;
            tx.State = "Installing";
            File.WriteAllText(manifest, JsonSerializer.Serialize(tx));
            var reopened = Installer(root);
            Suite.Check(reopened.FindInterruptedTransactions().Contains(result.TransactionId), "Interruption not discovered");
            reopened.Recover(result.TransactionId);
            Suite.Check(File.ReadAllText(Path.Combine(target, "file.txt")) == "original", "Recovery lost original");
        });
        suite.Run("Required running-game guard blocks all writes", root =>
        {
            var target = Target(root);
            var (zip, recipe) = Zip(root, ("file.txt", "new"));
            var installer = new LocalPackageInstaller(Path.Combine(root, "transactions"), _ => throw new InvalidOperationException("Game running"));
            Suite.Throws<InvalidOperationException>(() => installer.Install(zip, target, recipe, "game-1"));
            Suite.Check(!Directory.EnumerateFileSystemEntries(target).Any(), "Guard ignored");
        });
        suite.Run("Crash-left replacement temp does not prevent recovery", root =>
        {
            var target = Target(root);
            var destination = Path.Combine(target, "file.txt");
            File.WriteAllText(destination, "original");
            var (zip, recipe) = Zip(root, ("file.txt", "replacement"));
            var result = Installer(root).Install(zip, target, recipe, "game-1");
            var temp = destination + ".frameproof-" + result.TransactionId + ".tmp";
            File.WriteAllText(temp, "partial restore bytes");
            Installer(root).Recover(result.TransactionId);
            Suite.Check(File.ReadAllText(destination) == "original" && !File.Exists(temp), "Crash temp prevented recovery");
        });
        suite.Run("Final-file cancellation still rolls back", root =>
        {
            var target = Target(root);
            var (zip, recipe) = Zip(root, ("last.txt", "new"));
            using var cancel = new CancellationTokenSource();
            Suite.Throws<OperationCanceledException>(() => Installer(root).Install(zip, target, recipe, "game-1", cancel.Token,
                new InlineProgress(p => { if (p.Stage == "Kuruluyor") cancel.Cancel(); })));
            Suite.Check(!File.Exists(Path.Combine(target, "last.txt")), "Final cancellation ignored");
        });
        suite.Run("Symbolic-link ZIP entry is rejected", root =>
        {
            var zip = Path.Combine(root, "link.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("link");
                entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                using var writer = new StreamWriter(entry.Open()); writer.Write("../outside");
            }
            var recipe = new PackageRecipe("link", "1", "game-1", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip))));
            Suite.Throws<InvalidDataException>(() => Installer(root).Install(zip, Target(root), recipe, "game-1"));
        });
        suite.Run("Older package cannot undo newer same-byte ownership", root =>
        {
            var target = Target(root);
            var (zip, recipe) = Zip(root, ("shared.txt", "identical"));
            var installer = Installer(root);
            var older = installer.Install(zip, target, recipe, "game-1");
            var newer = installer.Install(zip, target, recipe with { PackageId = "second-package" }, "game-1");
            Suite.Throws<PackageConflictException>(() => installer.Undo(older.TransactionId));
            Suite.Check(File.ReadAllText(Path.Combine(target, "shared.txt")) == "identical", "Newer file removed");
            installer.Undo(newer.TransactionId);
            installer.Undo(older.TransactionId);
            Suite.Check(!File.Exists(Path.Combine(target, "shared.txt")), "Ordered undo incomplete");
        });
        suite.Run("Completed package releases staged copies", root =>
        {
            var (zip, recipe) = Zip(root, ("file.txt", "new"));
            var installer = Installer(root);
            var result = installer.Install(zip, Target(root), recipe, "game-1");
            var home = Path.Combine(root, "transactions", result.TransactionId);
            Suite.Check(!Directory.EnumerateFiles(home, "stage-*").Any(), "Installed package retained staging");
            installer.Undo(result.TransactionId);
            Suite.Check(!Directory.EnumerateFiles(home, "stage-*").Any(), "Rolled-back package retained staging");
        });
    }
    private static LocalPackageInstaller Installer(string root) => new(Path.Combine(root, "transactions"), _ => { });
    private static string Target(string root) { var path = Path.Combine(root, "game"); Directory.CreateDirectory(path); return path; }
    private static (string, PackageRecipe) Zip(string root, params (string Name, string Content)[] files)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (name, content) in files)
            { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(content); }
        using var stream = File.OpenRead(path);
        return (path, new("test-package", "fix-1", "game-1", Convert.ToHexString(SHA256.HashData(stream))));
    }
    private sealed class InlineProgress(Action<PackageProgress> action) : IProgress<PackageProgress>
    { public void Report(PackageProgress value) => action(value); }
}
