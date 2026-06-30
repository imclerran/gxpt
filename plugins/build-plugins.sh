#!/usr/bin/env bash
#
# Build every plugin under plugins/<slug>/ into a .gxpl archive plus a static
# discovery manifest, written to docs/plugins/. A plugin source folder mirrors
# the .gxpl layout (plugin.json + skills/<slug>/ + agents/<slug>.md), so building
# is mostly: validate, derive the member lists from disk, and zip.
#
# Output (served by GitHub Pages from docs/):
#   docs/plugins/<slug>.gxpl     - the installable archive (stable, version-less name)
#   docs/plugins/index.json      - the listing the website fetches (no GitHub API)
#
# The archive is built deterministically (fixed mtimes, sorted entries) so an
# unchanged plugin produces byte-identical output and never makes a spurious commit.
#
# Requires: bash, jq, zip, sha256sum, base64 (all present on ubuntu-latest CI).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/plugins"
OUT="$ROOT/docs/plugins"
EPOCH=1700000000   # fixed mtime for reproducible archives

for tool in jq zip sha256sum base64; do
  command -v "$tool" >/dev/null || { echo "ERROR: '$tool' is required" >&2; exit 1; }
done

mkdir -p "$OUT"

# Extract a frontmatter field (name/description) from a markdown file's leading --- block.
fm_field() {
  awk -v key="$2" '
    NR==1 && $0 != "---" { exit }            # no frontmatter block
    NR==1 { next }
    $0 == "---" { exit }                      # end of block
    {
      p = key ":"
      if (substr($0, 1, length(p)) == p) {
        v = substr($0, length(p) + 1)
        sub(/^[ \t]+/, "", v); sub(/[ \t]+$/, "", v)
        if (length(v) >= 2) {
          a = substr(v, 1, 1); b = substr(v, length(v), 1)
          if ((a == "\"" && b == "\"") || (a == "\x27" && b == "\x27"))
            v = substr(v, 2, length(v) - 2)
        }
        print v; exit
      }
    }
  ' "$1"
}

index_entries=()

for dir in "$SRC"/*/; do
  [ -f "${dir}plugin.json" ] || continue
  slug="$(basename "$dir")"
  name="$(jq -r '.name'               "${dir}plugin.json")"
  version="$(jq -r '.version // "0.0.0"' "${dir}plugin.json")"
  desc="$(jq -r '.description // ""'    "${dir}plugin.json")"

  members='[]'
  skills=()
  if [ -d "${dir}skills" ]; then
    for s in "${dir}skills"/*/; do
      [ -d "$s" ] || continue
      sslug="$(basename "$s")"
      [ -f "${s}SKILL.md" ] || { echo "ERROR: $slug/skills/$sslug has no SKILL.md" >&2; exit 1; }
      sdesc="$(fm_field "${s}SKILL.md" description)"
      [ -n "$sdesc" ] || { echo "ERROR: $slug/skills/$sslug declares no description" >&2; exit 1; }
      sname="$(fm_field "${s}SKILL.md" name)"; sname="${sname:-$sslug}"
      skills+=("$sslug")
      members="$(jq --arg k skill --arg slug "$sslug" --arg n "$sname" --arg d "$sdesc" \
        '. + [{kind:$k, slug:$slug, name:$n, description:$d}]' <<<"$members")"
    done
  fi

  agents=()
  if [ -d "${dir}agents" ]; then
    for a in "${dir}agents"/*.md; do
      [ -e "$a" ] || continue
      aslug="$(basename "$a" .md)"
      adesc="$(fm_field "$a" description)"
      [ -n "$adesc" ] || { echo "ERROR: $slug/agents/$aslug declares no description" >&2; exit 1; }
      aname="$(fm_field "$a" name)"; aname="${aname:-$aslug}"
      agents+=("$aslug")
      members="$(jq --arg k agent --arg slug "$aslug" --arg n "$aname" --arg d "$adesc" \
        '. + [{kind:$k, slug:$slug, name:$n, description:$d}]' <<<"$members")"
    done
  fi

  if [ "${#skills[@]}" -eq 0 ] && [ "${#agents[@]}" -eq 0 ]; then
    echo "ERROR: $slug contains no skills or agents" >&2; exit 1
  fi

  skills_json="$(printf '%s\n' "${skills[@]:-}" | jq -R . | jq -s 'map(select(length > 0))')"
  agents_json="$(printf '%s\n' "${agents[@]:-}" | jq -R . | jq -s 'map(select(length > 0))')"

  # Stage the archive contents: a built plugin.json (members derived from disk) plus
  # the skills/agents trees and the attribution files (the importer ignores extra root files).
  stage="$(mktemp -d)"
  jq --argjson sk "$skills_json" --argjson ag "$agents_json" \
     '{name: .name, version: (.version // "0.0.0"), description: (.description // ""), enabled: true, skills: $sk, agents: $ag}' \
     "${dir}plugin.json" > "${stage}/plugin.json"
  [ -d "${dir}skills" ] && cp -R "${dir}skills" "${stage}/skills"
  [ -d "${dir}agents" ] && cp -R "${dir}agents" "${stage}/agents"
  [ -f "${dir}README.md" ]            && cp "${dir}README.md" "${stage}/"
  [ -f "${dir}LICENSE.superpowers" ]  && cp "${dir}LICENSE.superpowers" "${stage}/"

  archive="${OUT}/${slug}.gxpl"
  rm -f "$archive"
  find "$stage" -exec touch -d "@${EPOCH}" {} +
  ( cd "$stage" && find . -type f | LC_ALL=C sort | zip -q -X -9 -D "$archive" -@ )
  rm -rf "$stage"

  size="$(stat -c %s "$archive" 2>/dev/null || stat -f %z "$archive")"
  sha="$(sha256sum "$archive" | awk '{print $1}')"

  index_entries+=("$(jq -n \
    --arg name "$name" --arg slug "$slug" --arg version "$version" --arg desc "$desc" \
    --arg dl "${slug}.gxpl" --argjson size "$size" --arg sha "$sha" --argjson members "$members" \
    '{name:$name, slug:$slug, version:$version, description:$desc, download:$dl, size:$size, sha256:$sha, members:$members}')")
  echo "built $archive ($size bytes, sha256 ${sha:0:12}…)"
done

if [ "${#index_entries[@]}" -eq 0 ]; then
  echo "ERROR: no plugins found under $SRC" >&2; exit 1
fi

printf '%s\n' "${index_entries[@]}" | jq -s '{plugins: (. | sort_by(.name))}' > "${OUT}/index.json"
echo "wrote ${OUT}/index.json"
