#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
cat Emanager-Linux-x64V0.7.tar.gz.part{01..15} > Emanager-Linux-x64V0.7.tar.gz
printf '%s  %s\n' 'a491f63b46ee606687da71b13b94ffae87e9a688bd636f52f4e44f6f755dcca5' 'Emanager-Linux-x64V0.7.tar.gz' | sha256sum -c -
echo 'Emanager Linux V0.7 archive assembled and checksum verified.'
