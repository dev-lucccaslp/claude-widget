#!/bin/bash
# Compila o Claude Widget para macOS (Apple Silicon e Intel) e monta dist/ClaudeWidget-mac.zip
# com o "Claude Widget.app" dentro. Uso: bash mac/build.sh 1.5.0
# Precisa do Xcode ou das Command Line Tools (xcode-select --install).
set -euo pipefail

versao="${1:-0.0.0}"
versao="${versao#v}"
[[ "$versao" =~ ^[0-9]+(\.[0-9]+)*$ ]] || versao="0.0.0"
mac="$(cd "$(dirname "$0")" && pwd)"
dist="$mac/../dist"
app="$dist/Claude Widget.app"
temp="$(mktemp -d)"
trap 'rm -rf "$temp"' EXIT

rm -rf "$app" "$dist/ClaudeWidget-mac.zip"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

# um binário para cada tipo de Mac, juntos num só (universal)
for arq in arm64 x86_64; do
  xcrun swiftc -O -swift-version 5 -target "$arq-apple-macos11.0" "$mac/Sources/main.swift" -o "$temp/ClaudeWidget-$arq"
done
lipo -create "$temp/ClaudeWidget-arm64" "$temp/ClaudeWidget-x86_64" -output "$app/Contents/MacOS/ClaudeWidget"

sed "s/__VERSAO__/$versao/g" "$mac/Info.plist" > "$app/Contents/Info.plist"

xcrun swiftc -O "$mac/icone.swift" -o "$temp/icone"
"$temp/icone" "$temp/AppIcon.iconset"
iconutil -c icns "$temp/AppIcon.iconset" -o "$app/Contents/Resources/AppIcon.icns"

# Assinatura ad hoc (sem conta de desenvolvedor da Apple): o Mac com chip M não abre app sem nenhuma assinatura
codesign --force --deep --sign - "$app"

(cd "$dist" && ditto -c -k --keepParent "Claude Widget.app" ClaudeWidget-mac.zip)
echo "Gerado: $dist/ClaudeWidget-mac.zip ($versao)"
