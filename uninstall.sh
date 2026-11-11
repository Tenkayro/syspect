#!/usr/bin/env bash
set -Eeuo pipefail
INSTALL_DIR="${SYSPECT_INSTALL_DIR:-${HOME}/.local/bin}"
rm -f "$INSTALL_DIR/syspect"
printf 'Syspect executable removed from %s.\n' "$INSTALL_DIR"

remove_path_entry() {
  local shell_name rc line safe tmp
  shell_name="$(basename "${SHELL:-/bin/bash}")"
  case "$shell_name" in
    fish) rc="${XDG_CONFIG_HOME:-${HOME}/.config}/fish/config.fish" ;;
    zsh)  rc="${ZDOTDIR:-${HOME}}/.zshrc" ;;
    *)    rc="${HOME}/.bashrc" ;;
  esac
  [ -f "$rc" ] || return 0

  safe=${INSTALL_DIR//\'/\'\\\'\'}
  if [ "$shell_name" = "fish" ]; then
    line="fish_add_path '${safe}'"
  else
    line="export PATH='${safe}':\$PATH"
  fi

  grep -Fqx "$line" "$rc" 2>/dev/null || return 0
  tmp="$(mktemp)"
  grep -Fvx -e "$line" -e '# Added by the Syspect installer' "$rc" > "$tmp" || true
  cat "$tmp" > "$rc"
  rm -f "$tmp"
  printf 'Removed the Syspect PATH entry from %s.\n' "$rc"
}
remove_path_entry

DATA_DIR="${SYSPECT_HOME:-${XDG_DATA_HOME:-$HOME/.local/share}/syspect}"
CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/syspect"
if [[ "${1:-}" == "--purge" ]]; then
  rm -rf "$DATA_DIR" "$CONFIG_DIR"
  printf 'Syspect reports, settings, logs, and cached data were also removed.\n'
else
  printf 'Your reports and settings were kept. Use --purge to remove them too.\n'
fi
