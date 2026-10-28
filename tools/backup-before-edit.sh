#!/usr/bin/env bash
# PreToolUse hook (Edit|Write|NotebookEdit): before Claude modifies a file inside
# the game folder, copy the ORIGINAL to the backup folder. Only the first copy is
# kept, so the pristine version is never replaced by an already-modified one.
GAME_DIR="/c/Program Files (x86)/Steam/steamapps/common/Diplomacy is Not an Option"
BACKUP_DIR="/c/Users/tryst/DINO_Modding/Backups/Original"
LOG_FILE="/c/Users/tryst/DINO_Modding/Backups/backup-log.txt"

path=$(node -e '
  let s = "";
  process.stdin.on("data", d => s += d).on("end", () => {
    const i = JSON.parse(s).tool_input || {};
    process.stdout.write(i.file_path || i.notebook_path || "");
  });') || { echo "Backup hook could not read its input; edit blocked." >&2; exit 2; }
[ -z "$path" ] && exit 0

full=$(cygpath -u "$path")
lower_full=$(printf '%s' "$full" | tr '[:upper:]' '[:lower:]')
lower_game=$(printf '%s' "$GAME_DIR/" | tr '[:upper:]' '[:lower:]')
case "$lower_full" in "$lower_game"*) ;; *) exit 0 ;; esac   # not a game file
[ -f "$full" ] || exit 0                                       # new file: nothing to preserve

rel="${full:${#GAME_DIR}+1}"
dest="$BACKUP_DIR/$rel"
[ -e "$dest" ] && exit 0                                       # original already saved

if mkdir -p "$(dirname "$dest")" && cp -p "$full" "$dest"; then
  printf '%s  backed up  %s\n' "$(date -u '+%F %TZ')" "$rel" >> "$LOG_FILE"
  exit 0
fi
# Never modify an original without a copy: exit 2 blocks the tool call.
echo "Backup of '$rel' failed; edit blocked." >&2
exit 2
