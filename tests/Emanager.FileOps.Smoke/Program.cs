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
    if (!OperatingSystem.IsWindows())
    {
        var linkToTrash = Path.Combine(root, "link-to-trash");
        File.CreateSymbolicLink(linkToTrash, Path.Combine(root, "nonexistent"));
        SafeFileOperations.Trash(linkToTrash, trashRoot);
        Check(!File.Exists(linkToTrash) && !Directory.EnumerateFileSystemEntries(Path.Combine(trashRoot, "files")).Contains("link-to-trash"),
            "symlink trash unlinks without moving");

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
