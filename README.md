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

Linux on **x86_64** and **ARM64** is supported. The installer places the program in `~/.local/bin`. Make sure that directory is in your `PATH`.

## Reports and settings

Reports are saved locally in `~/.local/share/syspect/reports/`. Settings are stored in `~/.config/syspect/`. You can enable privacy mode and choose which optional checks to run inside Syspect.

## Uninstall

Remove the application and keep your reports and settings:

```bash
curl -fsSL https://raw.githubusercontent.com/Tenkayro/syspect/main/uninstall.sh | bash
```

To remove reports and settings as well, run `bash uninstall.sh --purge` after downloading the script.
