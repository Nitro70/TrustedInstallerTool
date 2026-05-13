@echo off
:: Check if the script is running as admin
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrative privileges...
    powershell -Command "Start-Process -FilePath '%0' -ArgumentList '-RunAsAdmin' -Verb RunAs"
    exit /b
)

:: If running as admin, execute multiple commands in one PowerShell session
powershell -Command "Import-Module NtObjectManager; Start-Sleep -Seconds 0.5; sc.exe config TrustedInstaller binpath= 'C:\Windows\servicing\TrustedInstaller.exe'; Start-Sleep -Seconds 0.5; sc.exe start TrustedInstaller; Start-Sleep -Seconds 0.5; $p = Get-NtProcess TrustedInstaller.exe | Select-Object -First 1; Start-Sleep -Seconds 0.5; $proc = New-Win32Process cmd.exe -CreationFlags NewConsole -ParentProcess $p"
