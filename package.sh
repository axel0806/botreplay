#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
version=${1:?Usage: ./package.sh v1.0.0}
[[ "$version" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Version must be vMAJOR.MINOR.PATCH' >&2; exit 1; }
[[ -f dist/BotReplay.dll && -f dist/BotReplay.deps.json ]] || { echo 'Build the plugin first' >&2; exit 1; }
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
curl -fsSL https://github.com/XBribo/CS2-Bot-Controller/releases/download/v0.9.0/BotController-MM-linux-0.9.0.zip -o "$tmp/native.zip"
printf '%s  %s\n' 707e00f4431b54172e8e2d9b5ad3654c41ce3552ee25f29745a2bc137034caae "$tmp/native.zip" | sha256sum -c -
# This file configures the optional upstream managed provider, which is not bundled.
unzip -q "$tmp/native.zip" -x addons/BotController/config.json -d "$tmp/package"
mkdir -p "$tmp/package/addons/counterstrikesharp/plugins/BotReplay"
cp dist/BotReplay.dll dist/BotReplay.deps.json "$tmp/package/addons/counterstrikesharp/plugins/BotReplay/"
cp LICENSE "$tmp/package/BotReplay-LICENSE.txt"
output="$PWD/dist/BotReplay-$version.zip"
python3 - "$tmp/package" "$output" <<'PY'
import pathlib, sys, zipfile
root = pathlib.Path(sys.argv[1])
with zipfile.ZipFile(sys.argv[2], 'w', zipfile.ZIP_DEFLATED) as archive:
    for path in sorted(root.rglob('*')):
        if path.is_file():
            archive.write(path, path.relative_to(root))
PY
echo "Created $output"
