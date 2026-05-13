# TrustedInstallerTool Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `TrustedInstallerTool.exe` — a silent, single-file, ~5 MB Native-AOT Windows executable that launches an interactive shell running as `NT SERVICE\TrustedInstaller`, replacing `TrustedInstallercmd.bat`.

**Architecture:** One .NET 10 C# project (`PublishAot=true`, `OutputType=WinExe`), one source file (`Program.cs`), one embedded `app.manifest` for auto-elevation. All Win32 work via source-generated P/Invoke (`LibraryImport`) — no NuGet dependencies. Service start uses `advapi32` SCM APIs; child process inherits the TrustedInstaller token via `STARTUPINFOEX` + `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS`.

**Tech Stack:** .NET 10 SDK, C# 13, Native AOT, Visual Studio 2026 Insiders, Win32 (advapi32 + kernel32).

**Spec reference:** [2026-05-11-trustedinstallertool-design.md](../specs/2026-05-11-trustedinstallertool-design.md)

**Testing note:** This tool's behavior (UAC elevation, service manipulation, token inheritance) is not amenable to automated unit tests in a useful way. The plan uses **manual verification gates** between tasks instead of TDD. Every task ends with a concrete observable check before commit.

---

## File Structure

```
trustedinstallertool/
  TrustedInstallerTool.csproj      (Task 1)
  app.manifest                     (Task 1)
  Program.cs                       (Tasks 2-6, grown incrementally)
  TrustedInstallercmd.bat          (kept as-is; reference)
  docs/superpowers/
    specs/2026-05-11-trustedinstallertool-design.md
    plans/2026-05-11-trustedinstallertool.md   (this file)
```

`Program.cs` is one file because the total surface is ~250 lines and every section uses the same P/Invoke types. Splitting would force shared internal types and create more friction than it removes.

---

## Task 1: Project scaffolding + UAC manifest

**Files:**
- Create: `D:\tools\repos\trustedinstallertool\TrustedInstallerTool.csproj`
- Create: `D:\tools\repos\trustedinstallertool\app.manifest`
- Create: `D:\tools\repos\trustedinstallertool\Program.cs` (stub)

- [ ] **Step 1: Create `TrustedInstallerTool.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RootNamespace>TrustedInstallerTool</RootNamespace>
    <AssemblyName>TrustedInstallerTool</AssemblyName>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>

    <!-- Native AOT -->
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <StripSymbols>true</StripSymbols>
    <OptimizationPreference>Size</OptimizationPreference>
    <IlcGenerateStackTraceData>false</IlcGenerateStackTraceData>
    <DebuggerSupport>false</DebuggerSupport>
    <EnableUnsafeUTF7Encoding>false</EnableUnsafeUTF7Encoding>
    <HttpActivityPropagationSupport>false</HttpActivityPropagationSupport>

    <!-- Embedded manifest -->
    <ApplicationManifest>app.manifest</ApplicationManifest>

    <!-- Build defaults -->
    <Platforms>x64</Platforms>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Create `app.manifest`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="TrustedInstallerTool" type="win32"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false"/>
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <!-- Windows 10/11 -->
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/>
    </application>
  </compatibility>
</assembly>
```

- [ ] **Step 3: Create stub `Program.cs`**

```csharp
namespace TrustedInstallerTool;

internal static class Program
{
    private static int Main(string[] args) => 0;
}
```

- [ ] **Step 4: Build and verify it compiles + elevates**

Run from a normal (non-admin) terminal in the project directory:

```
dotnet build -c Release
```

Expected: build succeeds with zero warnings.

Then run the *built* (non-published) exe:

```
.\bin\Release\net10.0-windows\win-x64\TrustedInstallerTool.exe
```

Expected: UAC prompt appears. After clicking Yes, exe exits with code 0 and no visible window.

- [ ] **Step 5: Commit**

```
git add TrustedInstallerTool.csproj app.manifest Program.cs
git commit -m "feat: scaffold TrustedInstallerTool AOT project with UAC manifest"
```

> Skip the commit step on any task if `D:\tools\repos\trustedinstallertool` is not a git repo. Ask the user whether to `git init` before starting Task 1 — if yes, run `git init` then proceed; if no, skip every commit step in this plan.

---

## Task 2: Win32 interop (constants, structs, P/Invoke)

**Files:**
- Modify: `Program.cs` — add interop region

- [ ] **Step 1: Replace `Program.cs` with the full interop + stub Main**

```csharp
using System;
using System.Runtime.InteropServices;

namespace TrustedInstallerTool;

internal static unsafe partial class Program
{
    // ===== Service Control Manager constants =====
    private const uint SC_MANAGER_CONNECT     = 0x0001;
    private const uint SERVICE_QUERY_STATUS   = 0x0004;
    private const uint SERVICE_CHANGE_CONFIG  = 0x0002;
    private const uint SERVICE_START          = 0x0010;
    private const uint SERVICE_NO_CHANGE      = 0xFFFFFFFF;
    private const uint SERVICE_RUNNING        = 0x00000004;
    private const int  SC_STATUS_PROCESS_INFO = 0;
    private const int  ERROR_SERVICE_ALREADY_RUNNING = 1056;

    // ===== Process creation constants =====
    private const uint PROCESS_CREATE_PROCESS       = 0x0080;
    private const uint CREATE_NEW_CONSOLE           = 0x00000010;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PARENT_PROCESS = (IntPtr)0x00020000;

    // ===== Structs =====
    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public uint   cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint   dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr     lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint   dwProcessId;
        public uint   dwThreadId;
    }

    // ===== advapi32 P/Invoke =====
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16, EntryPoint = "OpenSCManagerW")]
    private static partial IntPtr OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16, EntryPoint = "OpenServiceW")]
    private static partial IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16, EntryPoint = "ChangeServiceConfigW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig(
        IntPtr hService,
        uint dwServiceType, uint dwStartType, uint dwErrorControl,
        string? lpBinaryPathName, string? lpLoadOrderGroup, IntPtr lpdwTagId,
        string? lpDependencies, string? lpServiceStartName, string? lpPassword,
        string? lpDisplayName);

    [LibraryImport("advapi32.dll", SetLastError = true, EntryPoint = "StartServiceW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartService(IntPtr hService, uint dwNumServiceArgs, IntPtr lpServiceArgVectors);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(
        IntPtr hService, int InfoLevel, IntPtr lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr hSCObject);

    // ===== kernel32 P/Invoke =====
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, uint dwAttributeCount, uint dwFlags, ref nuint lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute,
        IntPtr lpValue, nuint cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcess(
        string? lpApplicationName,
        [In, Out] char[] lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    private static int Main(string[] args) => 0;
}
```

- [ ] **Step 2: Build to verify interop compiles**

```
dotnet build -c Release
```

Expected: build succeeds, zero warnings. The `LibraryImport` source generator runs cleanly.

- [ ] **Step 3: Commit**

```
git add Program.cs
git commit -m "feat: add Win32 P/Invoke surface for SCM + process creation"
```

---

## Task 3: Arg parser + optional logger + exit-code model

**Files:**
- Modify: `Program.cs` — replace `Main` and add `ParseArgs`, `JoinArgs`, `Log`, `TiException`

- [ ] **Step 1: Add `using` directives at top of `Program.cs`**

Replace the existing using block with:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
```

- [ ] **Step 2: Replace `Main` and add helpers**

Replace the stub `private static int Main(string[] args) => 0;` with this block (inside `Program`):

```csharp
    // ===== Exit codes =====
    // 0 success, 1 arg error, 2 service fail, 3 OpenProcess fail, 4 CreateProcess fail.
    private sealed class TiException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    // ===== Logger =====
    private static StreamWriter? _log;

    private static void TryOpenLog()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
            var dir = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;
            var path = Path.Combine(dir, "TrustedInstallerTool.log");
            _log = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
        }
        catch
        {
            // Logging is best-effort. Failure must never abort the main task.
            _log = null;
        }
    }

    private static void Log(string msg)
    {
        if (_log is null) return;
        _log.WriteLine($"{DateTime.UtcNow:O} {msg}");
        _log.Flush();
    }

    // ===== Argument parsing =====
    private static (bool Logging, string CommandLine) ParseArgs(string[] args)
    {
        bool logging = false;
        var rest = new List<string>(args.Length);
        foreach (var a in args)
        {
            if (IsLogFlag(a)) logging = true;
            else rest.Add(a);
        }
        var cmd = rest.Count == 0 ? "cmd.exe" : JoinArgs(rest);
        return (logging, cmd);
    }

    private static bool IsLogFlag(string a) =>
        string.Equals(a, "/log",  StringComparison.OrdinalIgnoreCase) ||
        string.Equals(a, "-log",  StringComparison.OrdinalIgnoreCase) ||
        string.Equals(a, "--log", StringComparison.OrdinalIgnoreCase);

    // CRT-compatible argv joining (matches Process.Start's quoting rules).
    private static string JoinArgs(List<string> args)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < args.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            AppendArg(sb, args[i]);
        }
        return sb.ToString();
    }

    private static void AppendArg(StringBuilder sb, string a)
    {
        if (a.Length > 0 && a.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            sb.Append(a);
            return;
        }
        sb.Append('"');
        int backslashes = 0;
        foreach (var c in a)
        {
            if (c == '\\') { backslashes++; }
            else if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); backslashes = 0; }
            else { sb.Append('\\', backslashes); sb.Append(c); backslashes = 0; }
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }

    private static int Main(string[] args)
    {
        var (logging, cmdLine) = ParseArgs(args);
        if (logging) TryOpenLog();
        Log($"start cmdLine='{cmdLine}'");
        Log("end (stub)");
        return 0;
    }
```

- [ ] **Step 3: Build**

```
dotnet build -c Release
```

Expected: zero warnings.

- [ ] **Step 4: Manually verify logger + arg parsing**

```
.\bin\Release\net10.0-windows\win-x64\TrustedInstallerTool.exe /log powershell -NoProfile
```

Expected: UAC prompt, then no window, exit 0. A file `TrustedInstallerTool.log` exists next to the exe, containing two lines with timestamps and `cmdLine='powershell -NoProfile'`.

Now run without `/log`:

```
del .\bin\Release\net10.0-windows\win-x64\TrustedInstallerTool.log
.\bin\Release\net10.0-windows\win-x64\TrustedInstallerTool.exe powershell
```

Expected: no log file is created (silent run).

- [ ] **Step 5: Commit**

```
git add Program.cs
git commit -m "feat: add /log flag parsing, log writer, and CRT-compatible argv joining"
```

---

## Task 4: TrustedInstaller service start + PID retrieval

**Files:**
- Modify: `Program.cs` — add `ConfigureAndStartService`

- [ ] **Step 1: Add the service helper above `Main`**

Insert this block inside `Program` immediately above the `Main` method:

```csharp
    private const string TI_SERVICE_NAME = "TrustedInstaller";
    private const string TI_BINARY_PATH  = @"C:\Windows\servicing\TrustedInstaller.exe";
    private const int    SERVICE_POLL_TIMEOUT_MS = 5000;
    private const int    SERVICE_POLL_INTERVAL_MS = 100;

    private static uint ConfigureAndStartService()
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
            throw new TiException(2, $"OpenSCManager failed (err={Marshal.GetLastWin32Error()})");

        try
        {
            IntPtr svc = OpenService(scm, TI_SERVICE_NAME, SERVICE_CHANGE_CONFIG | SERVICE_START | SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero)
                throw new TiException(2, $"OpenService(TrustedInstaller) failed (err={Marshal.GetLastWin32Error()})");

            try
            {
                if (!ChangeServiceConfig(
                        svc, SERVICE_NO_CHANGE, SERVICE_NO_CHANGE, SERVICE_NO_CHANGE,
                        TI_BINARY_PATH, null, IntPtr.Zero, null, null, null, null))
                {
                    throw new TiException(2, $"ChangeServiceConfig failed (err={Marshal.GetLastWin32Error()})");
                }
                Log("ChangeServiceConfig ok");

                if (!StartService(svc, 0, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ERROR_SERVICE_ALREADY_RUNNING)
                        throw new TiException(2, $"StartService failed (err={err})");
                    Log("service already running");
                }
                else
                {
                    Log("StartService dispatched");
                }

                return WaitForRunningPid(svc);
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    private static uint WaitForRunningPid(IntPtr svc)
    {
        int waited = 0;
        int structSize = Marshal.SizeOf<SERVICE_STATUS_PROCESS>();
        IntPtr buf = Marshal.AllocHGlobal(structSize);
        try
        {
            while (waited < SERVICE_POLL_TIMEOUT_MS)
            {
                if (!QueryServiceStatusEx(svc, SC_STATUS_PROCESS_INFO, buf, (uint)structSize, out _))
                    throw new TiException(2, $"QueryServiceStatusEx failed (err={Marshal.GetLastWin32Error()})");

                var status = Marshal.PtrToStructure<SERVICE_STATUS_PROCESS>(buf);
                if (status.dwCurrentState == SERVICE_RUNNING && status.dwProcessId != 0)
                {
                    Log($"service RUNNING pid={status.dwProcessId}");
                    return status.dwProcessId;
                }

                System.Threading.Thread.Sleep(SERVICE_POLL_INTERVAL_MS);
                waited += SERVICE_POLL_INTERVAL_MS;
            }
            throw new TiException(2, "service did not reach RUNNING within 5s");
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
```

- [ ] **Step 2: Wire it into `Main` (temporary verification)**

Replace the existing `Main` body with:

```csharp
    private static int Main(string[] args)
    {
        var (logging, cmdLine) = ParseArgs(args);
        if (logging) TryOpenLog();
        Log($"start cmdLine='{cmdLine}'");

        try
        {
            uint tiPid = ConfigureAndStartService();
            Log($"got TI pid {tiPid}");
            return 0;
        }
        catch (TiException ex)
        {
            Log($"fail code={ex.Code} msg={ex.Message}");
            return ex.Code;
        }
    }
```

- [ ] **Step 3: Build**

```
dotnet build -c Release
```

Expected: zero warnings.

- [ ] **Step 4: Manually verify the service starts and PID is captured**

```
.\bin\Release\net10.0-windows\win-x64\TrustedInstallerTool.exe /log
```

Expected: UAC prompt, then no window, exit 0. The log file contains lines `ChangeServiceConfig ok`, either `StartService dispatched` or `service already running`, and `service RUNNING pid=<number>`.

Cross-check in Task Manager: a `TrustedInstaller.exe` process is running with the same PID.

- [ ] **Step 5: Commit**

```
git add Program.cs
git commit -m "feat: configure + start TrustedInstaller service and capture its PID"
```

---

## Task 5: Spawn shell with TrustedInstaller as parent process

**Files:**
- Modify: `Program.cs` — add `SpawnShellAsTrustedInstaller`, wire into `Main`

- [ ] **Step 1: Add the spawn helper above `Main`**

Insert this block inside `Program`, immediately above `Main`:

```csharp
    private static void SpawnShellAsTrustedInstaller(string cmdLine, uint tiPid)
    {
        IntPtr tiHandle = OpenProcess(PROCESS_CREATE_PROCESS, false, tiPid);
        if (tiHandle == IntPtr.Zero)
            throw new TiException(3, $"OpenProcess(TI pid={tiPid}) failed (err={Marshal.GetLastWin32Error()})");

        IntPtr attrList = IntPtr.Zero;
        IntPtr parentHandlePtr = IntPtr.Zero;

        try
        {
            // Size the attribute list (1 attribute: PARENT_PROCESS).
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size); // expected: returns false, sets size
            attrList = Marshal.AllocHGlobal((int)size);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
                throw new TiException(4, $"InitializeProcThreadAttributeList failed (err={Marshal.GetLastWin32Error()})");

            // UpdateProcThreadAttribute needs a *pointer to* the handle, kept alive across the CreateProcess call.
            parentHandlePtr = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(parentHandlePtr, tiHandle);

            if (!UpdateProcThreadAttribute(
                    attrList, 0, PROC_THREAD_ATTRIBUTE_PARENT_PROCESS,
                    parentHandlePtr, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new TiException(4, $"UpdateProcThreadAttribute failed (err={Marshal.GetLastWin32Error()})");
            }

            var siex = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO { cb = (uint)Marshal.SizeOf<STARTUPINFOEX>() },
                lpAttributeList = attrList,
            };

            // CreateProcessW requires a writable buffer for lpCommandLine.
            var cmdBuf = new char[cmdLine.Length + 1];
            cmdLine.CopyTo(0, cmdBuf, 0, cmdLine.Length);
            cmdBuf[cmdLine.Length] = '\0';

            if (!CreateProcess(
                    null, cmdBuf, IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_NEW_CONSOLE,
                    IntPtr.Zero, null, ref siex, out var pi))
            {
                throw new TiException(4, $"CreateProcess failed (err={Marshal.GetLastWin32Error()})");
            }

            Log($"spawned child pid={pi.dwProcessId}");
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
        finally
        {
            if (attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
            if (parentHandlePtr != IntPtr.Zero) Marshal.FreeHGlobal(parentHandlePtr);
            CloseHandle(tiHandle);
        }
    }
```

- [ ] **Step 2: Replace `Main` with the final body**

```csharp
    private static int Main(string[] args)
    {
        var (logging, cmdLine) = ParseArgs(args);
        if (logging) TryOpenLog();
        Log($"start cmdLine='{cmdLine}'");

        try
        {
            uint tiPid = ConfigureAndStartService();
            SpawnShellAsTrustedInstaller(cmdLine, tiPid);
            Log("done");
            return 0;
        }
        catch (TiException ex)
        {
            Log($"fail code={ex.Code} msg={ex.Message}");
            return ex.Code;
        }
    }
```

- [ ] **Step 3: Build**

```
dotnet build -c Release
```

Expected: zero warnings.

- [ ] **Step 4: End-to-end manual verification — default shell**

```
.\bin\Release\net10.0-windows\win-x64\TrustedInstallerTool.exe /log
```

Expected:
1. UAC prompt appears, accept it.
2. A single `cmd.exe` window appears.
3. No other windows flash; the launcher itself shows nothing.
4. In the spawned `cmd.exe`, run:

```
whoami
whoami /groups | findstr TrustedInstaller
```

Expected: `whoami` prints `nt authority\system`. `whoami /groups` lists a group whose name contains `TrustedInstaller`.

5. Log file contains a `spawned child pid=<n>` line and a `done` line.

- [ ] **Step 5: End-to-end manual verification — alternate shell**

Close the previous cmd window, then:

```
.\bin\Release\net10.0-windows\win-x64\TrustedInstallerTool.exe powershell
```

Expected: a PowerShell window appears. Inside it:

```
[Security.Principal.WindowsIdentity]::GetCurrent().Groups | ForEach-Object { $_.Translate([Security.Principal.NTAccount]) } | Select-String TrustedInstaller
```

Expected: at least one matching line.

- [ ] **Step 6: Commit**

```
git add Program.cs
git commit -m "feat: spawn child shell with TrustedInstaller as parent process"
```

---

## Task 6: Native AOT publish + size verification

**Files:**
- (No code changes.)

- [ ] **Step 1: Publish Native AOT**

```
dotnet publish -c Release -r win-x64
```

Expected: build succeeds with zero warnings, and a final `.exe` is emitted under `bin\Release\net10.0-windows\win-x64\publish\TrustedInstallerTool.exe`.

- [ ] **Step 2: Confirm the published exe is under 10 MB**

```
(Get-Item .\bin\Release\net10.0-windows\win-x64\publish\TrustedInstallerTool.exe).Length / 1MB
```

Expected: a number under `10`. Target range: 3–7. If over 10 MB, audit `csproj` for missing AOT trim flags (the ones listed in Task 1 should drop it).

- [ ] **Step 3: Verify no sidecar files are required**

```
Get-ChildItem .\bin\Release\net10.0-windows\win-x64\publish\
```

Expected: the published folder contains `TrustedInstallerTool.exe` and at most a `.pdb` file. Anything else (e.g. `*.dll`) means single-file/AOT publish is misconfigured.

- [ ] **Step 4: Copy the exe somewhere clean and run it**

```
$dst = "$env:USERPROFILE\Desktop\ti-tool-test"
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item .\bin\Release\net10.0-windows\win-x64\publish\TrustedInstallerTool.exe $dst
& "$dst\TrustedInstallerTool.exe" /log
```

Expected: UAC prompt → `cmd.exe` window opens running as TI. A `TrustedInstallerTool.log` appears next to the exe in the test folder (not in the build directory).

Run again without `/log`:

```
Remove-Item "$dst\TrustedInstallerTool.log" -ErrorAction Ignore
& "$dst\TrustedInstallerTool.exe"
```

Expected: cmd window opens, **no** log file appears (silent run).

- [ ] **Step 5: Commit**

```
git add docs/
git commit -m "docs: trustedinstallertool spec and implementation plan"
```

(If the spec/plan have already been committed, skip this step.)

---

## Manual Verification Matrix (from spec)

After all tasks complete, run through this checklist on a clean machine state:

| Scenario | Command | Expected |
|---|---|---|
| Default shell, silent | `TrustedInstallerTool.exe` | UAC → one cmd window as TI, no log |
| Default shell, log | `TrustedInstallerTool.exe /log` | Same + log file next to exe |
| Default shell, log (linux flag) | `TrustedInstallerTool.exe --log` | Same as above |
| Alternate shell | `TrustedInstallerTool.exe powershell` | UAC → one powershell window as TI |
| Inline command | `TrustedInstallerTool.exe cmd /k whoami` | UAC → cmd window prints `nt authority\system` and stays open |
| Membership check (in spawned shell) | `whoami /groups` | Output lists `NT SERVICE\TrustedInstaller` |
| Size | `Get-Item` on published exe | < 10 MB |
| No sidecars | `dir` on publish dir | Only `.exe` (and optional `.pdb`) |

---

## Self-Review

- **Spec coverage:** Single-file ✓ (Task 6), size ✓ (Task 6), silent ✓ (Task 1 manifest + WinExe + Task 3 verification), UAC ✓ (Task 1), shell arg ✓ (Task 3 parser + Task 5 spawn), `/log` flag with `/-//--` variants ✓ (Task 3 `IsLogFlag`), log written next to exe ✓ (Task 3 `TryOpenLog`), exit codes 0/1/2/3/4 ✓ (Task 3 `TiException` + Tasks 4/5 throws). Service binpath set every run ✓ (Task 4 `ChangeServiceConfig`). PID via `QueryServiceStatusEx` ✓ (Task 4 `WaitForRunningPid`). Parent-process attribute ✓ (Task 5 `UpdateProcThreadAttribute`).
- **Placeholders:** None.
- **Type consistency:** `TiException(int code, string message)` used identically in Tasks 4 and 5. `ParseArgs` returns `(bool Logging, string CommandLine)` and `Main` destructures that exact shape in Tasks 3, 4, and 5. `SERVICE_STATUS_PROCESS.dwProcessId` (uint) flows into `OpenProcess(... uint dwProcessId)` consistently.
- **Argument-error exit code (1):** declared in `TiException` comment but no path throws it. Intentional — the loose CLI accepts any args; nothing currently fails parsing. Left in place for future tightening.
