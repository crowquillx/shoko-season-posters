#!/usr/bin/env bash
# Checks the plugin against other builds of Shoko.Abstractions: the oldest it supports by default, or the versions
# given. For each one the tests must pass when compiled against it, and the plugin, built against the version it
# ships with, must find every Shoko.Abstractions type and member it uses in it.
#
#   scripts/check-compat.sh                    # the floor in Directory.Build.props
#   scripts/check-compat.sh 6.0.0-alpha.150    # other builds
set -euo pipefail
cd "$(dirname "$0")/.."

prop() { sed -n -E "s#.*<$1>([^<]+)</$1>.*#\1#p" Directory.Build.props; }
versions=("$@")
[ ${#versions[@]} -gt 0 ] || versions=("$(prop ShokoAbstractionsFloorVersion)")

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

dlls=()
for version in "${versions[@]}"; do
  echo "::group::Tests compiled against Shoko.Abstractions $version"
  # MSBuild judges a build up to date by timestamps, and an older package's files are older than the last build.
  dotnet build Shoko.SeasonPosters.slnx -c Release --no-incremental "-p:ShokoAbstractionsVersion=$version"
  dotnet test Shoko.SeasonPosters.slnx -c Release --no-build "-p:ShokoAbstractionsVersion=$version"
  echo "::endgroup::"
  curl -fsSL -o "$work/$version.nupkg" \
    "https://api.nuget.org/v3-flatcontainer/shoko.abstractions/$version/shoko.abstractions.$version.nupkg"
  unzip -p "$work/$version.nupkg" 'lib/net10.0/Shoko.Abstractions.dll' > "$work/Shoko.Abstractions-$version.dll"
  dlls+=("$work/Shoko.Abstractions-$version.dll")
done

echo "::group::Build against Shoko.Abstractions $(prop ShokoAbstractionsVersion)"
dotnet build src/Shoko.SeasonPosters.csproj -c Release --no-incremental
echo "::endgroup::"
dotnet run --project tools/AbiCheck -c Release -- src/bin/Release/net10.0/Shoko.SeasonPosters.dll "${dlls[@]}"
