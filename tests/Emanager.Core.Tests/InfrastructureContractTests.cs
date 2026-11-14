using Emanager.Core.FileOperations;
using Emanager.Core.Hashing;
using Emanager.Core.Search;
using Emanager.Core.Shortcuts;
using Emanager.Core.Synchronization;
using Emanager.Core.Plugins;
using Emanager.Core.Tasks;
using Xunit;

namespace Emanager.Core.Tests;

public sealed class InfrastructureContractTests
{
    [Fact]
    public async Task Hash_service_streams_sha256_and_reports_bytes()
    {
        var service = new FileHashService();
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello"));
        var progress = new List<long>();
        var result = await service.ComputeAsync(stream, HashAlgorithmKind.Sha256,
            new Progress<long>(progress.Add));
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", result.Hex);
        Assert.Equal(5, result.BytesRead);
        Assert.Contains(5, progress);
    }

    [Fact]
    public void Conflict_resolver_does_not_mutate_existing_destination()
    {
        var existing = new FileConflict("/source/a", "/target/a", 3, 4, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var decision = new FileConflictResolver().Resolve(existing, ConflictAction.Skip);
        Assert.Equal(ConflictAction.Skip, decision.Action);
        Assert.False(decision.MayOverwrite);
    }

    [Fact]
    public async Task Search_stream_can_be_cancelled_and_returns_matching_names()
    {
        var root = Directory.CreateTempSubdirectory("emanager-search-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "report.txt"), "needle");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "other.bin"), "no");
            var results = new List<SearchResult>();
            await new FileSearchEngine().SearchAsync(root.FullName,
                new SearchQuery { NameContains = "report", IncludeSubdirectories = true },
                results.Add);
            Assert.Single(results);
            Assert.Equal("report.txt", results[0].Name);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task Search_respects_mime_filter_and_hidden_directories()
    {
        var root = Directory.CreateTempSubdirectory("emanager-search-hidden-");
        try
        {
            var hidden = Directory.CreateDirectory(Path.Combine(root.FullName, ".private"));
            await File.WriteAllTextAsync(Path.Combine(hidden.FullName, "secret.txt"), "secret");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "visible.bin"), "bin");
            var results = new List<SearchResult>();
            await new FileSearchEngine().SearchAsync(root.FullName, new SearchQuery { IncludeSubdirectories = true, MimeType = "text/plain" }, results.Add);
            Assert.Empty(results);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void Shortcut_manager_detects_conflicts_and_roundtrips_json()
    {
        var manager = new ShortcutManager();
        Assert.True(manager.Register("copy", "Ctrl+C").Accepted);
        Assert.False(manager.Register("custom", "Ctrl+C").Accepted);
        var restored = ShortcutManager.FromJson(manager.ToJson());
        Assert.Equal("Ctrl+C", restored.Get("copy")!.Gesture);
    }

    [Fact]
    public void Sync_planner_requires_confirmation_for_destructive_actions()
    {
        var left = Path.Combine(Path.GetTempPath(), "left-file");
        var right = Path.Combine(Path.GetTempPath(), "right-file");
        var plan = new FolderSyncPlanner().Plan(new[] { new ComparisonEntry(left, right, ComparisonKind.OnlyInLeft, null, null) }, SyncDirection.LeftToRight);
        Assert.Contains(plan.Actions, action => action.Kind == SyncActionKind.Copy);
        Assert.DoesNotContain(plan.Actions, action => action.RequiresConfirmation == false && action.Kind is SyncActionKind.Delete or SyncActionKind.Overwrite);
    }

    [Fact]
    public void Plugin_registry_rejects_untrusted_plugins_and_isolates_failures()
    {
        var registry = new PluginRegistry(new Version(1, 0));
        var result = registry.Register(new TestPlugin("external", false, true));
        Assert.False(result.Accepted);
        Assert.Empty(registry.Plugins);
        var builtIn = registry.Register(new TestPlugin("built-in", true, false));
        Assert.True(builtIn.Accepted);
        Assert.Single(registry.Plugins);
    }

    [Fact]
    public async Task Background_tasks_run_high_priority_before_queued_low_priority_work()
    {
        await using var manager = new BackgroundTaskManager(1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var first = manager.EnqueueAsync("first", async _ => { order.Add("first"); await gate.Task; return 1; }, TaskPriority.Normal);
        var low = manager.EnqueueAsync("low", _ => { order.Add("low"); return Task.FromResult(1); }, TaskPriority.Low);
        var high = manager.EnqueueAsync("high", _ => { order.Add("high"); return Task.FromResult(1); }, TaskPriority.High);
        gate.SetResult(); await Task.WhenAll(first, low, high);
        Assert.Equal(new[] { "first", "high", "low" }, order);
    }

    private sealed class TestPlugin(string id, bool trusted, bool fail) : IFileManagerPlugin
    {
        public string Id => id;
        public Version ApiVersion => new(1, 0);
        public bool IsTrustedBuiltIn => trusted;
        public void Initialize(IPluginHost host) { if (fail) throw new InvalidOperationException(); }
    }
}
