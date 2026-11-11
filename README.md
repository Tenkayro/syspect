# Syspect

**A clear, interactive Linux system diagnostics tool.**

Syspect helps you understand what is running on your Linux machine. It collects hardware, operating system, storage, network, temperature, service, kernel, and virtualization information in one terminal interface.

## What it does

- Interactive terminal dashboard built with Bubble Tea.
- Quick, full, hardware, and server diagnostic profiles.
- CPU, memory, graphics, motherboard, disks, network, sensors, and more.
- Most values are read directly from `/sys`, `/proc`, and the Go standard library, so no extra tools are required.
- Firmware and board identity from DMI/SMBIOS, CPU caches and vulnerability mitigations, NVMe controllers, hwmon temperatures, EDAC memory modules, kernel taint state, and security hardening checks.
- Built-in DNS resolver benchmark: real-world, warm-cache, and cache-miss lookups with average, median, P90/P95/P99, min, max, standard deviation, and failure counts.
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

Linux on **x86_64** and **ARM64** is supported. The installer places the program in `~/.local/bin` and adds that directory to your shell configuration (`~/.bashrc`, `~/.zshrc`, or the fish config). Afterwards it asks whether to start Syspect right away. Set `SYSPECT_NO_PATH_MODIFY=1` if you do not want the installer to touch your shell configuration and `SYSPECT_NO_PROMPT=1` to skip the start prompt.

## Reports and settings

Reports are saved locally in `~/.local/share/syspect/reports/`. Settings are stored in `~/.config/syspect/`. You can enable privacy mode and choose which optional checks to run inside Syspect.

The result screen shows pass, warning, failed, and skipped counters. Every measured value is written to the HTML and JSON reports, and the report path is shown after each scan.

## DNS benchmark

The **Run DNS benchmark** menu entry measures the system resolver and well-known public resolvers. It runs three phases:

- **Real-world lookups** of popular domains.
- **Warm-cache lookups** after priming the resolver caches.
- **Cache-miss lookups** against unique `example.com` names.

For each resolver and phase it reports sample count, failures, average, median, P90, P95, P99, minimum, maximum, and standard deviation in milliseconds. Unreachable resolvers are listed separately instead of skewing the ranking.

The benchmark is implemented in Go and does not need `dig`. It only sends DNS queries and never changes the system DNS configuration. Because it produces network traffic, it is opt-in: it runs when you select it from the menu or when you pass `--network` on the command line.

## Command line

```bash
syspect --scan [profile]
```

Runs a scan without the interactive interface and prints the same structured results as the JSON report to stdout. Progress is written to stderr.

| Profile | Categories |
| --- | --- |
| `full` | All categories except the DNS benchmark (default) |
| `quick` | Fast OS, CPU, memory, GPU, storage, security, and virtualization overview |
| `hardware` | CPU, memory, GPU, board, storage, network, sensors, and battery |
| `server` | OS, CPU, memory, storage, network, kernel, services, logs, security, and virtualization |
| `dns` | DNS resolver benchmark (implies `--network`) |

Other flags: `--network` enables opt-in network tests, `--paths` prints the data directory, and `--version` prints the version.

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

