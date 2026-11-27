#!/usr/bin/env bash
set -euo pipefail
if [[ $# != 2 || ! $1 =~ ^[1-9][0-9]*$ || ! $2 =~ ^[a-zA-Z0-9_-]{1,32}$ ]]; then
  echo "Usage: bash tools/claim.sh ISSUE short-handle" >&2
  exit 2
fi
issue=$1
handle=$2
repo=ameenalqershi/wow-decision-intelligence
root=$(git rev-parse --show-toplevel)
cd "$root"
if [[ $(gh repo view --json nameWithOwner --jq .nameWithOwner) != "$repo" ]]; then
  echo "Run from the WOW DI origin checkout." >&2
  exit 1
fi
case "$(git remote get-url --push origin)" in
  "https://github.com/$repo"| "https://github.com/$repo.git"| "git@github.com:$repo.git") ;;
  *) echo "Origin must point to $repo." >&2; exit 1 ;;
esac
git fetch -q origin main
metadata=$(gh api "repos/$repo/issues/$issue")
jq -e '.state == "open" and (.pull_request == null) and (.assignees | length == 0) and all(.labels[]; .name != "status:active")' <<<"$metadata" >/dev/null || {
  echo "The issue is closed, already claimed, or is a pull request." >&2; exit 1;
}
# The origin snapshot prevents stale local dependencies from authorizing a claim.
key=$(git show origin/main:planning/issue-map.json | jq -er --argjson number "$issue" 'to_entries[] | select(.value == $number) | .key')
map=$(git show origin/main:planning/issue-map.json)
for dep in $(git show origin/main:planning/backlog.json | jq -r --arg key "$key" '.[] | select(.id == $key) | .deps[]'); do
  number=$(jq -er --arg dep "$dep" '.[$dep]' <<<"$map")
  if [[ $(gh api "repos/$repo/issues/$number" --jq .state) != closed ]]; then
    echo "Blocked by #$number ($dep)." >&2; exit 1
  fi
done
actor=$(gh api user --jq .login)
tag="work-claims/$issue"
branch="work/$issue-$handle"
worktree="$(dirname "$root")/wowdi-$issue-$handle"
if [[ -e $worktree ]] || git show-ref --quiet --verify "refs/heads/$branch"; then
  echo "Local branch or worktree already exists. Inspect the existing work first." >&2; exit 1
fi
# Annotated tag objects carry a nonce even when two sessions share an account.
nonce=$(git hash-object --stdin <<<"$handle-$$-$RANDOM-$(date +%s)")
git tag -a "$tag" origin/main -m "Issue #$issue reserved by $actor / $handle ($nonce)"
if ! git push origin "refs/tags/$tag:refs/tags/$tag"; then
  echo "Reservation push failed. Inspect the remote tag before recovery. No worktree was created." >&2
  exit 1
fi
echo "Reservation created for #$issue. If interrupted, use CONTRIBUTING.md claim recovery."
gh issue edit "$issue" -R "$repo" --add-assignee "$actor" --add-label status:active --remove-label status:backlog
git worktree add -b "$branch" "$worktree" origin/main
echo "Worktree: $worktree ($branch)"
