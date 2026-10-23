# Builds AirPlayCast and installs it for the current user, then starts it.
# Usage:  powershell -ExecutionPolicy Bypass -File install.ps1
#
# The app runs as "dotnet AirPlayCast.dll" (inside a headless conhost, so no console window):
# Smart App Control blocks unsigned self-built .exe files but allows the signed dotnet host.
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\AirPlayCast'

Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*AirPlayCast.dll*' -and $_.Name -eq 'dotnet.exe' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 500

dotnet publish "$PSScriptRoot\AirPlayCast.csproj" -c Release -o $target --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$dotnet = (Get-Command dotnet).Source
$conhost = Join-Path $env:SystemRoot 'System32\conhost.exe'
$dll = Join-Path $target 'AirPlayCast.dll'
$launchArgs = "--headless `"$dotnet`" `"$dll`""

$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'AirPlayCast.lnk'
$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath = $conhost
$s.Arguments = $launchArgs
$s.WorkingDirectory = $target
$s.IconLocation = Join-Path $target 'app.ico'
$s.Description = 'Stream Windows audio to AirPlay receivers'
$s.Save()

Start-Process $conhost -ArgumentList $launchArgs -WorkingDirectory $target
Write-Host "Installed to $target and started. Look for the icon in the system tray."
