using Emanager.Core.Storage;
using Xunit;

namespace Emanager.Core.Tests;

public sealed class DiskSafetyTests
{
    [Fact]
    public void Root_device_is_system_critical_and_cannot_be_unmounted()
    {
        var snapshot = Snapshot() with { Swaps = [new SwapEntry("/dev/nvme0n1p4", "data", true)] };
        var result = new SystemDiskProtectionService().Evaluate(snapshot, "root");

        Assert.Equal(DiskProtectionClass.SystemCritical, result.ProtectionClass);
        Assert.False(result.Capabilities.CanUnmount);
        Assert.False(new DiskOperationPolicy().Authorize(snapshot,
            new DiskOperationRequest(DiskOperationKind.Unmount, "root", "/dev/nvme0n1p3", "test")).Allowed);
    }

    [Fact]
    public void Physical_parent_of_root_is_protected_from_poweroff()
    {
        var snapshot = Snapshot();
        var result = new DiskOperationPolicy().Authorize(snapshot,
            new DiskOperationRequest(DiskOperationKind.PowerOff, "disk", "/dev/nvme0n1", "test"));

        Assert.False(result.Allowed);
        Assert.Equal(DiskDenyReason.ProtectedPhysicalAncestor, result.DenyReason);
        Assert.Equal(DiskProtectionClass.SystemCritical, result.Protection!.ProtectionClass);
    }

    [Fact]
    public void Independent_data_partition_can_be_mounted_but_not_ejected_as_internal_disk()
    {
        var snapshot = Snapshot(dataMounted: false);
        var protection = new SystemDiskProtectionService().Evaluate(snapshot, "data");
        var policy = new DiskOperationPolicy();

        Assert.Equal(DiskProtectionClass.Protected, protection.ProtectionClass);
        Assert.True(policy.Authorize(snapshot,
            new DiskOperationRequest(DiskOperationKind.Mount, "data", "/dev/nvme0n1p4", "test")).Allowed);
        Assert.False(policy.Authorize(snapshot,
            new DiskOperationRequest(DiskOperationKind.Eject, "data", "/dev/nvme0n1p4", "test")).Allowed);
    }

    [Fact]
    public void Active_swap_and_incomplete_topology_fail_closed()
    {
        var snapshot = Snapshot() with { Swaps = [new SwapEntry("/dev/nvme0n1p4", "data", true)] };
        var swapResult = new DiskOperationPolicy().Authorize(snapshot,
            new DiskOperationRequest(DiskOperationKind.Unmount, "data", "/dev/nvme0n1p4", "test"));
        Assert.False(swapResult.Allowed);

        var incomplete = snapshot with { IsComplete = false, Error = "test failure" };
        var unknownResult = new DiskOperationPolicy().Authorize(incomplete,
            new DiskOperationRequest(DiskOperationKind.Unmount, "data", "/dev/nvme0n1p4", incomplete.Fingerprint));
        Assert.False(unknownResult.Allowed);
        Assert.Equal(DiskDenyReason.DiscoveryUnavailable, unknownResult.DenyReason);
    }

    [Fact]
    public void Stale_snapshot_or_changed_device_path_is_rejected()
    {
        var policy = new DiskOperationPolicy();
        var snapshot = Snapshot(dataMounted: false);

        var stale = policy.Authorize(snapshot, new DiskOperationRequest(
            DiskOperationKind.Mount, "data", "/dev/nvme0n1p4", "old-fingerprint"));
        Assert.False(stale.Allowed);
        Assert.Equal(DiskDenyReason.TopologyChanged, stale.DenyReason);

        var changedPath = policy.Authorize(snapshot, new DiskOperationRequest(
            DiskOperationKind.Mount, "data", "/dev/changed", snapshot.Fingerprint));
        Assert.False(changedPath.Allowed);
        Assert.Equal(DiskDenyReason.IdentityChanged, changedPath.DenyReason);
    }

    [Fact]
    public void Removable_disk_cannot_be_ejected_while_a_child_partition_is_mounted()
    {
        var mounted = RemovableSnapshot(dataMounted: true);
        var mountedResult = new SystemDiskProtectionService().Evaluate(mounted, "usb-disk");
        Assert.Equal(DiskProtectionClass.Removable, mountedResult.ProtectionClass);
        Assert.False(mountedResult.Capabilities.CanEject);

        var unmounted = RemovableSnapshot(dataMounted: false);
        var unmountedResult = new SystemDiskProtectionService().Evaluate(unmounted, "usb-disk");
        Assert.True(unmountedResult.Capabilities.CanEject);
        Assert.True(unmountedResult.Capabilities.CanPowerOff);
    }

    [Fact]
    public async Task Policy_service_does_not_call_backend_for_system_device()
    {
        var discovery = new FixedDiscovery(Snapshot());
        var backend = new RecordingBackend();
        var service = new PolicyEnforcedDiskMountService(discovery, new DiskOperationPolicy(), backend);

        var result = await service.ExecuteAsync(new DiskOperationRequest(
            DiskOperationKind.Unmount, "root", "/dev/nvme0n1p3", "test"));

        Assert.False(result.Succeeded);
        Assert.Equal(DiskDenyReason.ProtectedPhysicalAncestor, result.DenyReason);
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task Policy_service_revalidates_then_verifies_a_safe_mount()
    {
        var before = Snapshot(dataMounted: false);
        var after = Snapshot(dataMounted: true);
        var discovery = new SequenceDiscovery(before, before, after);
        var backend = new RecordingBackend();
        var service = new PolicyEnforcedDiskMountService(discovery, new DiskOperationPolicy(), backend);

        var result = await service.ExecuteAsync(new DiskOperationRequest(
            DiskOperationKind.Mount, "data", "/dev/nvme0n1p4", "test"));

        Assert.True(result.Succeeded);
        Assert.Equal(1, backend.Calls);
        Assert.Equal(3, discovery.Calls);
    }

    private static StorageDiscoverySnapshot Snapshot(bool dataMounted = true)
    {
        var disk = new StorageNode("disk", StorageNodeKind.PhysicalDisk, "nvme0n1", "/dev/nvme0n1",
            500_000_000_000, false, false, null, null, "system", "nvme", [], ["root", "data"], []);
        var root = new StorageNode("root", StorageNodeKind.Partition, "nvme0n1p3", "/dev/nvme0n1p3",
            400_000_000_000, false, false, "btrfs", "root-uuid", null, "nvme", ["disk"], [], ["/"]);
        var dataMounts = dataMounted ? new[] { "/mnt/data" } : Array.Empty<string>();
        var data = new StorageNode("data", StorageNodeKind.Partition, "nvme0n1p4", "/dev/nvme0n1p4",
            100_000_000_000, false, false, "ext4", "data-uuid", "Data", "nvme", ["disk"], [], dataMounts);
        var mounts = new List<StorageMount>
        {
            new("root", "/", "/dev/nvme0n1p3", "btrfs", false, [])
        };
        if (dataMounted) mounts.Add(new("data", "/mnt/data", "/dev/nvme0n1p4", "ext4", false, []));
        return new StorageDiscoverySnapshot([disk, root, data], mounts, [], true, "test", DateTimeOffset.UtcNow);
    }

    private static StorageDiscoverySnapshot RemovableSnapshot(bool dataMounted)
    {
        var disk = new StorageNode("usb-disk", StorageNodeKind.PhysicalDisk, "sdb", "/dev/sdb",
            64_000_000_000, true, false, null, null, "USB", "usb", [], ["usb-data"], [], true, true);
        var mounts = dataMounted ? new[] { "/run/media/user/USB" } : Array.Empty<string>();
        var data = new StorageNode("usb-data", StorageNodeKind.Partition, "sdb1", "/dev/sdb1",
            64_000_000_000, true, false, "exfat", "usb-uuid", "USB", "usb", ["usb-disk"], [], mounts);
        var storageMounts = dataMounted
            ? new[] { new StorageMount("usb-data", "/run/media/user/USB", "/dev/sdb1", "exfat", false, []) }
            : Array.Empty<StorageMount>();
        return new StorageDiscoverySnapshot([disk, data], storageMounts, [], true, "usb-test", DateTimeOffset.UtcNow);
    }

    private sealed class FixedDiscovery(StorageDiscoverySnapshot snapshot) : IBlockDeviceDiscovery
    {
        public Task<StorageDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }

    private sealed class SequenceDiscovery(params StorageDiscoverySnapshot[] snapshots) : IBlockDeviceDiscovery
    {
        private int index;
        public int Calls => index;
        public Task<StorageDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken = default)
        {
            var position = Math.Min(Interlocked.Increment(ref index) - 1, snapshots.Length - 1);
            return Task.FromResult(snapshots[position]);
        }
    }

    private sealed class RecordingBackend : IDiskOperationBackend
    {
        public int Calls { get; private set; }
        public Task<DiskOperationBackendResult> ExecuteAsync(StorageNode node, DiskOperationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new DiskOperationBackendResult(true));
        }
    }
}
