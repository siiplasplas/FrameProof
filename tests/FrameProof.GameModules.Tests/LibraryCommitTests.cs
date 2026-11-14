using FrameProof.Core.Library;

internal static class LibraryCommitTests
{
    public static void Run(Suite suite)
    {
        suite.Run("Ready commit preserves custom identity and atomically publishes paired versions", root =>
        {
            var exe = Suite.Exe(root, "game/game.exe");
            var store = new GameLibraryStore(Path.Combine(root, "library.json"));
            var manual = store.AddManual(exe, "My custom title");
            var ready = store.RegisterInstalled("catalog-id", "Catalog title", Path.GetDirectoryName(exe)!,
                new() { ExecutablePath = exe, Arguments = new[] { "--friend-mode" } }, "game-1", "fix-1");
            var persisted = new GameLibraryStore(store.FilePath).Load().Single();
            Suite.Check(ready.Id == manual.Id && persisted.Id == manual.Id && persisted.Name == "My custom title", "Custom identity overwritten");
            Suite.Check(persisted.CatalogId == "catalog-id" && persisted.InstalledGameVersion == "game-1"
                && persisted.InstalledFixVersion == "fix-1" && persisted.CanLaunch, "Ready state incomplete");
            Suite.Check(persisted.Launch!.Arguments.SequenceEqual(new[] { "--friend-mode" }), "Launch arguments lost");
            var repeated = store.RegisterInstalled("catalog-id", "Catalog title", Path.GetDirectoryName(exe)!,
                persisted.Launch, "game-1", "fix-1");
            Suite.Check(repeated.Id == manual.Id && store.Load().Count == 1, "Commit retry created duplicate");
        });
        suite.Run("Ready commit validation failures leave previous library unchanged", root =>
        {
            var exe = Suite.Exe(root, "game/game.exe");
            var store = new GameLibraryStore(Path.Combine(root, "library.json"));
            store.RegisterInstalled("catalog-id", "Title", Path.GetDirectoryName(exe)!, new() { ExecutablePath = exe }, "1", "1");
            var before = File.ReadAllBytes(store.FilePath);
            Suite.Throws<InvalidOperationException>(() => store.RegisterInstalled("other-catalog", "Other", Path.GetDirectoryName(exe)!,
                new() { ExecutablePath = exe }, "2", "2"));
            Suite.Throws<ArgumentException>(() => store.RegisterInstalled("catalog-id", "Title", root,
                new() { ExecutablePath = Suite.Exe(Path.Combine(root, "sibling"), "game.exe") }, "", "2"));
            Suite.Check(before.SequenceEqual(File.ReadAllBytes(store.FilePath)), "Rejected commit changed stored readiness");
        });
        suite.Run("Concurrent readers never observe half of readiness pair", root =>
        {
            var exe = Suite.Exe(root, "game/game.exe");
            var store = new GameLibraryStore(Path.Combine(root, "library.json"));
            var profile = new GameLaunchProfile { ExecutablePath = exe };
            store.RegisterInstalled("catalog", "Title", Path.GetDirectoryName(exe)!, profile, "0", "0");
            Parallel.Invoke(
                () => { for (var i = 1; i <= 20; i++) store.RegisterInstalled("catalog", "Title", Path.GetDirectoryName(exe)!, profile, i.ToString(), i.ToString()); },
                () => { for (var i = 0; i < 20; i++) { var entry = new GameLibraryStore(store.FilePath).Load().Single(); Suite.Check(entry.InstalledGameVersion == entry.InstalledFixVersion, "Half committed pair observed"); } });
        });
        suite.Run("Pending installation blocks launch until atomic ready commit", root =>
        {
            var exe = Suite.Exe(root, "game/game.exe");
            var store = new GameLibraryStore(Path.Combine(root, "library.json"));
            var entry = store.AddManual(exe, "My title");
            store.SetPackageVersions(entry.Id, "catalog", "old", "old-fix");
            var pending = store.SetInstallationPending(entry.Id, true);
            Suite.Check(pending.InstallationPending && !pending.CanLaunch && pending.Name == "My title"
                && pending.InstalledGameVersion == "old" && pending.InstalledFixVersion == "old-fix", "Pending changed old metadata");
            Suite.Throws<InvalidOperationException>(() => GameLauncher.Start(pending));
            var restored = store.SetInstallationPending(entry.Id, false);
            Suite.Check(restored.CanLaunch && restored.InstalledGameVersion == "old", "Rollback readiness lost");
            store.SetInstallationPending(entry.Id, true);
            var ready = store.RegisterInstalled("catalog", "Store title", Path.GetDirectoryName(exe)!, new() { ExecutablePath = exe }, "new", "new-fix");
            Suite.Check(!ready.InstallationPending && ready.CanLaunch && ready.Id == entry.Id && ready.Name == "My title"
                && ready.InstalledGameVersion == "new" && ready.InstalledFixVersion == "new-fix", "Ready commit did not clear pending with versions");
        });
    }
}
