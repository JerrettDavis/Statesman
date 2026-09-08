#!/usr/bin/env bash
# Fails the release when the pushed tag does not name the version that
# Nerdbank.GitVersioning will stamp on the packages. The package version comes
# from version.json, not from the tag, so a tag that disagrees with version.json
# would publish packages under a different number than the tag and GitHub
# release claim.
#
# Usage: eng/check-release-tag.sh <ref-type> <ref-name>
#   ref-type: "tag" or "branch" (GitHub's github.ref_type)
#   ref-name: e.g. "v0.3.0"
set -euo pipefail

ref_type="${1:?usage: check-release-tag.sh <ref-type> <ref-name>}"
ref_name="${2:?usage: check-release-tag.sh <ref-type> <ref-name>}"

if [ "$ref_type" != "tag" ]; then
  echo "::error::Releases must run from a v*.*.* tag; got $ref_type '$ref_name'. Push a tag instead of dispatching on a branch."
  exit 1
fi

case "$ref_name" in
  v[0-9]*.[0-9]*.[0-9]*) ;;
  *)
    echo "::error::Release tag '$ref_name' is not of the form vMAJOR.MINOR.PATCH[-prerelease]."
    exit 1
    ;;
esac

if ! command -v nbgv >/dev/null 2>&1; then
  dotnet tool install --global nbgv >/dev/null
fi

expected="${ref_name#v}"
actual="$(nbgv get-version --public-release=true --variable NuGetPackageVersion)"

if [ "$expected" != "$actual" ]; then
  echo "::error::Tag '$ref_name' names version $expected, but version.json yields package version $actual. Set \"version\" in version.json to \"$expected\" and commit before tagging, or tag v$actual."
  exit 1
fi

echo "Release tag $ref_name matches package version $actual."
