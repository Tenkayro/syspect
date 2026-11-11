#!/usr/bin/env bash
set -Eeuo pipefail
INSTALL_DIR="${SYSPECT_INSTALL_DIR:-${HOME}/.local/bin}"
rm -f "$INSTALL_DIR/syspect"
printf 'Syspect executable removed from %s.\n' "$INSTALL_DIR"
DATA_DIR="${SYSPECT_HOME:-${XDG_DATA_HOME:-$HOME/.local/share}/syspect}"
CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/syspect"
if [[ "${1:-}" == "--purge" ]]; then
  rm -rf "$DATA_DIR" "$CONFIG_DIR"
  printf 'Syspect reports, settings, logs, and cached data were also removed.\n'
else
  printf 'Your reports and settings were kept. Use --purge to remove them too.\n'
fi
