#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

ui_text="src/Emanager.App/UiText.cs"
if [[ ! -f "$ui_text" ]]; then
  echo "FAIL: $ui_text not found" >&2
  exit 1
fi

duplicates=$(rg -o '^\s*\["[^"]*"\]' "$ui_text" |
  sed 's/.*\[//; s/\]$//' | sort | uniq -d)
if [[ -n "$duplicates" ]]; then
  echo "FAIL: duplicate localization keys:" >&2
  echo "$duplicates" >&2
  exit 1
fi

missing=$(perl -e '
use strict;
use warnings;

my %keys;
open my $u, "<", $ARGV[0] or die "$ARGV[0]: $!";
while (<$u>) {
    $keys{$1} = 1 if /\["((?:\\.|[^"\\])*)"\]\s*=/;
}

for my $file (glob("src/Emanager.App/*.cs")) {
    next if $file =~ /UiText\.cs$/;
    open my $source, "<", $file or die "$file: $!";
    while (<$source>) {
        while (/UiText\.(?:T|F)\("((?:\\.|[^"\\])*)"/g) {
            print "$file:$.:$1\n" unless $keys{$1};
        }
    }
}
' "$ui_text" | sort -u)

if [[ -n "$missing" ]]; then
  echo "FAIL: UI localization calls without a dictionary key:" >&2
  echo "$missing" >&2
  exit 1
fi

echo "PASS: localization dictionary has unique keys and all literal UiText calls are covered"
