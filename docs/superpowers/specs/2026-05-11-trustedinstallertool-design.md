# TrustedInstallerTool — Design

**Date:** 2026-05-11
**Status:** Approved for implementation planning
**Source:** Replaces `TrustedInstallercmd.bat`

## Goal

A single self-contained Windows executable (`TrustedInstallerTool.exe`) that launches an interactive shell running as `NT SERVICE\TrustedInstaller` (effectively SYSTEM with TrustedInstaller group membership), for developer use on the local machine.

Replaces the existing `TrustedInstallercmd.bat`, which depends on the PowerShell `NtObjectManager` module being installed system-wide and which flashes ~3 console windows on the way to spawning the final shell.

## Non-goals

- No GUI.
- No remote execution. Local only.
- No persistent service installation. The tool is launch-and-exit; the spawned shell is the only thing that lives on.
- Not a privilege-escalation tool against the OS. It uses the already-blessed `TrustedInstaller` service, which Microsoft ships running as SYSTEM.

## Requirements

1. **Single file.** `TrustedInstallerTool.exe`. No sidecar DLLs, no `.NET runtime` install required on the target machine.
2. **Size budget:** under 10 MB. Target 4–6 MB.
3. **Silent by default.** No console window for the tool itself. No MessageBox on success or failure. Only the spawned shell window is visible.
4. **UAC-elevated.** If launched non-elevated, Windows must prompt automatically before code runs.
5. **Configurable shell.** Default to `cmd.exe`. Allow the user to pass any command line as args.
6. **Opt-in logging.** A `/log` flag writes a log file next to the exe; without the flag, the tool is fully silent and only signals failure through its exit code.

## Architecture

One C# project, one source file, no external NuGet dependencies.

- **Toolchain:** Visual Studio 2026 Insiders, .NET 9 SDK.
- **Project type:** `OutputType=WinExe` (windows-subsystem so the tool itself has no console).
- **Publish mode:** Native AOT (`PublishAot=true`), single-file, self-contained, `InvariantGlobalization=true`, full trimming.
- **Manifest:** embedded `app.manifest` with `requestedExecutionLevel level="requireAdmin" uiAccess="false"`. Windows shows the UAC prompt before `Main` runs, eliminating any need for self-relaunch logic.

### Win32 surface

All interop via `LibraryImport` source-generated P/Invoke (Native-AOT friendly).

- `advapi32.dll`: `OpenSCManager`, `OpenService`, `ChangeServiceConfig`, `StartService`, `QueryServiceStatusEx`, `CloseServiceHandle`.
- `kernel32.dll`: `OpenProcess`, `InitializeProcThreadAttributeList`, `UpdateProcThreadAttribute`, `DeleteProcThreadAttributeList`, `CreateProcessW`, `CloseHandle`, `GetLastError`.

## Execution flow

1. **Parse args.** Strip the log flag if present — accept `/log`, `-log`, and `--log` (case-insensitive). Treat remaining args as the shell command line. If none, default to `cmd.exe`.
2. **Open log** (only if `/log`). Path: `<exe-dir>\TrustedInstallerTool.log`, append mode, UTF-8. Each line: ISO-8601 timestamp + step + result.
3. **Open SCM** with `SC_MANAGER_CONNECT`.
4. **Open service** `TrustedInstaller` with `SERVICE_CHANGE_CONFIG | SERVICE_START | SERVICE_QUERY_STATUS`.
5. **ChangeServiceConfig** to set `lpBinaryPathName = C:\Windows\servicing\TrustedInstaller.exe`. Other fields use `SERVICE_NO_CHANGE`.
6. **StartService.** If it returns `ERROR_SERVICE_ALREADY_RUNNING`, that's success.
7. **Poll `QueryServiceStatusEx`** until `dwCurrentState == SERVICE_RUNNING` and `dwProcessId != 0`. Max wait ~5 s; bail on timeout.
8. **OpenProcess** with `PROCESS_CREATE_PROCESS` on `dwProcessId`. This is the TrustedInstaller process handle.
9. **InitializeProcThreadAttributeList** (1 attribute), then **UpdateProcThreadAttribute** with `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` pointing at the TI handle.
10. **CreateProcessW** with the shell command line. Flags: `EXTENDED_STARTUPINFO_PRESENT | CREATE_NEW_CONSOLE`. `lpStartupInfo` points at the `STARTUPINFOEX`.
11. **Close** the four handles (TI process, child process, child thread, attribute list). **Exit 0.**

The spawned shell inherits the TrustedInstaller process's primary token, giving it SYSTEM + `NT SERVICE\TrustedInstaller` group membership.

## CLI

```
TrustedInstallerTool.exe                       # silent → cmd.exe
TrustedInstallerTool.exe /log                  # cmd.exe + log next to exe
TrustedInstallerTool.exe --log                 # same; -log and --log also accepted
TrustedInstallerTool.exe powershell            # silent → powershell.exe
TrustedInstallerTool.exe /log powershell       # log + powershell.exe
TrustedInstallerTool.exe "notepad foo.txt"     # silent → that command, as TI
TrustedInstallerTool.exe /log cmd /k "whoami"  # log + cmd.exe running `whoami`
```

Argument joining: the log flag (any of `/log`, `-log`, `--log`, case-insensitive) is removed, then the remaining `string[] args` are passed verbatim to `CreateProcessW` as a single command-line string built via the same quoting rules `Process.Start` uses (each arg with whitespace gets wrapped in `"…"`).

## Error handling

| Failure | Behavior |
|---|---|
| Any Win32 call fails | Exit with non-zero code. Log line written **only if `/log`**. No UI. |
| Service won't reach RUNNING within ~5 s | Same. |
| User clicks "No" on UAC prompt | Process never starts; nothing for us to do. |
| `/log` and the log file can't be opened | Continue silently without logging; do not abort the main task. |

Exit codes:
- `0` — shell spawned successfully.
- `1` — argument parsing error (shouldn't happen given the loose CLI).
- `2` — service configuration / start failed.
- `3` — TI process open failed.
- `4` — CreateProcess failed.

## File layout

```
trustedinstallertool/
  TrustedInstallerTool.csproj
  Program.cs
  app.manifest
  TrustedInstallercmd.bat        (kept as historical reference)
  docs/superpowers/specs/
    2026-05-11-trustedinstallertool-design.md   (this file)
```

## Testing

Manual, on the developer machine. Native AOT + UAC + service manipulation does not lend itself to automated unit tests in this codebase, and the surface area is small enough that the cost of a test rig would dwarf the value.

Verification checklist for the implementation plan:
1. Run `TrustedInstallerTool.exe` from Explorer (non-elevated). UAC prompt appears. After accept, a single `cmd.exe` window appears.
2. In that `cmd.exe`, run `whoami /groups`. Confirm `NT SERVICE\TrustedInstaller` is present.
3. Run `TrustedInstallerTool.exe powershell` — a `powershell.exe` window appears with TI membership.
4. Run `TrustedInstallerTool.exe /log` once, then inspect `TrustedInstallerTool.log` next to the exe — confirm step lines are written.
5. Run `TrustedInstallerTool.exe` without `/log` — confirm no log file is created.
6. Confirm published exe is under 10 MB.

## Open risks

- **Native AOT + manifest embedding.** The .NET SDK supports `<ApplicationManifest>` for AOT-published WinExe; verify on first build.
- **Attribute-list interop.** `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` requires correct sizing of the attribute list buffer; standard idiom is well-documented.
- **Service binpath change persists.** The bat script sets the binpath every run; this is harmless because it's set to the OS default. We do the same to match bat behavior.
