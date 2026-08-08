#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-2.0-or-later

set -euo pipefail

script_directory="$(unset CDPATH; cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
readonly script_directory
readonly verifier="$script_directory/source-slices.mjs"
destination="${1:-$script_directory}"

fail() {
  printf 'fetch-rade-sources: %s\n' "$*" >&2
  exit 1
}

case "$destination" in
  ""|"/"|"."|"..") fail "refusing unsafe destination: $destination" ;;
esac
command -v git >/dev/null 2>&1 || fail "git is required to fetch the pinned RADE source slices"
command -v node >/dev/null 2>&1 || fail "Node.js is required to verify the pinned RADE source slices"
[[ -f "$verifier" ]] || fail "source-slice verifier is missing: $verifier"

fetch_config="$(node "$verifier" fetch-config)" \
  || fail "could not read the pinned RADE fetch configuration"
IFS=$'\t' read -r upstream_repository upstream_commit sparse_path <<< "$fetch_config"
[[ -n "$upstream_repository" && -n "$upstream_commit" && -n "$sparse_path" ]] \
  || fail "pinned RADE fetch configuration is incomplete"

work="$(mktemp -d "${TMPDIR:-/tmp}/zeus-rade-source.XXXXXX")" \
  || fail "could not create a temporary checkout"
cleanup() {
  rm -rf -- "$work"
}
trap cleanup EXIT

git -C "$work" init -q || fail "could not initialize the temporary checkout"
git -C "$work" config core.autocrlf false \
  || fail "could not disable line-ending conversion in the temporary checkout"
git -C "$work" config core.eol lf \
  || fail "could not require LF source checkout bytes"
git -C "$work" remote add origin "$upstream_repository" \
  || fail "could not configure the pinned RADE upstream"
git -C "$work" config core.sparseCheckout true \
  || fail "could not enable sparse checkout"
git -C "$work" sparse-checkout set "$sparse_path" \
  || fail "could not configure the RADE sparse checkout"
git -C "$work" fetch -q --depth 1 origin "$upstream_commit" \
  || fail "network fetch of pinned Thetis-RADE commit $upstream_commit failed; refusing an incomplete source export"
git -C "$work" checkout -q FETCH_HEAD \
  || fail "could not check out pinned Thetis-RADE commit $upstream_commit"
actual_commit="$(git -C "$work" rev-parse HEAD)"
[[ "$actual_commit" == "$upstream_commit" ]] \
  || fail "fetched commit $actual_commit does not match pin $upstream_commit"

mkdir -p -- "$destination"
slice_paths="$(node "$verifier" slice-paths)" \
  || fail "could not read the pinned RADE slice list"
while IFS=$'\t' read -r slice upstream_path expected_tree; do
  [[ -n "$slice" ]] || continue
  source_path="$work/$upstream_path"
  [[ -d "$source_path" ]] \
    || fail "pinned source slice is absent upstream: $upstream_path"
  actual_tree="$(git -C "$work" rev-parse "HEAD:$upstream_path")"
  [[ "$actual_tree" == "$expected_tree" ]] \
    || fail "$slice Git tree $actual_tree does not match recorded tree $expected_tree"
  rm -rf -- "${destination:?}/$slice"
  cp -RP "$source_path" "$destination/$slice" \
    || fail "could not materialize $slice into $destination"
done <<< "$slice_paths"

node "$verifier" verify "$destination" \
  || fail "materialized RADE source slices failed pinned integrity verification"
printf 'fetch-rade-sources: materialized and verified Thetis-RADE %s in %s\n' \
  "$upstream_commit" "$destination"
