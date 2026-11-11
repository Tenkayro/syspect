#!/usr/bin/env bash
set -Eeuo pipefail

REPO="Tenkayro/syspect"
INSTALL_DIR="${SYSPECT_INSTALL_DIR:-${HOME}/.local/bin}"
ASSET=""

fail() { printf 'Error: %s\n' "$1" >&2; exit 1; }
command -v curl >/dev/null 2>&1 || command -v wget >/dev/null 2>&1 || fail 'curl or wget is required.'
command -v tar >/dev/null 2>&1 || fail 'tar is required.'

case "$(uname -s)" in Linux) ;; *) fail 'Syspect currently supports Linux only.' ;; esac
case "$(uname -m)" in
  x86_64|amd64) ASSET="syspect-linux-amd64.tar.gz" ;;
  aarch64|arm64) ASSET="syspect-linux-arm64.tar.gz" ;;
  *) fail "Unsupported architecture: $(uname -m)" ;;
esac

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT
URL="https://github.com/${REPO}/releases/latest/download/${ASSET}"
printf 'Installing Syspect for %s...\n' "$(uname -m)"
if command -v curl >/dev/null 2>&1; then
  curl -fL --retry 2 --connect-timeout 15 "$URL" -o "$TMP_DIR/syspect.tar.gz" || fail 'Could not download the latest Syspect release. Check whether a release is available.'
else
  wget -q "$URL" -O "$TMP_DIR/syspect.tar.gz" || fail 'Could not download the latest Syspect release. Check whether a release is available.'
fi

tar -tzf "$TMP_DIR/syspect.tar.gz" >/dev/null || fail 'The downloaded release archive is invalid.'
mkdir -p "$INSTALL_DIR"
tar -xzf "$TMP_DIR/syspect.tar.gz" -C "$TMP_DIR" syspect
[ -f "$TMP_DIR/syspect" ] || fail 'The release archive does not contain the Syspect executable.'
install -m 0755 "$TMP_DIR/syspect" "$INSTALL_DIR/syspect"
printf '\nSyspect was installed to %s/syspect\n' "$INSTALL_DIR"
case ":${PATH}:" in *":${INSTALL_DIR}:"*) ;; *) printf 'Add this directory to your PATH: %s\n' "$INSTALL_DIR" ;; esac
printf 'Start Syspect with: syspect\n'
