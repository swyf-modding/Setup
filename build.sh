#!/usr/bin/env bash
#
# Builds `swyf-setup`, the standalone command-line tool, as one self-contained binary per platform.
#
#     ./build.sh                    # linux-x64 and win-x64
#     ./build.sh linux-x64          # just one runtime
#     CONFIGURATION=Debug ./build.sh
#
# Self-contained and single-file on purpose: the download is one executable with the runtime and the
# vendored Doorstop and unstripped corlib compiled inside it, so it runs on a machine with nothing
# installed. There is no `Setup\` folder to keep beside it.
#
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
configuration="${CONFIGURATION:-Release}"
project="$repo_root/src/ScamWYF.Setup.Cli/ScamWYF.Setup.Cli.csproj"

if [ "$#" -gt 0 ]; then
    rids=("$@")
else
    rids=(linux-x64 win-x64)
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet was not found on PATH. The .NET SDK is what builds this: https://dotnet.microsoft.com/download" >&2
    exit 1
fi

for rid in "${rids[@]}"; do
    out="$repo_root/dist/$rid"
    rm -rf "$out"
    mkdir -p "$out"

    echo "=== building swyf-setup for $rid ($configuration)"
    dotnet publish "$project" \
        --configuration "$configuration" \
        --runtime "$rid" \
        --self-contained true \
        --output "$out" \
        --nologo \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        -p:DebugType=none

    binary="$out/swyf-setup"
    [ -f "$out/swyf-setup.exe" ] && binary="$out/swyf-setup.exe"
    printf '  %s  (%s bytes)\n\n' "$binary" "$(wc -c <"$binary")"
done

echo "Done. Run it:  ./dist/linux-x64/swyf-setup doctor"
