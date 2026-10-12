#!/usr/bin/env bash
set -euo pipefail
aha_root="$(cd "$(dirname "$0")/../../.." && pwd)"
aha_dotnet="${AHA_DOTNET:-dotnet}"
if [[ -x "$aha_root/.tools/dotnet/dotnet" && -z "${AHA_DOTNET:-}" ]]; then
  aha_dotnet="$aha_root/.tools/dotnet/dotnet"
  export DOTNET_CLI_HOME="$aha_root/.tools/dotnet-home"
  export NUGET_PACKAGES="$aha_root/.tools/nuget"
fi
aha_yak="${AHA_YAK:-/Applications/Rhino 8.app/Contents/Resources/bin/yak}"
"$aha_dotnet" build "$aha_root/src/aha/rhino/Aha.csproj" -c Release --nologo
aha_output="$aha_root/src/aha/rhino/bin/Release/net8.0"
aha_stage="$(mktemp -d "${TMPDIR:-/tmp}/aha-package.XXXXXX")"
trap 'rm -rf "$aha_stage"' EXIT
cp "$aha_output"/*.rhp "$aha_output"/*.dll "$aha_output"/*.json "$aha_output/Aha.rui" "$aha_output/LICENSE" "$aha_output/THIRD_PARTY_NOTICES.txt" "$aha_stage/"
aha_version="$("$aha_dotnet" msbuild "$aha_root/src/aha/rhino/Aha.csproj" -getProperty:Version -nologo)"
sed "s/@AHA_VERSION@/$aha_version/" "$aha_root/src/aha/package/manifest.yml" > "$aha_stage/manifest.yml"
(cd "$aha_stage" && "$aha_yak" build --platform mac)
mkdir -p "$aha_root/outputs/packages"
cp "$aha_stage"/*.yak "$aha_root/outputs/packages/"
for aha_file in "$aha_stage"/*.yak; do printf '%s\n' "$aha_root/outputs/packages/$(basename "$aha_file")"; done
