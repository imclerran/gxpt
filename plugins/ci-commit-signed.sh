#!/usr/bin/env bash
#
# Commit the given files to a branch as a single VERIFIED commit, via the GitHub
# API (GraphQL createCommitOnBranch). Commits created through the API with the
# Actions GITHUB_TOKEN are automatically GPG-signed by GitHub, so they show as
# "Verified" - unlike a plain `git push` from the runner. Pushes made with
# GITHUB_TOKEN also do not re-trigger workflows, so this won't loop.
#
# Usage: ci-commit-signed.sh <branch> <message> <file>...
#   - files are repo-relative paths (e.g. docs/plugins/xtra-powers.gxpl)
#   - a no-op (no existing files / nothing to add) exits 0 quietly
#
# Requires: gh (authenticated via GH_TOKEN/GITHUB_TOKEN), jq, base64, git.
set -euo pipefail

branch="$1"; message="$2"; shift 2
repo="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY must be set}"
head_oid="$(git rev-parse HEAD)"

additions='[]'
for f in "$@"; do
  [ -f "$f" ] || continue
  b64="$(base64 -w0 "$f" 2>/dev/null || base64 "$f" | tr -d '\n')"
  additions="$(jq --arg p "$f" --arg c "$b64" '. + [{path:$p, contents:$c}]' <<<"$additions")"
done

if [ "$(jq 'length' <<<"$additions")" -eq 0 ]; then
  echo "ci-commit-signed: nothing to commit"; exit 0
fi

body="$(jq -n \
  --arg repo "$repo" --arg branch "$branch" --arg msg "$message" \
  --arg oid "$head_oid" --argjson adds "$additions" \
  '{
     query: "mutation($input: CreateCommitOnBranchInput!) { createCommitOnBranch(input: $input) { commit { oid url } } }",
     variables: { input: {
       branch: { repositoryNameWithOwner: $repo, branchName: $branch },
       message: { headline: $msg },
       expectedHeadOid: $oid,
       fileChanges: { additions: $adds }
     } }
   }')"

echo "$body" | gh api graphql --input - --jq '.data.createCommitOnBranch.commit | "committed \(.oid) \(.url)"'
