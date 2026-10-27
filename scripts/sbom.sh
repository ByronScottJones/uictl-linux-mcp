#!/usr/bin/env bash
# Writes a CycloneDX JSON SBOM for a project to <out-dir>/bom.json using the
# dotnet-CycloneDX local tool pinned in dotnet-tools.json.
# Usage: scripts/sbom.sh --project <project.csproj> --out <dir> [--skip-if-missing]
#
# Without --skip-if-missing a missing tool is an error (the standalone `sbom`
# target). With it, a missing tool only warns and exits 0 so publishing isn't
# blocked; a genuine tool failure still fails either way.
set -euo pipefail

project=""
out_dir=""
skip_if_missing=false

while [ "$#" -gt 0 ]; do
	case "$1" in
		--project) project="${2:?--project needs a value}"; shift 2 ;;
		--out) out_dir="${2:?--out needs a value}"; shift 2 ;;
		--skip-if-missing) skip_if_missing=true; shift ;;
		*) echo "sbom.sh: unknown argument: $1" >&2; exit 2 ;;
	esac
done

if [ -z "$out_dir" ]; then
	echo "usage: sbom.sh --project <project.csproj> --out <dir> [--skip-if-missing]" >&2
	exit 2
fi

# The manifest declares the command as "dotnet-CycloneDX" - case-sensitive.
if ! dotnet tool run dotnet-CycloneDX --version >/dev/null 2>&1; then
	if [ "$skip_if_missing" = true ]; then
		echo "sbom.sh: warning: SBOM tool not installed - skipping (run 'make sbom/install')" >&2
		exit 0
	fi
	echo "sbom.sh: SBOM tool not installed - run 'make sbom/install'" >&2
	exit 1
fi

if [ -z "$project" ]; then
	echo "usage: sbom.sh --project <project.csproj> --out <dir> [--skip-if-missing]" >&2
	exit 2
fi

mkdir -p "$out_dir"
dotnet tool run dotnet-CycloneDX "$project" \
	--output "$out_dir" --filename bom.json --output-format Json \
	--disable-package-restore --exclude-test-projects
echo "SBOM: $(cd "$out_dir" && pwd)/bom.json"
