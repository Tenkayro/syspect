# Syspect

**A clear, interactive Linux system diagnostics tool.**

Syspect helps you understand what is running on your Linux machine. It collects hardware, operating system, storage, network, temperature, service, kernel, and virtualization information in one terminal interface.

## What it does

- Interactive terminal dashboard built with Bubble Tea.
- Quick, full, hardware, and server diagnostic profiles.
- CPU, memory, graphics, motherboard, disks, network, sensors, and more.
- Optional checks for SMART health, connectivity, and system logs.
- Optional detection and installation of useful diagnostic tools.
- Local HTML and JSON reports, with a privacy mode.
- No account, telemetry, or cloud service required.

## Install

```bash
curl -fsSL https://raw.githubusercontent.com/Tenkayro/syspect/main/install.sh | bash
```

Then start it:

```bash
syspect
```

Linux on **x86_64** and **ARM64** is supported. The installer places the program in `~/.local/bin` and adds that directory to your shell configuration (`~/.bashrc`, `~/.zshrc`, or the fish config). Open a new terminal afterwards, or run `source` on the file it prints. Set `SYSPECT_NO_PATH_MODIFY=1` if you do not want the installer to touch your shell configuration.

## Reports and settings

Reports are saved locally in `~/.local/share/syspect/reports/`. Settings are stored in `~/.config/syspect/`. You can enable privacy mode and choose which optional checks to run inside Syspect.

## Uninstall

Remove the application and keep your reports and settings:

```bash
curl -fsSL https://raw.githubusercontent.com/Tenkayro/syspect/main/uninstall.sh | bash
```

To remove reports and settings as well, run `bash uninstall.sh --purge` after downloading the script.

## Development

Requires Go 1.23+ and Node.js 20.12+.

```bash
go test ./...
go build ./cmd/syspect

npm install
npm run release
```

`npm run release` builds the Linux binaries for amd64 and arm64 on your machine,
writes `dist/syspect-linux-*.tar.gz`, and publishes them through `gh release create`.
The GitHub CLI must be authenticated with `gh auth login`.

Useful flags:

| Flag | Description |
| --- | --- |
| `--tag vX.Y.Z` | Release a specific version instead of prompting |
| `--yes`, `-y` | Skip the confirmation prompt |
| `--dry-run` | Build the archives without publishing |
| `--allow-dirty` | Allow uncommitted changes (not recommended) |

The tag is created by the GitHub API, so the tag-push release workflow does not
run and the locally built archives are the published artifacts.

