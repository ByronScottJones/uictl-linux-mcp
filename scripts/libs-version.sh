#!/usr/bin/env bash
# Prints every direct and transitive NuGet library the given project(s) ship
# with, one "name  version" pair per line, aligned in columns, sorted by name.
# Usage: scripts/libs-version.sh <project.csproj>...
# Expects `dotnet restore` to have run already (the Makefile's `deps` target)
# - it reads the restored assets rather than hitting the network.
set -euo pipefail

if [ "$#" -eq 0 ]; then
	echo "usage: libs-version.sh <project.csproj>..." >&2
	exit 2
fi

for project in "$@"; do
	dotnet list "$project" package --include-transitive --no-restore
done |
	# Package rows look like "   > Name   [requested]   resolved" - the
	# resolved version is always the last field, so this works for both the
	# top-level rows (3 columns) and the transitive rows (2 columns).
	awk '$1 == ">" { print $2, $NF }' |
	LC_ALL=C sort -u |
	awk '{ name[NR] = $1; ver[NR] = $2; if (length($1) > w) w = length($1) }
		END { for (i = 1; i <= NR; i++) printf "%-*s  %s\n", w, name[i], ver[i] }'
