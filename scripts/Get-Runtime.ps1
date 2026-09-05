<#
.SYNOPSIS
    Sets up the Android runtime that PortableDroid drives.

.DESCRIPTION
    PortableDroid ships without QEMU, adb or an Android image. That is deliberate:

      * QEMU is GPL-2.0 and is easiest to keep compliant when you fetch the official build.
      * Google's platform-tools are covered by the Android SDK Terms, which restrict
        redistribution by third parties.
      * Android system images vary in licensing; the FOSS/Vanilla builds are the ones
        that are safe to use, and you should download them yourself.

    This script checks what is missing and tells you exactly where each piece goes.
    It downloads nothing without asking you first.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Get-Runtime.ps1
    powershell -ExecutionPolicy Bypass -File Get-Runtime.ps1 -DownloadPlatformTools
#>
[CmdletBinding()]
param(
    [switch]$DownloadPlatformTools,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

# Resolve the PortableDroid root whether this script sits in the root or in scripts\.
$here = $PSScriptRoot
$root = if (Test-Path (Join-Path $here 'config\settings.json')) { $here } else { Split-Path -Parent $here }

$runtime       = Join-Path $root 'runtime'
$qemuDir       = Join-Path $runtime 'qemu'
$toolsDir      = Join-Path $runtime 'platform-tools'
$androidDir    = Join-Path $runtime 'android'

New-Item -ItemType Directory -Force -Path $qemuDir, $toolsDir, $androidDir | Out-Null

function Write-Section($text) {
    if (-not $Quiet) {
        Write-Host ""
        Write-Host $text -ForegroundColor Cyan
        Write-Host ('-' * $text.Length) -ForegroundColor DarkGray
    }
}

function Test-Component($name, $path, $instructions) {
    $ok = Test-Path -LiteralPath $path
    $mark = if ($ok) { '[ok]     ' } else { '[missing]' }
    $colour = if ($ok) { 'Green' } else { 'Yellow' }
    Write-Host "$mark $name" -ForegroundColor $colour
    if (-not $ok -and -not $Quiet) {
        Write-Host "          expected at: $path" -ForegroundColor DarkGray
        foreach ($line in $instructions) { Write-Host "          $line" -ForegroundColor DarkGray }
        Write-Host ""
    }
    return $ok
}

Write-Section "PortableDroid runtime check"
Write-Host "Root: $root"

$qemuOk = Test-Component 'QEMU (qemu-system-x86_64.exe)' (Join-Path $qemuDir 'qemu-system-x86_64.exe') @(
    'Download the Windows 64-bit QEMU installer from https://qemu.weilnetz.de/w64/',
    'Install it anywhere, then copy the whole installation folder contents into:',
    "  $qemuDir",
    'You need at least qemu-system-x86_64.exe, qemu-img.exe and every DLL beside them.'
)

$qemuImgOk = Test-Component 'qemu-img.exe' (Join-Path $qemuDir 'qemu-img.exe') @(
    'This ships in the same QEMU installation as qemu-system-x86_64.exe.'
)

$adbOk = Test-Component 'Android platform-tools (adb.exe)' (Join-Path $toolsDir 'adb.exe') @(
    'Download from https://developer.android.com/studio/releases/platform-tools',
    "Extract the contents of the platform-tools folder into:",
    "  $toolsDir",
    'Or re-run this script with -DownloadPlatformTools to fetch it automatically.'
)

$imageOk = (Test-Path (Join-Path $androidDir 'system.img')) -or
           ((Get-ChildItem -Path $androidDir -Filter '*.iso' -ErrorAction SilentlyContinue).Count -gt 0)

$mark = if ($imageOk) { '[ok]     ' } else { '[missing]' }
$colour = if ($imageOk) { 'Green' } else { 'Yellow' }
Write-Host "$mark Android system image" -ForegroundColor $colour
if (-not $imageOk -and -not $Quiet) {
    Write-Host "          expected at: $androidDir\<something>.iso" -ForegroundColor DarkGray
    Write-Host "          Download a Vanilla or FOSS x86_64 build of Bliss OS or Android-x86:" -ForegroundColor DarkGray
    Write-Host "            https://blissos.org/  (choose a Vanilla or FOSS x86_64 build)" -ForegroundColor DarkGray
    Write-Host "            https://www.android-x86.org/" -ForegroundColor DarkGray
    Write-Host "          Put the .iso in that folder. On first start PortableDroid boots the" -ForegroundColor DarkGray
    Write-Host "          installer so you can install Android onto its own persistent disk." -ForegroundColor DarkGray
    Write-Host "          Avoid GApps builds: they bundle proprietary Google components." -ForegroundColor DarkGray
    Write-Host ""
}

if ($DownloadPlatformTools -and -not $adbOk) {
    Write-Section "Downloading Android platform-tools"
    Write-Host "Source: https://dl.google.com/android/repository/platform-tools-latest-windows.zip"
    Write-Host "These are Google's tools, covered by the Android SDK Terms of Service."
    $answer = Read-Host "Download now? (y/N)"
    if ($answer -eq 'y') {
        $zip = Join-Path $env:TEMP "platform-tools-$(Get-Random).zip"
        try {
            Invoke-WebRequest -Uri 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip' `
                              -OutFile $zip -UseBasicParsing
            $extract = Join-Path $env:TEMP "pt-$(Get-Random)"
            Expand-Archive -Path $zip -DestinationPath $extract -Force
            Copy-Item -Path (Join-Path $extract 'platform-tools\*') -Destination $toolsDir -Recurse -Force
            Remove-Item $zip, $extract -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "[ok]      platform-tools installed." -ForegroundColor Green
            $adbOk = Test-Path (Join-Path $toolsDir 'adb.exe')
        }
        catch {
            Write-Host "Download failed: $_" -ForegroundColor Red
            Write-Host "Please download it manually instead." -ForegroundColor Yellow
        }
    }
}

Write-Section "Virtualization check"
$hyperv = (Get-CimInstance Win32_ComputerSystem).HypervisorPresent
$virt = (Get-CimInstance Win32_Processor | Select-Object -First 1).VirtualizationFirmwareEnabled

if ($hyperv -or $virt) {
    Write-Host "[ok]      Hardware virtualization is available." -ForegroundColor Green
} else {
    Write-Host "[warning] Hardware virtualization was not detected." -ForegroundColor Yellow
    Write-Host "          Android will run, but very slowly." -ForegroundColor DarkGray
    Write-Host "          1. Reboot into BIOS/UEFI and enable Intel VT-x or AMD-V (SVM)." -ForegroundColor DarkGray
    Write-Host "          2. In Windows, enable 'Windows Hypervisor Platform' in" -ForegroundColor DarkGray
    Write-Host "             'Turn Windows features on or off', then restart." -ForegroundColor DarkGray
}

Write-Section "Summary"
if ($qemuOk -and $qemuImgOk -and $adbOk -and $imageOk) {
    Write-Host "Everything is in place. Start PortableDroid.exe and press 'Start Android'." -ForegroundColor Green
    exit 0
} else {
    Write-Host "Some components are still missing (see above)." -ForegroundColor Yellow
    Write-Host "PortableDroid will start and show you the same list, but it cannot boot" -ForegroundColor Yellow
    Write-Host "Android until they are present." -ForegroundColor Yellow
    exit 1
}
