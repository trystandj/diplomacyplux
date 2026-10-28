#!/usr/bin/env bash
# Installs BepInEx 5 (if missing) and the VeteranLevels plugin into the game folder.
# Only ADDS files; never overwrites an original game file (it stops if one would be).
# Run from Git Bash:  bash C:/Users/tryst/DINO_Modding/tools/install.sh
set -euo pipefail
GAME="/c/Program Files (x86)/Steam/steamapps/common/Diplomacy is Not an Option"
MOD_ROOT="/c/Users/tryst/DINO_Modding"
ZIP="$MOD_ROOT/downloads/BepInEx_win_x64_5.4.23.5.zip"
ZIP_SHA256="82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4"
PLUGIN="$MOD_ROOT/VeteranLevels/bin/Release/VeteranLevels.dll"
LOG="$MOD_ROOT/Backups/backup-log.txt"

if [ ! -d "$GAME/BepInEx/core" ]; then
  echo "$ZIP_SHA256 *$ZIP" | sha256sum -c --quiet
  tmp=$(mktemp -d)
  unzip -q "$ZIP" -d "$tmp"
  (cd "$tmp" && find . -type f) | while read -r f; do
    if [ -e "$GAME/$f" ]; then echo "Refusing to overwrite existing game file: $f" >&2; exit 1; fi
  done
  cp -r "$tmp/." "$GAME/"
  rm -rf "$tmp"
  printf '%s  installed BepInEx 5.4.23.5 (added winhttp.dll, doorstop_config.ini, .doorstop_version, changelog.txt, BepInEx/)\n' "$(date -u '+%F %TZ')" >> "$LOG"
  echo "BepInEx installed."
else
  echo "BepInEx already installed."
fi

mkdir -p "$GAME/BepInEx/plugins/VeteranLevels"
cp "$PLUGIN" "$GAME/BepInEx/plugins/VeteranLevels/"
echo "VeteranLevels.dll deployed to BepInEx/plugins/VeteranLevels/."
echo "Start the game once; the log is at BepInEx/LogOutput.log and settings at BepInEx/config/$(printf 'trystan.dino.veteranlevels.cfg')."
