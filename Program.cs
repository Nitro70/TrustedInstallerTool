using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

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

    // ===== Privilege enable (SeDebugPrivilege) =====
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY             = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED    = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES_ONE { public uint PrivilegeCount; public LUID_AND_ATTRIBUTES Privilege; }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16, EntryPoint = "LookupPrivilegeValueW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES_ONE NewState,
        uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    private static void EnableDebugPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
            throw new TiException(2, $"OpenProcessToken failed (err={Marshal.GetLastWin32Error()})");
        try
        {
            if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out LUID luid))
                throw new TiException(2, $"LookupPrivilegeValue(SeDebugPrivilege) failed (err={Marshal.GetLastWin32Error()})");

            var tp = new TOKEN_PRIVILEGES_ONE
            {
                PrivilegeCount = 1,
                Privilege = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED }
            };

            // AdjustTokenPrivileges can return TRUE while only partially succeeding.
            // The authoritative result is in GetLastError, which is ERROR_SUCCESS only on full success.
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                throw new TiException(2, $"AdjustTokenPrivileges failed (err={Marshal.GetLastWin32Error()})");
            int post = Marshal.GetLastWin32Error();
            if (post != 0)
                throw new TiException(2, $"AdjustTokenPrivileges did not enable SeDebugPrivilege (err={post})");

            Log("SeDebugPrivilege enabled");
        }
        finally { CloseHandle(token); }
    }

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
            var exePath = Environment.ProcessPath;
            var dir = (exePath is null ? null : Path.GetDirectoryName(exePath)) ?? Environment.CurrentDirectory;
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

    private static int Main(string[] args)
    {
        var (logging, cmdLine) = ParseArgs(args);
        if (logging) TryOpenLog();
        Log($"start cmdLine='{cmdLine}'");

        try
        {
            EnableDebugPrivilege();
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
}
