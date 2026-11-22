# Builds Kadr.exe with the C# compiler that ships with Windows (.NET Framework 4.8) — nothing to install.
param([string]$TestOut = "")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$refs = "/r:System.dll", "/r:System.Core.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll", "/r:System.Net.Http.dll", "/r:System.Web.Extensions.dll"
$src = Get-ChildItem "$root\src\*.cs" | ForEach-Object { $_.FullName }
$common = @("/nologo", "/codepage:65001", "/optimize+", "/platform:anycpu", "/win32manifest:$root\app.manifest") + $refs

$ico = "$root\kadr.ico"
if (-not (Test-Path $ico)) {
    # First build without an icon, let the program draw its own icon, then build again with it.
    $tmp = Join-Path $env:TEMP "kadr-icon-build.exe"
    & $csc @common /target:winexe "/out:$tmp" @src
    if ($LASTEXITCODE -ne 0) { exit 1 }
    Start-Process $tmp -ArgumentList "--make-icon", "`"$ico`"" -Wait
    Remove-Item $tmp
}

& $csc @common /target:winexe "/out:$root\Kadr.exe" "/win32icon:$ico" @src
if ($LASTEXITCODE -ne 0) { exit 1 }

if ($TestOut) {
    & $csc @common /target:exe "/out:$TestOut" @src
    if ($LASTEXITCODE -ne 0) { exit 1 }
}
Write-Output "OK: $root\Kadr.exe"
