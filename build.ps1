# Builds Kadr.exe with the C# compiler that ships with Windows (.NET Framework 4.8) — nothing to install.
# The icon comes from icons\kadr.ico (drawn by qt\tools\draw_icon.py); it is also embedded for the tray and windows.
param([string]$TestOut = "")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$refs = "/r:System.dll", "/r:System.Core.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll", "/r:System.Net.Http.dll", "/r:System.Web.Extensions.dll", "/r:System.Security.dll"
$src = @(Get-ChildItem "$root\src\*.cs" | ForEach-Object { $_.FullName })
# Version for the updater: the release tag on GitHub (VERSION=v1.2.0), "0.0.0-dev" for builds made anywhere else
$ver = if ($env:VERSION) { $env:VERSION.TrimStart("v") } else { "0.0.0-dev" }
$verFile = Join-Path $env:TEMP "kadr-version.cs"
$repo = if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { "marseillered-design/kadr" }   # where updates come from
$imgur = if ($env:KADR_IMGUR_CLIENT_ID) { $env:KADR_IMGUR_CLIENT_ID.Trim() } else { "" }   # GitHub secret; empty in local builds
"[assembly: System.Reflection.AssemblyInformationalVersion(`"$ver`")]", "[assembly: System.Reflection.AssemblyMetadata(`"Repo`", `"$repo`")]", "[assembly: System.Reflection.AssemblyMetadata(`"ImgurClientId`", `"$imgur`")]" | Set-Content $verFile -Encoding UTF8
$src += $verFile
$ico = "$root\icons\kadr.ico"
$common = @("/nologo", "/codepage:65001", "/optimize+", "/platform:anycpu", "/win32manifest:$root\app.manifest", "/resource:$ico,Kadr.icon.ico") + $refs

& $csc @common /target:winexe "/out:$root\Kadr.exe" "/win32icon:$ico" @src
if ($LASTEXITCODE -ne 0) { exit 1 }

if ($TestOut) {
    & $csc @common /target:exe "/out:$TestOut" @src
    if ($LASTEXITCODE -ne 0) { exit 1 }
}
Write-Output "OK: $root\Kadr.exe"
