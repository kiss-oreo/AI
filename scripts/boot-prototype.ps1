<#
    PortableDroid — Milestone M1 prototype boot script.

    STATUS: UNTESTED. This script has never been executed; it encodes the QEMU
    argument list proposed in docs/02-architecture.md so it can be run and
    corrected on real Windows hardware. Treat every value here as a hypothesis.

    Prerequisites you must provide yourself (nothing is downloaded by this script):
      runtime\qemu\qemu-system-x86_64.exe   a Windows QEMU build with WHPX (+ virgl if you want GL)
      runtime\qemu\qemu-img.exe
      runtime\android\system.iso            a Vanilla/FOSS Android-x86_64 image (see docs/01)
      runtime\platform-tools\adb.exe

    Usage:
      powershell -ExecutionPolicy Bypass -File scripts\boot-prototype.ps1
      powershell ... -MemoryMB 1536 -Cores 2 -Graphics compatibility
#>
[CmdletBinding()]
param(
    [int]$MemoryMB = 2048,
    [int]$Cores = 2,
    [ValidateSet('auto','hardware','compatibility')][string]$Graphics = 'auto',
    [int]$AdbPort = 5556,
    [int]$QmpPort = 5557,
    [int]$UserdataGB = 16,
    [switch]$FirstBoot   # attach the installer ISO as boot device
)

$ErrorActionPreference = 'Stop'

# --- portable path resolution: everything is relative to the repo/payload root ---
$Root      = Split-Path -Parent $PSScriptRoot
$Payload   = Join-Path $Root 'payload'
$Qemu      = Join-Path $Payload 'runtime\qemu\qemu-system-x86_64.exe'
$QemuImg   = Join-Path $Payload 'runtime\qemu\qemu-img.exe'
$Iso       = Join-Path $Payload 'runtime\android\system.iso'
$Adb       = Join-Path $Payload 'runtime\platform-tools\adb.exe'
$Userdata  = Join-Path $Payload 'profiles\default\userdata\android.qcow2'
$LogDir    = Join-Path $Payload 'logs'

function Require-File($path, $hint) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing runtime component: $path`n  $hint"
    }
}

Require-File $Qemu    'Place a WHPX-enabled QEMU for Windows in payload\runtime\qemu\.'
Require-File $QemuImg 'qemu-img.exe ships with the same QEMU build.'
Require-File $Adb     'Google platform-tools, extracted to payload\runtime\platform-tools\.'
if ($FirstBoot) { Require-File $Iso 'Download a Vanilla/FOSS Android-x86_64 ISO yourself; see docs/01-runtime-comparison.md.' }

New-Item -ItemType Directory -Force -Path $LogDir, (Split-Path $Userdata) | Out-Null

# --- persistent userdata: created once, never recreated, never used with -snapshot ---
if (-not (Test-Path -LiteralPath $Userdata)) {
    Write-Host "Creating persistent userdata image ($UserdataGB GB, qcow2)..."
    & $QemuImg create -f qcow2 $Userdata "${UserdataGB}G" | Write-Host
} else {
    Write-Host "Reusing existing userdata image: $Userdata"
}

# --- accelerator ---
$accel = 'whpx,kernel-irqchip=off'
$cpu   = 'host'
$whpx  = (Get-CimInstance Win32_ComputerSystem).HypervisorPresent
if (-not $whpx) {
    Write-Warning @'
WHPX / hypervisor not detected.
  What happened : hardware acceleration is unavailable.
  Why           : "Windows Hypervisor Platform" is off, or VT-x/AMD-V is disabled in BIOS.
  What to try   : enable virtualization in BIOS, then Windows Features ->
                  "Windows Hypervisor Platform". Falling back to TCG (slow).
'@
    $accel = 'tcg,thread=multi'
    $cpu   = 'qemu64'
}

# --- graphics ---
switch ($Graphics) {
    'hardware'      { $gpu = @('-device','virtio-gpu-gl-pci'); $disp = @('-display','sdl,gl=on') }
    'compatibility' { $gpu = @('-device','virtio-vga');        $disp = @('-display','sdl,gl=off') }
    default         { $gpu = @('-device','virtio-gpu-gl-pci'); $disp = @('-display','sdl,gl=on') }
}

$qemuArgs = @(
    '-name','PortableDroid'
    '-accel',$accel
    '-cpu',$cpu
    '-smp',"$Cores"
    '-m',"$MemoryMB"
    '-drive',"file=$Userdata,if=virtio,format=qcow2,cache=writeback,discard=unmap"
    '-netdev',"user,id=n0,hostfwd=tcp:127.0.0.1:$AdbPort-:5555"
    '-device','virtio-net-pci,netdev=n0'
    '-device','virtio-tablet-pci'
    '-device','virtio-keyboard-pci'
    '-qmp',"tcp:127.0.0.1:$QmpPort,server=on,wait=off"
    '-rtc','base=localtime'
    '-no-reboot'
) + $gpu + $disp

if ($FirstBoot) { $qemuArgs += @('-cdrom',$Iso,'-boot','d') }

# NOTE: -snapshot must NEVER appear above. It would discard all Android data.
if ($qemuArgs -contains '-snapshot') { throw 'Refusing to boot: -snapshot would destroy persistence.' }

$argLine = ($qemuArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
Write-Host "`n$Qemu $argLine`n"

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $Qemu -ArgumentList $qemuArgs -PassThru `
        -RedirectStandardError (Join-Path $LogDir "runtime-$stamp.log")

Write-Host "QEMU pid $($p.Id). Waiting for ADB on 127.0.0.1:$AdbPort ..."
& $Adb connect "127.0.0.1:$AdbPort" | Out-Null

$deadline = (Get-Date).AddMinutes(4)
$booted = $false
while ((Get-Date) -lt $deadline -and -not $p.HasExited) {
    Start-Sleep -Seconds 3
    & $Adb connect "127.0.0.1:$AdbPort" *> $null
    $sysBoot = (& $Adb -s "127.0.0.1:$AdbPort" shell getprop sys.boot_completed 2>$null).Trim()
    if ($sysBoot -eq '1') { $booted = $true; break }
}
$sw.Stop()

if ($booted) {
    $rel = (& $Adb -s "127.0.0.1:$AdbPort" shell getprop ro.build.version.release).Trim()
    $abi = (& $Adb -s "127.0.0.1:$AdbPort" shell getprop ro.product.cpu.abilist).Trim()
    Write-Host "`nM1 PASS"
    Write-Host ("  boot time      : {0:n1} s" -f $sw.Elapsed.TotalSeconds)
    Write-Host "  android version: $rel"
    Write-Host "  abis           : $abi"
    Write-Host "  host RSS (QEMU): $([math]::Round((Get-Process -Id $p.Id).WorkingSet64/1MB)) MB"
    Write-Host "`nRecord these numbers in docs/benchmarks.md."
} else {
    Write-Host "`nM1 FAIL - guest did not report sys.boot_completed."
    Write-Host "  Check logs\runtime-$stamp.log. Common causes: no WHPX, GL device"
    Write-Host "  unsupported (retry with -Graphics compatibility), or empty userdata"
    Write-Host "  image with no OS installed (retry with -FirstBoot)."
}
