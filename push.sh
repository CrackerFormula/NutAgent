#!/usr/bin/env bash
# Push main to GitHub and the forgejo branch (Forgejo-tailored README) to Forgejo.
# Run from the repo root: bash push.sh
set -e

CURRENT=$(git rev-parse --abbrev-ref HEAD)

# Rebase forgejo on main so it picks up any new commits
git checkout forgejo
git rebase main
git checkout "$CURRENT"

git push github main
git push origin forgejo:main
