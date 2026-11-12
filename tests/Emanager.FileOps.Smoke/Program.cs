using Emanager.App;

var root = Path.Combine(Path.GetTempPath(), "emanager-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
try
{
    var srcDir = Path.Combine(root, "source");
    var dstDir = Path.Combine(root, "target");
    Directory.CreateDirectory(srcDir);
    Directory.CreateDirectory(dstDir);
    var source = Path.Combine(srcDir, "sample.bin");
    var destination = Path.Combine(dstDir, "sample.bin");
    var payload = new byte[1024 * 1024 + 17];
    new Random(1234).NextBytes(payload);
    await File.WriteAllBytesAsync(source, payload);
    await SafeFileOperations.CopyFileAsync(source, dstDir);
    Check((await File.ReadAllBytesAsync(destination)).SequenceEqual(payload), "binary copy is byte-identical");
    await File.WriteAllTextAsync(destination, "KEEP");
    await ThrowsAsync<IOException>(() => SafeFileOperations.CopyFileAsync(source, dstDir), "destination is not overwritten");
    Check(await File.ReadAllTextAsync(destination) == "KEEP", "existing contents preserved");
    File.Delete(destination);

    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => SafeFileOperations.CopyFileAsync(source, dstDir, canceled.Token), "pre-canceled copy");
    }
    Check(!File.Exists(destination), "cancel does not publish output");
    long progressBytes = 0;
    using (var cancelDuringCopy = new CancellationTokenSource())
    {
        await ThrowsAsync<OperationCanceledException>(
            () => SafeFileOperations.CopyFileAsync(source, dstDir, cancelDuringCopy.Token, bytes =>
            {
                progressBytes = bytes;
                cancelDuringCopy.Cancel();
            }), "cancel during copy");
    }
    Check(progressBytes > 0 && progressBytes < payload.Length, "progress reported before cancellation");
    Check(!File.Exists(destination), "mid-copy cancellation does not publish output");
    Check(!Directory.EnumerateFiles(dstDir, ".emanager-copy-*.tmp").Any(), "mid-copy cancellation cleans staging");
    Check(!Directory.EnumerateFiles(dstDir, ".emanager-copy-*.tmp").Any(), "no staging leftovers");
    await ThrowsAsync<IOException>(() => SafeFileOperations.CopyFileAsync(source, srcDir), "same source and destination");
    await ThrowsAsync<FileNotFoundException>(() => SafeFileOperations.CopyFileAsync(Path.Combine(srcDir, "missing"), dstDir), "missing source");
    await ThrowsAsync<DirectoryNotFoundException>(() => SafeFileOperations.CopyFileAsync(source, Path.Combine(root, "missing")), "missing target directory");
    await ThrowsAsync<NotSupportedException>(() => SafeFileOperations.CopyFileAsync(srcDir, dstDir), "source directory blocked");

    // A competing destination must never be replaced, including when created
    // after staging starts but before the final rename.
    await ThrowsAsync<IOException>(() => SafeFileOperations.CopyFileAsync(source, dstDir, default, bytes =>
    {
        if (!File.Exists(destination)) File.WriteAllText(destination, "RACE-WINNER");
    }), "concurrent destination creation is preserved");
    Check(await File.ReadAllTextAsync(destination) == "RACE-WINNER", "racing writer data preserved");
    File.Delete(destination);
    Check(!Directory.EnumerateFiles(dstDir, ".emanager-copy-*.tmp").Any(), "race cleans staging");

    // An exception raised by a UI progress observer must not leave a partial
    // published file or a temporary staging artifact.
    await ThrowsAsync<InvalidOperationException>(() => SafeFileOperations.CopyFileAsync(source, dstDir, default,
        _ => throw new InvalidOperationException("observer failed")), "observer failure propagates");
    Check(!File.Exists(destination), "observer failure does not publish");
    Check(!Directory.EnumerateFiles(dstDir, ".emanager-copy-*.tmp").Any(), "observer failure cleans staging");

    // Mutation during a copy is detected at the final consistency check.
    bool changed = false;
    await ThrowsAsync<IOException>(() => SafeFileOperations.CopyFileAsync(source, dstDir, default, _ =>
    {
        if (changed) return;
        changed = true;
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-5));
    }), "source timestamp mutation blocked");
    Check(!File.Exists(destination), "mutated source not published");
    Check(!Directory.EnumerateFiles(dstDir, ".emanager-copy-*.tmp").Any(), "source mutation cleans staging");
    await File.WriteAllBytesAsync(source, payload);

    // Same-length edits with a restored timestamp evade metadata-only checks.
    var savedTime = File.GetLastWriteTimeUtc(source);
    bool contentMutated = false;
    await ThrowsAsync<IOException>(() => SafeFileOperations.CopyFileAsync(source, dstDir, default, _ =>
    {
        if (contentMutated) return;
        contentMutated = true;
        using var stream = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        stream.WriteByte((byte)(payload[0] ^ 0xff));
        stream.Flush(true);
        File.SetLastWriteTimeUtc(source, savedTime);
    }), "same-size timestamp-restored mutation blocked");
    Check(!File.Exists(destination), "content mutation not published");
    Check(!Directory.EnumerateFiles(dstDir, ".emanager-copy-*.tmp").Any(), "content mutation staging cleaned");
    await File.WriteAllBytesAsync(source, payload);

    var empty = Path.Combine(srcDir, "empty.txt");
    await File.WriteAllBytesAsync(empty, []);
    await SafeFileOperations.CopyFileAsync(empty, dstDir);
    Check(new FileInfo(Path.Combine(dstDir, "empty.txt")).Length == 0, "zero-byte file");
    if (!OperatingSystem.IsWindows())
    {
        var link = Path.Combine(srcDir, "link.txt");
        File.CreateSymbolicLink(link, source);
        await ThrowsAsync<NotSupportedException>(() => SafeFileOperations.CopyFileAsync(link, dstDir), "source symlink blocked");
        var dangling = Path.Combine(dstDir, "sample.bin");
        File.CreateSymbolicLink(dangling, Path.Combine(root, "nonexistent-target"));
        await ThrowsAsync<IOException>(() => SafeFileOperations.CopyFileAsync(source, dstDir), "dangling destination symlink not replaced");
        Check(File.GetAttributes(dangling).HasFlag(FileAttributes.ReparsePoint), "dangling link preserved");
        File.Delete(dangling);
        var linkedDestination = Path.Combine(root, "linked-target");
        Directory.CreateSymbolicLink(linkedDestination, dstDir);
        await ThrowsAsync<NotSupportedException>(() => SafeFileOperations.CopyFileAsync(source, linkedDestination), "linked destination directory blocked");
        var linkedSourceDir = Path.Combine(root, "linked-source");
        Directory.CreateSymbolicLink(linkedSourceDir, srcDir);
        await ThrowsAsync<NotSupportedException>(() => SafeFileOperations.CopyFileAsync(Path.Combine(linkedSourceDir, "sample.bin"), dstDir), "linked source parent blocked");
        Check(!Directory.EnumerateFiles(dstDir, ".emanager-copy-*.tmp").Any(), "link rejection leaves no staging files");
    }
    // Read-only Linux filesystem listing: preserve dangling symlinks and ordinary files.
    var listing = Path.Combine(root, "listing");
    Directory.CreateDirectory(listing);
    await File.WriteAllTextAsync(Path.Combine(listing, "normal.txt"), "hello");
    Directory.CreateDirectory(Path.Combine(listing, "folder"));
    var listed = await FileSystem.ListAsync(listing, CancellationToken.None);
    Check(listed.Any(x => x.Name == "normal.txt" && !x.IsDirectory && !x.IsLink), "ordinary file is listed");
    Check(listed.Any(x => x.Name == "folder" && x.IsDirectory), "directory is listed");
    if (OperatingSystem.IsLinux())
    {
        var brokenLink = Path.Combine(listing, "dangling-link");
        File.CreateSymbolicLink(brokenLink, Path.Combine(root, "does-not-exist"));
        var withBroken = await FileSystem.ListAsync(listing, CancellationToken.None);
        Check(withBroken.Any(x => x.Name == "dangling-link" && x.IsLink && x.Kind == "Bağlantı"),
            "dangling symlink remains visible");
        Check(FileSystem.ReadEntry(brokenLink)?.IsLink == true, "broken symlink metadata recognized");
        Check(!Directory.Exists(brokenLink), "test link is genuinely dangling");
    }
    using (var scanCancelled = new CancellationTokenSource())
    {
        scanCancelled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => FileSystem.ListAsync(listing, scanCancelled.Token),
            "pre-cancelled directory scan");
    }

    // Language is independent of paths and of persisted sort/view identifiers.
    UiText.Use("en");
    Check(UiText.T("HIZLI ERİŞİM") == "QUICK ACCESS", "English sidebar headings");
    Check(UiText.T("Aç") == "Open", "English context menu labels");
    Check(UiText.T("Detaylar") == "Details", "English view options");
    Check(UiText.Canonical("Details") == "Detaylar", "localized view keeps canonical preference");
    Check(UiText.Canonical("Modified") == "Değiştirilme", "localized sort keeps canonical preference");
    Check(UiText.F("{0} öğe görüntüleniyor", 7) == "Showing 7 items", "English dynamic UI strings");
    Check(UiText.T("/home/adminmehmet/Masaüstü") == "/home/adminmehmet/Masaüstü",
        "physical paths are never translated");
    Check(FileSystem.ReadEntry(Path.Combine(listing, "normal.txt"))?.Kind == "TXT file",
        "file type descriptions in English");
    var settings = new Preferences { Language = "en", SortBy = "Değiştirilme", ViewMode = "Detaylar" };
    var serializedSettings = System.Text.Json.JsonSerializer.Serialize(settings);
    var restoredSettings = System.Text.Json.JsonSerializer.Deserialize<Preferences>(serializedSettings);
    Check(restoredSettings?.Language == "en", "English language setting survives JSON roundtrip");
    Check(restoredSettings?.SortBy == "Değiştirilme" && restoredSettings.ViewMode == "Detaylar",
        "sort/view preferences survive English session");
    UiText.Use("tr");
    Check(UiText.T("HIZLI ERİŞİM") == "HIZLI ERİŞİM", "Turkish original preserved");
    Check(FileSystem.ReadEntry(Path.Combine(listing, "normal.txt"))?.Kind == "TXT dosyası",
        "file type descriptions in Turkish");
    UiText.Use("fr");
    Check(UiText.Language == "tr", "unknown locale falls back to Turkish");

    // Storage sidebar must not surface virtual mounts such as /proc and /dev.
    Check(FileSystem.IsUserStorageMount("/"), "root is a user storage mount");
    Check(FileSystem.IsUserStorageMount("/run/media/user/USB"), "removable media mount is allowed");
    Check(!FileSystem.IsUserStorageMount("/proc"), "proc is excluded");
    Check(!FileSystem.IsUserStorageMount("/dev"), "dev is excluded");
    Check(!FileSystem.IsUserStorageMount("/boot/efi"), "boot firmware mount is excluded");
    Check(FileSystem.UnescapeMountField(@"/run/media/name\040with\040spaces") == "/run/media/name with spaces", "mountinfo spaces decoded");
    // Unmounted data partitions must appear; boot/swap/loop must not.
    var blockJson = """
    {"blockdevices":[
      {"name":"loop0","path":"/dev/loop0","type":"loop","fstype":"squashfs","size":4096,"rm":false},
      {"name":"sda","path":"/dev/sda","type":"disk","fstype":null,"size":320000000000,"rm":false,"children":[
        {"name":"sda5","path":"/dev/sda5","type":"part","fstype":"ntfs","size":320000000000,
         "mountpoints":[null],"rm":false}]},
      {"name":"sdb","path":"/dev/sdb","type":"disk","fstype":null,"size":1000000000000,"rm":false,"children":[
        {"name":"sdb1","path":"/dev/sdb1","type":"part","fstype":"xfs","size":1000000000000,
         "mountpoints":[null],"rm":false}]},
      {"name":"nvme0n1","path":"/dev/nvme0n1","type":"disk","fstype":null,"size":500000000000,"rm":false,"children":[
        {"name":"nvme0n1p1","path":"/dev/nvme0n1p1","type":"part","fstype":"vfat","size":600000000,
         "mountpoints":["/boot/efi"],"rm":false},
        {"name":"nvme0n1p2","path":"/dev/nvme0n1p2","type":"part","fstype":"ext4","size":2000000000,
         "mountpoints":["/boot"],"rm":false},
        {"name":"nvme0n1p3","path":"/dev/nvme0n1p3","type":"part","fstype":"btrfs","label":"fedora",
         "size":490000000000,"mountpoints":["/home","/"],"rm":false}]}
    ]}
    """;
    var dataDisks = StorageDevices.Parse(blockJson);
    Check(dataDisks.Count == 3, "only three user-facing storage partitions");
    Check(dataDisks.Any(x => x.Device == "/dev/sda5" && !x.IsMounted), "unmounted NTFS partition discovered");
    Check(dataDisks.Any(x => x.Device == "/dev/sdb1" && !x.IsMounted), "unmounted XFS partition discovered");
    Check(dataDisks.Any(x => x.Device == "/dev/nvme0n1p3" && x.MountPoint == "/"),
        "Btrfs root deduplicated and preferred");
    Check(dataDisks.All(x => x.Device is not ("/dev/loop0" or "/dev/nvme0n1p1" or "/dev/nvme0n1p2")),
        "virtual and boot volumes excluded");

    if (OperatingSystem.IsLinux())
    {
        var drives = FileSystem.Volumes();
        Check(drives.All(x => x.Device.StartsWith("/dev/", StringComparison.Ordinal)), "sidebar sources are devices");
        Check(drives.All(x => !x.IsMounted || FileSystem.IsUserStorageMount(x.MountPoint) || x.MountPoint == "/home"), "sidebar mounted paths filtered");
        Check(drives.Select(x => x.Device).Distinct(StringComparer.Ordinal).Count() == drives.Count, "no duplicate block device");
    }

    // Network URI parser: never accept credentials, unexpected schemes or traversal.
    Check(NetworkLocations.TryNormalize("smb://nas/shared", out var smb) && smb.StartsWith("smb://nas/shared"), "SMB share URI accepted");
    Check(NetworkLocations.TryNormalize("sftp://alice@server/home/alice", out var sftp) && sftp.StartsWith("sftp://alice@server"), "SFTP URI accepted");
    Check(!NetworkLocations.TryNormalize("smb://nas", out _), "SMB host without share rejected");
    Check(!NetworkLocations.TryNormalize("http://example.com", out _), "unexpected URI scheme rejected");
    Check(!NetworkLocations.TryNormalize("sftp://alice:secret@server/", out _), "password in URI rejected");
    Check(!NetworkLocations.TryNormalize("sftp://alice%3Asecret@server/", out _), "escaped password in URI rejected");
    Check(!NetworkLocations.TryNormalize("smb://nas/share?password=secret", out _), "query credentials rejected");
    Check(!NetworkLocations.TryNormalize("smb://nas/share/../secret", out _), "path traversal rejected");
    Check(NetworkLocations.Mounted().All(x => Directory.Exists(x.Path)), "mounted GVfs folders exist");

    // Navigation history regression: inaccessible/cancelled navigation must not
    // prematurely change the current history index.
    var navigation = new NavigationHistory();
    navigation.RecordSuccess("/one");
    navigation.RecordSuccess("/two");
    navigation.RecordSuccess("/three");
    Check(navigation.Peek(-1) == "/two", "back target is correct");
    Check(navigation.Current == "/three", "preview does not mutate history");
    Check(!navigation.CommitTravel(-1, "/missing"), "failed navigation not committed");
    Check(navigation.Current == "/three", "failed navigation keeps current directory");
    Check(navigation.CommitTravel(-1, "/two"), "successful back travel committed");
    Check(navigation.Peek(1) == "/three", "forward history remains available");
    navigation.RecordSuccess("/four");
    Check(navigation.Peek(1) is null, "new navigation truncates forward history");
    navigation.RecordSuccess("/four");
    Check(navigation.Entries.Count == 3, "revisiting current directory does not duplicate history");
    Check(!navigation.CommitTravel(-3, "/one"), "invalid travel is rejected");

    // ---- v16 engine: recursive copy, move, rename, freedesktop trash ----
    var tree = Path.Combine(root, "tree");
    var treeSub = Path.Combine(tree, "alt");
    Directory.CreateDirectory(treeSub);
    await File.WriteAllTextAsync(Path.Combine(tree, "a.txt"), "A");
    await File.WriteAllTextAsync(Path.Combine(treeSub, "b.txt"), "B");
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(Path.Combine(tree, "a.txt"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead | UnixFileMode.GroupRead);
    var treeLink = Path.Combine(tree, "koplanti");
    if (!OperatingSystem.IsWindows()) File.CreateSymbolicLink(treeLink, Path.Combine(tree, "a.txt"));

    var treeTarget = Path.Combine(root, "tree-target");
    Directory.CreateDirectory(treeTarget);
    await SafeFileOperations.CopyAsync(tree, treeTarget);
    var copiedTree = Path.Combine(treeTarget, "tree");
    Check(File.ReadAllText(Path.Combine(copiedTree, "a.txt")) == "A", "recursive copy keeps top-level file");
    Check(File.ReadAllText(Path.Combine(copiedTree, "alt", "b.txt")) == "B", "recursive copy keeps nested file");
    if (!OperatingSystem.IsWindows())
    {
        Check(File.GetAttributes(Path.Combine(copiedTree, "koplanti")).HasFlag(FileAttributes.ReparsePoint),
            "recursive copy recreates symlinks as links");
        Check(new FileInfo(Path.Combine(copiedTree, "koplanti")).LinkTarget == Path.Combine(tree, "a.txt"),
            "copied link keeps original target");
        Check((File.GetUnixFileMode(Path.Combine(copiedTree, "a.txt")) & UnixFileMode.OtherRead) != 0,
            "recursive copy preserves unix mode bits");
    }
    Check(Directory.Exists(Path.Combine(tree, "alt")), "source tree untouched after copy");
    await ThrowsAsync<IOException>(() => SafeFileOperations.CopyAsync(tree, treeTarget), "directory copy does not overwrite");
    await ThrowsAsync<IOException>(() => SafeFileOperations.CopyAsync(tree, Path.Combine(tree, "alt")), "directory cannot copy into itself");

    var moveTarget = Path.Combine(root, "move-target");
    Directory.CreateDirectory(moveTarget);
    await SafeFileOperations.MoveAsync(Path.Combine(tree, "a.txt"), moveTarget);
    Check(File.Exists(Path.Combine(moveTarget, "a.txt")) && !File.Exists(Path.Combine(tree, "a.txt")), "file move relocates source");
    await SafeFileOperations.MoveAsync(treeSub, moveTarget);
    Check(Directory.Exists(Path.Combine(moveTarget, "alt")) && !Directory.Exists(treeSub), "directory move relocates tree");
    await ThrowsAsync<IOException>(() => SafeFileOperations.MoveAsync(Path.Combine(moveTarget, "a.txt"), moveTarget), "move collision refused");
    await ThrowsAsync<IOException>(() => SafeFileOperations.MoveAsync(moveTarget, Path.Combine(moveTarget, "alt")), "directory cannot move into itself");

    await SafeFileOperations.RenameAsync(Path.Combine(moveTarget, "a.txt"), "z.txt");
    Check(File.ReadAllText(Path.Combine(moveTarget, "z.txt")) == "A", "rename keeps content");
    await ThrowsAsync<IOException>(() => SafeFileOperations.RenameAsync(Path.Combine(moveTarget, "z.txt"), "alt"), "rename does not overwrite");
    await ThrowsAsync<ArgumentException>(() => SafeFileOperations.RenameAsync(Path.Combine(moveTarget, "z.txt"), "a/b"), "rename rejects separators");

    var trashRoot = Path.Combine(root, "fake-trash");
    var trashFile = Path.Combine(root, "trashme.txt");
    await File.WriteAllTextAsync(trashFile, "T");
    SafeFileOperations.Trash(trashFile, trashRoot);
    Check(!File.Exists(trashFile), "trashed file leaves original location");
    Check(File.Exists(Path.Combine(trashRoot, "files", "trashme.txt")), "trash places file under files/");
    var infoFile = Path.Combine(trashRoot, "info", "trashme.txt.trashinfo");
    Check(File.Exists(infoFile), "trashinfo sidecar written");
    var infoText = await File.ReadAllTextAsync(infoFile);
    Check(infoText.Contains("[Trash Info]", StringComparison.Ordinal) && infoText.Contains("Path=", StringComparison.Ordinal)
        && infoText.Contains("DeletionDate=", StringComparison.Ordinal), "trashinfo follows freedesktop spec");

    // The default home-trash decision must work on Fedora's glibc. This is
    // intentionally tested without touching the real trash: it verifies the
    // filesystem identity primitive used before the destination is selected.
    if (OperatingSystem.IsLinux())
    {
        var sameDevice = typeof(SafeFileOperations).GetMethod("SameDevice",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var homeProbe = Path.Combine(UserFolders.Home, ".emanager-trash-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(homeProbe);
        try
        {
            Check((bool)sameDevice.Invoke(null, [homeProbe, UserFolders.Home])!,
                "home and isolated source resolve to the same filesystem");
        }
        finally { Directory.Delete(homeProbe); }
    }

    // A second trashed file with the same name must not evict the first one.
    await File.WriteAllTextAsync(trashFile, "T2");
    SafeFileOperations.Trash(trashFile, trashRoot);
    Check(File.ReadAllText(Path.Combine(trashRoot, "files", "trashme.txt")) == "T", "first trashed name preserved");
    Check(File.Exists(Path.Combine(trashRoot, "files", "trashme.txt-1")), "collision gets -1 suffix");

    var trashDir = Path.Combine(root, "trashdir");
    Directory.CreateDirectory(trashDir);
    await File.WriteAllTextAsync(Path.Combine(trashDir, "inside.txt"), "I");
    SafeFileOperations.Trash(trashDir, trashRoot);
    Check(!Directory.Exists(trashDir) && File.Exists(Path.Combine(trashRoot, "files", "trashdir", "inside.txt")),
        "directory trash keeps contents");
    var restoredTrashDir = TrashService.List(trashRoot).Single(x => x.Name == "trashdir");
    TrashService.Restore(restoredTrashDir);
    Check(Directory.Exists(trashDir) && File.ReadAllText(Path.Combine(trashDir, "inside.txt")) == "I",
        "non-empty directory trash restores contents");
    Directory.Delete(trashDir, recursive: true);
    var emptyTrashDir = Path.Combine(root, "empty-trash-dir");
    Directory.CreateDirectory(emptyTrashDir);
    SafeFileOperations.Trash(emptyTrashDir, trashRoot);
    var restoredEmptyDir = TrashService.List(trashRoot).Single(x => x.Name == "empty-trash-dir");
    TrashService.Restore(restoredEmptyDir);
    Check(Directory.Exists(emptyTrashDir) && !Directory.EnumerateFileSystemEntries(emptyTrashDir).Any(),
        "empty directory trash restores as a directory");
    Directory.Delete(emptyTrashDir);
    if (!OperatingSystem.IsWindows())
    {
        var linkToTrash = Path.Combine(root, "link-to-trash");
        File.CreateSymbolicLink(linkToTrash, Path.Combine(root, "nonexistent"));
        SafeFileOperations.Trash(linkToTrash, trashRoot);
        var trashedLink = Path.Combine(trashRoot, "files", "link-to-trash");
        Check(!File.Exists(linkToTrash) && new FileInfo(trashedLink).LinkTarget == Path.Combine(root, "nonexistent"),
            "symlink trash moves the link without deleting it");
        var trashedLinkEntry = TrashService.List(trashRoot).Single(x => x.Name == "link-to-trash");
        TrashService.Restore(trashedLinkEntry);
        Check(new FileInfo(linkToTrash).LinkTarget == Path.Combine(root, "nonexistent"),
            "trashed symlink can be restored");

        // Moving and renaming must relocate the link itself, never its target.
        await File.WriteAllTextAsync(Path.Combine(root, "outside-target"), "OUT");
        var moveLink = Path.Combine(tree, "tasi-link");
        File.CreateSymbolicLink(moveLink, Path.Combine(root, "outside-target"));
        await SafeFileOperations.MoveAsync(moveLink, moveTarget);
        var movedLink = Path.Combine(moveTarget, "tasi-link");
        Check(!File.Exists(moveLink), "moved link removed from source");
        Check(new FileInfo(movedLink).LinkTarget == Path.Combine(root, "outside-target"), "moved link keeps target");
        Check(File.Exists(Path.Combine(root, "outside-target")), "link target survived the move");

        await SafeFileOperations.RenameAsync(movedLink, "yeniden-link");
        Check(new FileInfo(Path.Combine(moveTarget, "yeniden-link")).LinkTarget == Path.Combine(root, "outside-target"),
            "renamed link follows rename(2) not its target");
        var dangling = Path.Combine(moveTarget, "dangling-link");
        File.CreateSymbolicLink(dangling, Path.Combine(root, "no-such-target"));
        await SafeFileOperations.RenameAsync(dangling, "dangling-renamed");
        Check(new DirectoryInfo(Path.Combine(moveTarget, "dangling-renamed")).LinkTarget == Path.Combine(root, "no-such-target"),
            "dangling link rename preserves target");
    }

    // ---- Trash service: isolated freedesktop metadata and lifecycle ----
    var managedTrash = Path.Combine(root, "managed-trash");
    var restoreDir = Path.Combine(root, "restore-destination");
    Directory.CreateDirectory(restoreDir);
    var trashSource = Path.Combine(restoreDir, "özel # dosya.txt");
    await File.WriteAllTextAsync(trashSource, "restore me");
    SafeFileOperations.Trash(trashSource, managedTrash);
    var trashEntries = TrashService.List(managedTrash);
    Check(trashEntries.Count == 1 && trashEntries[0].Name == "özel # dosya.txt", "trash lists metadata entry");
    Check(trashEntries[0].OriginalPath == trashSource && trashEntries[0].Size == 10, "trash metadata keeps original path and size");
    TrashService.Restore(trashEntries[0]);
    Check(File.Exists(trashSource) && await File.ReadAllTextAsync(trashSource) == "restore me", "trash entry restores to original path");

    var conflictSource = Path.Combine(restoreDir, "conflict.txt");
    await File.WriteAllTextAsync(conflictSource, "original");
    SafeFileOperations.Trash(conflictSource, managedTrash);
    await File.WriteAllTextAsync(conflictSource, "new file");
    var conflictEntry = TrashService.List(managedTrash).Single(x => x.Name == "conflict.txt");
    await ThrowsAsync<IOException>(() => Task.Run(() => TrashService.Restore(conflictEntry)), "restore collision is refused");
    Check(await File.ReadAllTextAsync(conflictSource) == "new file", "restore collision does not overwrite target");
    TrashService.PermanentlyDelete(conflictEntry);
    Check(!TrashService.List(managedTrash).Any(x => x.Name == "conflict.txt"), "permanent delete removes selected entry");

    var emptyA = Path.Combine(restoreDir, "empty-a");
    var emptyB = Path.Combine(restoreDir, "empty-b");
    await File.WriteAllTextAsync(emptyA, "a");
    await File.WriteAllTextAsync(emptyB, "b");
    SafeFileOperations.Trash(emptyA, managedTrash);
    SafeFileOperations.Trash(emptyB, managedTrash);
    Check(TrashService.Empty(managedTrash) == 2, "empty trash removes all entries");
    Check(TrashService.List(managedTrash).Count == 0, "empty trash reports empty state");

    var unavailableTrash = Path.Combine(root, "not-a-directory");
    await File.WriteAllTextAsync(unavailableTrash, "not a directory");
    var protectedSource = Path.Combine(restoreDir, "protected.txt");
    await File.WriteAllTextAsync(protectedSource, "must survive");
    await ThrowsAsync<IOException>(() => Task.Run(() => SafeFileOperations.Trash(protectedSource, unavailableTrash)),
        "unavailable trash refuses instead of deleting");
    Check(File.Exists(protectedSource), "unavailable trash preserves source");

    // Internal drag tickets must be one-use and may never carry external file lists.
    Check(!InternalDragRegistry.IsPending("external-fake-ticket"), "external drag token rejected");
    Check(InternalDragRegistry.Consume("external-fake-ticket") is null, "external drag cannot supply paths");
    var dragSource = Path.Combine(root, "drag-source.txt");
    await File.WriteAllTextAsync(dragSource, "fixture");
    var ticket = InternalDragRegistry.Begin([dragSource]);
    Check(InternalDragRegistry.IsPending(ticket), "internal drag token exists");
    Check(InternalDragRegistry.Consume(ticket) is { Count: 1 } paths && paths[0] == dragSource,
        "internal drag receives original registered path");
    Check(!InternalDragRegistry.IsPending(ticket) && InternalDragRegistry.Consume(ticket) is null,
        "replayed internal drag refused");
    var revoked = InternalDragRegistry.Begin([dragSource]);
    InternalDragRegistry.Revoke(revoked);
    Check(InternalDragRegistry.Consume(revoked) is null, "cancelled drag token revoked");
    await ThrowsAsync<ArgumentException>(() => Task.Run(() => InternalDragRegistry.Begin(["relative-file.txt"])),
        "relative drag paths rejected");

    var launcher = Path.Combine(root, "sample.desktop");
    await File.WriteAllTextAsync(launcher, "[Desktop Entry]\nExec=echo fixture\n");
    Check(UntrustedLauncher.IsPotentiallyExecutable(launcher), "desktop launcher requires warning");
    Check(UntrustedLauncher.Describe(launcher).Contains("echo fixture", StringComparison.Ordinal),
        "desktop warning shows launcher command");
    Check(!UntrustedLauncher.IsPotentiallyExecutable(dragSource), "plain text is not an executable");
    Check(TrustedTools.Resolve("tar") is { } tarPath && Path.IsPathFullyQualified(tarPath) &&
        tarPath.StartsWith("/usr/bin/", StringComparison.Ordinal), "tools resolved from system directory");
    Check(TrustedTools.Resolve("../tar") is null && TrustedTools.Resolve("/tmp/tar") is null,
        "tool resolver refuses path injection");

    if (OperatingSystem.IsLinux())
    {
        var linkAlias = Path.Combine(root, "disguised-document.txt");
        File.CreateSymbolicLink(linkAlias, launcher);
        Check(UntrustedLauncher.IsPotentiallyExecutable(linkAlias), "launcher symlink cannot hide executable type");
        Check(UntrustedLauncher.Describe(linkAlias).Contains("echo fixture", StringComparison.Ordinal),
            "launcher symlink warning includes command");
        var noReplaceSource = Path.Combine(root, "race-source-link");
        var noReplaceTarget = Path.Combine(root, "race-target-existing");
        File.CreateSymbolicLink(noReplaceSource, dragSource);
        await File.WriteAllTextAsync(noReplaceTarget, "KEEP");
        // The native RENAME_NOREPLACE path must not evict a competing entry.
        var method = typeof(SafeFileOperations).GetMethod("RenameNoReplaceNative",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var result = (int)method.Invoke(null, [-100, noReplaceSource, -100, noReplaceTarget, 1u])!;
        Check(result == -1 && File.ReadAllText(noReplaceTarget) == "KEEP",
            "Linux renameat2 rejects overwrite atomically");
        Check(new FileInfo(noReplaceSource).LinkTarget == dragSource,
            "failed atomic rename preserves source symlink");
    }
    // Build archives through the same implementation the UI calls.
    if (TrustedTools.Resolve("7z") is not null && TrustedTools.Resolve("tar") is not null)
    {
        var archiveDir = Path.Combine(root, "archive-fixtures");
        Directory.CreateDirectory(archiveDir);
        var archiveInput = Path.Combine(archiveDir, "sample.txt");
        await File.WriteAllTextAsync(archiveInput, "archive bytes");
        var firstZip = await SafeArchiveOperations.CreateAsync([archiveInput], archiveDir, "zip");
        Check(File.Exists(firstZip) && new FileInfo(firstZip).Length > 0, "ZIP archive published after complete build");
        var firstHash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(firstZip));
        var secondZip = await SafeArchiveOperations.CreateAsync([archiveInput], archiveDir, "zip");
        Check(firstZip != secondZip && File.Exists(secondZip), "second archive gets collision-free filename");
        Check(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(firstZip)).SequenceEqual(firstHash),
            "second archive never overwrites first");
        var tarball = await SafeArchiveOperations.CreateAsync([archiveInput], archiveDir, "tar.gz");
        Check(File.Exists(tarball) && new FileInfo(tarball).Length > 0, "tar.gz archive published after complete build");
        Check(!Directory.EnumerateDirectories(archiveDir, ".emanager-archive-*").Any(),
            "archive staging directories cleaned after success");
        await ThrowsAsync<ArgumentException>(() => SafeArchiveOperations.CreateAsync([archiveInput], archiveDir, "invalid"),
            "unknown archive format rejected");
        await ThrowsAsync<ArgumentException>(() => SafeArchiveOperations.CreateAsync([dragSource], archiveDir, "zip"),
            "source outside selected directory rejected");
        Check(!Directory.EnumerateDirectories(archiveDir, ".emanager-archive-*").Any(),
            "invalid archive requests leave no temporary directories");
    }

    var existingPath = Environment.GetEnvironmentVariable("PATH");
    try
    {
        Environment.SetEnvironmentVariable("PATH", ".:/tmp:/usr/bin");
        var trustedStart = new System.Diagnostics.ProcessStartInfo("/usr/bin/echo");
        TrustedTools.SecurePath(trustedStart);
        Check(!trustedStart.Environment["PATH"]!.Split(Path.PathSeparator).Contains(".") &&
            !trustedStart.Environment["PATH"]!.Split(Path.PathSeparator).Contains("/tmp"),
            "child environment excludes relative and shared-writable PATH components");
    }
    finally { Environment.SetEnvironmentVariable("PATH", existingPath); }
    // Open With: real desktop-entry parsing, MIME identification, safe app launching
    // and isolated default app settings (never mutate the user's real associations).
    var xdgRoot = Path.Combine(root, "xdg-openwith-test");
    var xdgApps = Path.Combine(xdgRoot, "applications");
    Directory.CreateDirectory(xdgApps);
    var uniqueMime = "application/x-emanager-openwith-" + Guid.NewGuid().ToString("N");
    var exampleDesktop = Path.Combine(xdgApps, "org.example.SafeViewer.desktop");
    await File.WriteAllTextAsync(exampleDesktop,
        "[Desktop Entry]\nType=Application\nName=Example Viewer\n" +
        "Exec=/usr/bin/true %f\nMimeType=" + uniqueMime + ";text/plain;\nComment=Fixture viewer\n");
    var hiddenDesktop = Path.Combine(xdgApps, "hidden.desktop");
    await File.WriteAllTextAsync(hiddenDesktop, "[Desktop Entry]\nType=Application\nName=Hidden\n" +
        "Hidden=true\nExec=/usr/bin/true %f\n");
    var disabledDesktop = Path.Combine(xdgApps, "not-installed.desktop");
    await File.WriteAllTextAsync(disabledDesktop, "[Desktop Entry]\nType=Application\nName=Unavailable\n" +
        "TryExec=/path/not-installed-app\nExec=/usr/bin/true %f\n");
    var submenuDir = Path.Combine(xdgApps, "subfolder");
    Directory.CreateDirectory(submenuDir);
    await File.WriteAllTextAsync(Path.Combine(submenuDir, "another.desktop"),
        "[Desktop Entry]\nType=Application\nName=Another Viewer\nExec=/usr/bin/true %f\n");
    var discovered = OpenWithService.DiscoverApplications([xdgRoot]);
    Check(discovered.Count == 2, "application picker ignores hidden or missing programs");
    var viewer = discovered.Single(x => x.DesktopId == "org.example.SafeViewer.desktop");
    Check(viewer.Name == "Example Viewer" && viewer.Supports(uniqueMime),
        "desktop application name and MIME association are parsed");
    Check(discovered.Any(x => x.DesktopId == "subfolder-another.desktop"),
        "nested desktop file identifier follows freedesktop specification");
    Check(OpenWithService.ValidMimeType("text/plain") &&
        !OpenWithService.ValidMimeType("text/plain\nother") &&
        !OpenWithService.ValidMimeType("../invalid"), "MIME identifiers are validated");
    Check(!OpenWithService.ValidDesktopId("../evil.desktop") &&
        OpenWithService.ValidDesktopId(viewer.DesktopId), "desktop application IDs reject path traversal");
    var launchInfo = OpenWithService.BuildLaunchInfo(viewer, dragSource);
    Check(launchInfo.FileName == "/usr/bin/gio" &&
        launchInfo.ArgumentList.Count == 3 && launchInfo.ArgumentList[0] == "launch" &&
        launchInfo.ArgumentList[1] == exampleDesktop && launchInfo.ArgumentList[2] == dragSource,
        "application launching uses gio launch and structured arguments");
    Check(await OpenWithService.GetMimeTypeAsync(dragSource) == "text/plain",
        "xdg-mime identifies text file MIME without relying on extension");
    await OpenWithService.LaunchAsync(viewer, dragSource);
    Check(true, "gio launch can open the selected installed test application");

    var installed = await OpenWithService.LoadChoicesAsync(dragSource);
    Check(installed.MimeType == "text/plain" && installed.Applications.Count > 0,
        "actual Fedora applications are discoverable for a real MIME type");
    Check(installed.Applications.Any(app => app.Supports(installed.MimeType) ||
        installed.RecommendedIds.Contains(app.DesktopId) || app.DesktopId == installed.DefaultId),
        "real MIME file type provides suggested applications");

    var originalConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    var originalData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    var originalDataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
    var originalDesktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
    var isolatedConfig = Path.Combine(root, "xdg-openwith-config");
    Directory.CreateDirectory(isolatedConfig);
    try
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", isolatedConfig);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdgRoot);
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", xdgRoot);
        Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "");
        await OpenWithService.SetDefaultAsync(uniqueMime, viewer);
        Check(await OpenWithService.GetDefaultAsync(uniqueMime) == viewer.DesktopId,
            "selected program is persisted as MIME default");
        Check(File.Exists(Path.Combine(isolatedConfig, "mimeapps.list")),
            "MIME association is saved only in isolated test configuration");
        var anyProgram = discovered.Single(x => x.DesktopId == "subfolder-another.desktop");
        await OpenWithService.SetDefaultAsync(uniqueMime, anyProgram);
        Check(await OpenWithService.GetDefaultAsync(uniqueMime) == anyProgram.DesktopId,
            "programs without declared MIME support can also become defaults");
    }
    finally
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", originalConfig);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", originalData);
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", originalDataDirs);
        Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", originalDesktop);
    }

    Console.WriteLine($"PASS: {passed} assertions");
}
finally
{
    Directory.Delete(root, recursive: true);
}

void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
}
async Task ThrowsAsync<T>(Func<Task> operation, string name) where T : Exception
{
    try { await operation(); }
    catch (T) { passed++; return; }
    throw new Exception($"FAIL: {name} (expected {typeof(T).Name})");
}
