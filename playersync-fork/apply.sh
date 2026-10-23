#!/usr/bin/env bash
# Checks out the pinned upstream PlayerSync revision and applies the export IPC patch.
#
#   playersync-fork/apply.sh [target-dir]
#
# The target directory (default: build/PlayerSync) must not exist yet. Nothing outside it is modified.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
target="${1:-$root/build/PlayerSync}"

# shellcheck disable=SC1091
source <(grep -E '^(repo|commit|version)=' "$here/UPSTREAM")

if [[ -e "$target" ]]; then
  echo "error: $target already exists; remove it or pass another directory" >&2
  exit 1
fi

git init -q "$target"
git -C "$target" remote add origin "$repo"
git -C "$target" fetch -q --depth 1 origin "$commit"
git -C "$target" checkout -q FETCH_HEAD
GIT_LFS_SKIP_SMUDGE=1 git -C "$target" submodule update -q --init --recursive --depth 1

contracts="$target/PlayerSync/Interop/Ipc/Export/Contracts"
mkdir -p "$contracts"
cp "$root"/src/PlayerSyncExport.Contracts/*.cs "$contracts/"

for patch in "$here"/patches/*.patch; do
  git -C "$target" apply --whitespace=nowarn "$patch"
done

echo "Patched PlayerSync $version ($commit) in $target"
echo "Build: DOTNET_ROLL_FORWARD=Major dotnet build \"$target/PlayerSync/PlayerSync.csproj\" -c Release"
