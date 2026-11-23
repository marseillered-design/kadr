# Builds Kadr.exe with the C# compiler that ships with Windows (.NET Framework 4.8) — nothing to install.
# The icon comes from icons\kadr.ico (drawn by qt\tools\draw_icon.py); it is also embedded for the tray and windows.
param([string]$TestOut = "")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$refs = "/r:System.dll", "/r:System.Core.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll", "/r:System.Net.Http.dll", "/r:System.Web.Extensions.dll", "/r:System.Security.dll"
$src = Get-ChildItem "$root\src\*.cs" | ForEach-Object { $_.FullName }
$ico = "$root\icons\kadr.ico"
$common = @("/nologo", "/codepage:65001", "/optimize+", "/platform:anycpu", "/win32manifest:$root\app.manifest", "/resource:$ico,Kadr.icon.ico") + $refs

& $csc @common /target:winexe "/out:$root\Kadr.exe" "/win32icon:$ico" @src
if ($LASTEXITCODE -ne 0) { exit 1 }

if ($TestOut) {
    & $csc @common /target:exe "/out:$TestOut" @src
    if ($LASTEXITCODE -ne 0) { exit 1 }
}
Write-Output "OK: $root\Kadr.exe"
