# DualSense Studio one-click setup for a fresh PC.
# Installs the signed drivers it needs, installs the app, names the audio devices
# "DualSense Wireless Controller", hides the real controller from games, and adds
# shortcuts. Run Setup.cmd (it asks for administrator approval once).
#Requires -RunAsAdministrator
# -DriversOnly -AppDir <dir>: used by DualSenseStudio-Setup.exe, which installs the app files itself.
param([switch]$DriversOnly, [string]$AppDir)
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$appDir = if ($AppDir) { $AppDir } else { Join-Path $env:LOCALAPPDATA 'Programs\DualSenseBtAudio' }
$tmp = Join-Path $env:TEMP 'dualsense-studio-setup'
New-Item -ItemType Directory -Force $tmp | Out-Null

function Get-Signed($url, $file, $publisher) {
    $path = Join-Path $tmp $file
    Write-Host "Downloading $file..."
    Invoke-WebRequest $url -OutFile $path -UseBasicParsing
    $sig = Get-AuthenticodeSignature $path
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch [regex]::Escape($publisher)) {
        throw "$file is not signed by $publisher - stopping."
    }
    return $path
}

function Test-Endpoint($pattern) {
    [bool](Get-PnpDevice -Class AudioEndpoint -ErrorAction SilentlyContinue | Where-Object FriendlyName -match $pattern)
}

# 1. VB-CABLE (headphone audio path)
if (-not (Test-Endpoint 'VB-Audio Virtual Cable')) {
    $zip = Join-Path $tmp 'vbcable.zip'
    Invoke-WebRequest 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip' -OutFile $zip -UseBasicParsing
    Expand-Archive $zip (Join-Path $tmp 'vbcable') -Force
    $exe = Join-Path $tmp 'vbcable\VBCABLE_Setup_x64.exe'
    if ((Get-AuthenticodeSignature $exe).Status -ne 'Valid') { throw 'VB-CABLE installer signature invalid.' }
    Write-Host 'Installing VB-CABLE (click "Install Driver")...'
    Start-Process $exe -Wait
}

# 2. VB-Audio Hi-Fi Cable (microphone path)
if (-not (Test-Endpoint 'Hi-Fi Cable|DualSense mic link')) {
    $zip = Join-Path $tmp 'hifi.zip'
    Invoke-WebRequest 'https://download.vb-audio.com/Download_CABLE/HiFiCableAsioBridgeSetup_v1007.zip' -OutFile $zip -UseBasicParsing
    Expand-Archive $zip (Join-Path $tmp 'hifi') -Force
    $exe = Join-Path $tmp 'hifi\HiFiCableAsioBridgeSetup.exe'
    if ((Get-AuthenticodeSignature $exe).Status -ne 'Valid') { throw 'Hi-Fi Cable installer signature invalid.' }
    Write-Host 'Installing Hi-Fi Cable (click "Install")...'
    Start-Process $exe -Wait
}

# 3. ViGEmBus (virtual Xbox / PlayStation controllers)
if (-not (Get-PnpDevice -FriendlyName 'Nefarius Virtual Gamepad Emulation Bus' -ErrorAction SilentlyContinue)) {
    $exe = Get-Signed 'https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe' 'vigembus.exe' 'Nefarius'
    Write-Host 'Installing ViGEmBus...'
    Start-Process $exe -Wait
}

# 4. HidHide (hide the real DualSense from games to avoid double input)
$hidHideCli = 'C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe'
if (-not (Test-Path $hidHideCli)) {
    $exe = Get-Signed 'https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe' 'hidhide.exe' 'Nefarius'
    Write-Host 'Installing HidHide (choose "restart later" if asked)...'
    Start-Process $exe -Wait
}

# 5. Install the app (Setup.exe already copied the files)
if (-not $DriversOnly) {
    Write-Host 'Installing DualSense Studio...'
    Get-Process DualSenseAudioStudio, DualSenseBtAudio -ErrorAction SilentlyContinue | Stop-Process -Force
    New-Item -ItemType Directory -Force $appDir | Out-Null
    Copy-Item (Join-Path $here 'app\*') $appDir -Recurse -Force
}

# 6. Name the audio devices like a real headset and hide unused cable endpoints
& (Join-Path $appDir 'DualSenseBtAudio.exe') --setup-devices

# 7. Hide every paired DualSense from games; only DualSense Studio may see it
if (Test-Path $hidHideCli) {
    & $hidHideCli --app-reg (Join-Path $appDir 'DualSenseAudioStudio.exe') | Out-Null
    & $hidHideCli --app-reg (Join-Path $appDir 'DualSenseBtAudio.exe') | Out-Null
    Get-PnpDevice | Where-Object InstanceId -match '(BTHENUM|HID|USB)\\.*VID(&0002)?_?054C.*PID_0CE6' |
        ForEach-Object { & $hidHideCli --dev-hide $_.InstanceId | Out-Null }
    & $hidHideCli --cloak-on | Out-Null
}

if ($DriversOnly) {
    Write-Host 'Drivers and devices are ready.' -ForegroundColor Green
    return
}

# 8. Shortcuts: desktop + start with Windows (tray)
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'DualSense Studio.lnk'))
$lnk.TargetPath = Join-Path $appDir 'DualSenseAudioStudio.exe'
$lnk.WorkingDirectory = $appDir
$lnk.Save()
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name DualSenseStudio -Value "`"$(Join-Path $appDir 'DualSenseAudioStudio.exe')`" --tray"

Write-Host ''
Write-Host 'DualSense Studio is ready. Pair your DualSense over Bluetooth (hold PS + Create), then open DualSense Studio.' -ForegroundColor Green
Start-Process (Join-Path $appDir 'DualSenseAudioStudio.exe') -WorkingDirectory $appDir
