using FrameProof.Core.Games;
using FrameProof.Core.Library;

var suite = new Suite();
suite.Run("Manual add normalizes paths and deduplicates", root =>
{
    var exe = Suite.Exe(root, "game/game.exe");
    var store = new GameLibraryStore(Path.Combine(root, "library.json"));
    var one = store.AddManual(exe, "My game");
    var two = store.AddManual(exe.Replace('\\', '/'));
    Suite.Check(one.Id == two.Id && store.Load().Count == 1, "Duplicate entry");
    Suite.Check(new GameLibraryStore(store.FilePath).Load().Single().Name == "My game", "Persistence lost");
});
suite.Run("Discovery preserves custom name, launch and versions", root =>
{
    var exe = Suite.Exe(root, "game/bin/game.exe");
    var store = new GameLibraryStore(Path.Combine(root, "library.json"));
    var game = store.AddManual(exe, "Custom name");
    store.SetPackageVersions(game.Id, "catalog-game", "1.0", "fix-1");
    var discovered = store.MergeDiscovered(new[] { new InstalledGame("Store title", Path.Combine(root, "game"), "Steam") }).Single();
    Suite.Check(discovered.Id == game.Id && discovered.Name == "Custom name" && discovered.Source == "Steam", "Metadata changed");
    Suite.Check(discovered.InstalledFixVersion == "fix-1" && discovered.Launch?.ExecutablePath == exe, "Profile lost");
    File.Delete(exe);
    Directory.Delete(Path.Combine(root, "game", "bin"));
    Directory.Delete(Path.Combine(root, "game"));
    var missing = store.MergeDiscovered(Array.Empty<InstalledGame>()).Single();
    Suite.Check(!missing.IsInstalled && !missing.CanLaunch, "Missing installation removed or ready");
});
suite.Run("Remove forgets entry without deleting files", root =>
{
    var exe = Suite.Exe(root, "game/game.exe");
    var store = new GameLibraryStore(Path.Combine(root, "library.json"));
    var game = store.AddManual(exe);
    Suite.Check(store.Remove(game.Id) && store.Load().Count == 0 && File.Exists(exe), "Removal touched game");
});
suite.Run("Corrupt library is not silently replaced", root =>
{
    var path = Path.Combine(root, "library.json");
    File.WriteAllText(path, "broken-json");
    Suite.Throws<InvalidDataException>(() => new GameLibraryStore(path).AddManual(Suite.Exe(root, "game/game.exe")));
    Suite.Check(File.ReadAllText(path) == "broken-json", "Corrupt source was overwritten");
});
suite.Run("Launch validation rejects missing EXE, bad directory and outside profile", root =>
{
    Suite.Throws<FileNotFoundException>(() => GameLauncher.Validate(new() { ExecutablePath = Path.Combine(root, "none.exe") }));
    var exe = Suite.Exe(root, "game/game.exe");
    Suite.Throws<DirectoryNotFoundException>(() => GameLauncher.Validate(new() { ExecutablePath = exe, WorkingDirectory = Path.Combine(root, "missing") }));
    var store = new GameLibraryStore(Path.Combine(root, "library.json"));
    var game = store.AddManual(exe);
    Suite.Throws<ArgumentException>(() => store.SetLaunchProfile(game.Id, new() { ExecutablePath = Suite.Exe(root, "other/game.exe") }));
});
suite.Run("Concurrent store instances do not lose additions", root =>
{
    var path = Path.Combine(root, "library.json");
    var exes = Enumerable.Range(0, 12).Select(i => Suite.Exe(root, $"game{i}/game.exe")).ToArray();
    Parallel.ForEach(exes, exe => new GameLibraryStore(path).AddManual(exe));
    Suite.Check(new GameLibraryStore(path).Load().Count == exes.Length, "Concurrent write lost records");
});
suite.Run("Relocating a game preserves identity and invalidates unverified versions", root =>
{
    var store = new GameLibraryStore(Path.Combine(root, "library.json"));
    var game = store.AddManual(Suite.Exe(root, "old/game.exe"), "My game");
    store.SetPackageVersions(game.Id, "catalog", "old-version", "old-fix");
    var relocated = store.Relocate(game.Id, Suite.Exe(root, "new/game.exe"));
    Suite.Check(relocated.Id == game.Id && relocated.Name == "My game" && relocated.CatalogId == "catalog" && relocated.CanLaunch, "Identity/profile lost");
    Suite.Check(relocated.InstalledGameVersion is null && relocated.InstalledFixVersion is null, "Unverified versions carried to new files");
    var other = store.AddManual(Suite.Exe(root, "other/game.exe"));
    Suite.Throws<InvalidOperationException>(() => store.Relocate(game.Id, other.Launch!.ExecutablePath));
    Suite.Check(store.Load().Count == 2, "Relocation duplicate corrupted library");
});
PackageTests.Run(suite);
DownloadTests.Run(suite);
LiveTransportTests.Run(suite);
CatalogTests.Run(suite);
InstallationTests.Run(suite);
LibraryCommitTests.Run(suite);
Console.WriteLine($"RESULT: {suite.Passed} passed, {suite.Failed} failed");
return suite.Failed == 0 ? 0 : 1;

internal sealed class Suite
{
    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public void Run(string name, Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "FrameProof-module-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { test(root); Passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { Failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        finally { try { Directory.Delete(root, recursive: true); } catch (Exception ex) { Console.WriteLine("Cleanup: " + ex.Message); } }
    }
    public static string Exe(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test fixture, never executed");
        return path;
    }
    public static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
