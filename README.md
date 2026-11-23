# syspect

Native Windows 11 system diagnostics, repair, and maintenance utility built with **WinUI 3** and **.NET 10** by [Tenkayro](https://tenkayro.de).

## Features

- **Overview**: Hardware, BIOS, OS, and physical NVMe/SSD S.M.A.R.T. health (temperature, wear, read/write errors), plus quick maintenance actions.
- **Repair**: Guided Windows repair pipeline (`SSD & S.M.A.R.T.`, `chkdsk C: /scan`, `DISM CheckHealth / ScanHealth / RestoreHealth`, `sfc /scannow`, `StartComponentCleanup`, optional cleanup), boot `chkdsk C: /f` scheduling, live progress bar, decoded console stream, and structured `CBS.log` parser.
- **Drivers**: PnP error detection, signed driver inventory, and automatic flagging of generic Microsoft inbox drivers on vendor hardware.
- **Startup & Services**: Manage Registry `Run`, Startup folders, UWP Store startup tasks, third-party services (with safety tiers protecting WSL/`SharedAccess`/NVIDIA and confirming DAW/iLok/VMware services), and non-Microsoft scheduled tasks (detecting missing `.exe` orphans).
- **Firewall**: Profile and remote-vector audit (Domain/Private/Public, RDP, WinRM, SMB), plus selective cleanup of orphaned inbound rules.
- **Logs & CBS**: Unified viewer for saved `syspect` logs and live `C:\Windows\Logs\CBS\CBS.log` repair summaries.

## Installation

Download the latest `syspect-<version>-win-x64.msi` from [GitHub Releases](https://github.com/Tenkayro/syspect/releases) to install to `C:\Program Files\syspect`, or use the portable `.zip` package. Requires Windows 10/11 (x64) and administrator privileges (requested automatically via UAC).

## Development

Requires `.NET 10 SDK`, `Windows 10/11 SDK (10.0.26100.0)`, and `Node.js`.

```powershell
npm test        # Run Syspect.Core unit and live read-only diagnostics tests
npm run start   # Incremental Debug build and elevated launch
npm run build   # Trimmed Release win-x64 publish to ./publish
npm run setup   # Build Release publish + WiX v6 MSI installer + ZIP + SHA256SUMS.txt in ./dist
npm run release # Version bump, verify, build, tag, and publish a GitHub Release (Tenkayro/syspect)
```
