#!/usr/bin/env bash
set -euo pipefail

tag="${1:?Release tag argument required (e.g. v4.0.0-rc.7)}"

version="${tag#v}"
major="${version%%.*}"

module_tag="packages/go/v${major}/${tag}"
legacy_tag="packages/go/${tag}"

repo="${GITHUB_REPOSITORY:-xberg-io/xberg}"
sha=$(git rev-parse "$tag^{commit}")

create_tag() {
  local t="$1"

  if git rev-parse "$t" >/dev/null 2>&1; then
    echo "::notice::Go module tag $t already exists locally; skipping."
    return
  fi

  if git ls-remote --tags origin | grep -q "refs/tags/${t}$"; then
    echo "::notice::Go module tag $t already exists on remote; skipping."
    return
  fi

  git tag -a "$t" "$tag" -m "Go module tag ${t}"

  if ! git push origin "refs/tags/${t}" 2>/dev/null; then
    echo "::warning::git push failed for tag $t, trying GitHub API..."
    gh api "repos/${repo}/git/refs" \
      -f "ref=refs/tags/${t}" \
      -f "sha=${sha}" \
      --silent
  fi

  echo "✅ Go module tag created: $t (sha: ${sha:0:12})"
}

create_tag "$module_tag"
create_tag "$legacy_tag"
