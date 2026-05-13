# TrustedInstallerTool

A single-file Windows executable that opens an interactive shell running as
`NT SERVICE\TrustedInstaller` — for developers who need to read, modify, or
delete files and registry keys whose ACLs are restricted to the
TrustedInstaller service.

- **One file.** ~1.6 MB, no .NET runtime install required.
- **No dependencies.** Pure Win32 P/Invoke. No NuGet, no PowerShell modules.
- **Silent.** No console window of its own; only the spawned shell is visible.
- **Auto-elevating.** Embedded UAC manifest prompts for admin on launch.

This replaces the `TrustedInstallercmd.bat` script (kept in the repo as
historical reference), which required James Forshaw's `NtObjectManager`
PowerShell module to be installed system-wide and flashed several windows
during startup.

## Usage

```
TrustedInstallerTool.exe                       # silent → cmd.exe as TI
TrustedInstallerTool.exe powershell            # silent → powershell.exe as TI
TrustedInstallerTool.exe cmd /k whoami         # cmd running whoami, as TI

TrustedInstallerTool.exe /log                  # default shell + log next to exe
TrustedInstallerTool.exe --log powershell      # same flag, GNU style
```

Flags accepted: `/log`, `-log`, `--log` (case-insensitive). The flag is
stripped from `argv`; the rest is passed verbatim as the command line to
`CreateProcessW`.

### Verifying it worked

Inside the spawned shell:

```cmd
whoami            :: should print  nt authority\system
whoami /groups | findstr /i trusted
                  :: should list  NT SERVICE\TrustedInstaller  as a group
```

If both are true, the shell really is running with TrustedInstaller authority
and can touch files normally restricted to that account.

## How it works

1. Embedded `app.manifest` requests `requireAdministrator`, so Windows shows
   the UAC prompt before `Main` runs.
2. `EnableDebugPrivilege()` flips `SeDebugPrivilege` from disabled to
   enabled on the current admin token. Without this, `OpenProcess` on the
   `TrustedInstaller.exe` service process returns `ERROR_ACCESS_DENIED`.
3. The TrustedInstaller service is reconfigured (`sc config` equivalent)
   to its default binary path and started via the Service Control Manager.
   `QueryServiceStatusEx` returns the service's PID once it reaches
   `SERVICE_RUNNING`.
4. `OpenProcess(PROCESS_CREATE_PROCESS)` on that PID yields a handle to
   the running `TrustedInstaller.exe`.
5. `CreateProcessW` is invoked with a `STARTUPINFOEX` containing the
   `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` attribute set to the TI handle.
   The new process is parented under TI and inherits its primary token,
   giving it `NT AUTHORITY\SYSTEM` identity plus `NT SERVICE\TrustedInstaller`
   group membership.

The technique is the same one James Forshaw documented for `NtObjectManager`'s
`New-Win32Process -ParentProcess`; this project just replicates it in
straight Win32 P/Invoke so no PowerShell module is required.

## Building from source

### Prerequisites

- **.NET 10 SDK** (preview is fine; verified on `10.0.300-preview.0.26177.108`).
- **Visual Studio C++ Build Tools** — Native AOT calls `link.exe`. Either
  install the "Desktop development with C++" workload in Visual Studio 2026
  Insiders / Build Tools, or any equivalent MSVC toolchain.
- **`vswhere.exe`** must be discoverable on `PATH`. It ships at
  `C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe`.
  Either add that directory to `PATH`, or build from a "Developer PowerShell
  for VS" shell which sets it up automatically.

### Build

```powershell
# From a Developer PowerShell, or after adding the VS Installer dir to PATH:
dotnet publish -c Release -r win-x64
```

The single-file exe lands at:

```
bin\Release\net10.0-windows\win-x64\publish\TrustedInstallerTool.exe
```

`dotnet build` (without publish) also works but produces a fat IL exe plus
~150 sidecar DLLs. Only `publish` yields the standalone Native-AOT binary.

## Exit codes

| Code | Meaning |
|------|---------|
| 0 | Shell spawned successfully |
| 2 | Service configuration / start / privilege adjust failed |
| 3 | `OpenProcess(TrustedInstaller)` failed |
| 4 | `CreateProcess` (the actual shell spawn) failed |

Without `/log`, failures are silent — the tool just returns the code above.
Pass `/log` to get a one-line-per-step diagnostic in
`TrustedInstallerTool.log` next to the executable.

## Caveats

- **Windows only**, x64. The whole project is built around Win32 services
  and SCM. Not portable.
- **Admin required.** Without elevation the manifest blocks the launch.
- **Some AV/EDR products will flag this.** The "parent-process spoofing to
  inherit a SYSTEM token" pattern is also used by malware. The technique
  itself is documented Microsoft API usage and the TrustedInstaller service
  is a legitimate OS component, but expect potential false positives on
  managed corporate machines.
- **Local use only.** Nothing here implements remote execution; this is a
  desktop developer utility.

## License

Not specified yet. Treat as personal-use for now.
