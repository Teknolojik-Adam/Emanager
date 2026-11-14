#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

core='src/Emanager.Core/Storage/DiskSafetyPolicy.cs'
contracts='src/Emanager.Core/Storage/DiskSafetyContracts.cs'
discovery='src/Emanager.App/Infrastructure/LinuxDiskStorage.cs'
window='src/Emanager.App/MainWindow.cs'

grep -q 'SystemDiskProtectionService' "$core"
grep -q 'ExpectedSnapshotFingerprint' "$core"
grep -q 'ProtectedPhysicalAncestor' "$core"
grep -q 'TryReadMountInfoAsync' "$discovery"
grep -q 'ReadSwapsAsync' "$discovery"
grep -q 'PolicyEnforcedDiskMountService' "$window"
grep -q 'BuildStorageContextMenu' "$window"
grep -q 'ContextRequested' "$window"
grep -q 'StorageOperationTarget' "$window"
grep -q 'Bu diski çıkar' "$window"
grep -q 'AddStorageTree' "$window"
grep -q 'storageNodes.Where(x => x.IsPhysicalDisk)' "$window"
grep -q 'CreateStorageSidebarButton(node, depth)' "$window"
storage_menu="$(awk '/private ContextMenu BuildStorageContextMenu/{inside=1} /private async Task ShowStorageInfoAsync/{inside=0} inside' "$window")"
if grep -q 'ShowDiskUse' <<<"$storage_menu"; then
  echo 'FAIL: storage context menu still routes to disk-usage analysis.' >&2
  exit 1
fi
if grep -q 'Gücü kapat' <<<"$storage_menu"; then
  echo 'FAIL: storage context menu exposes direct power-off instead of safe eject.' >&2
  exit 1
fi
if grep -q 'StorageDevices\.MountAsync' "$window"; then
  echo 'FAIL: MainWindow still uses the legacy path-only mount helper.' >&2
  exit 1
fi

echo 'PASS: disk safety policy, topology sources, UI policy service and bypass audit'
