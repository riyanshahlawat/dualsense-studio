param(
    [Parameter(Mandatory = $true)]
    [string]$Destination,

    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x64", "win-x86")]
    [string]$RuntimeIdentifier
)

$ErrorActionPreference = "Stop"
$Destination = [System.IO.Path]::GetFullPath($Destination)

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

# First check whether dotnet publish already copied it.
$alreadyThere = Join-Path $Destination "hidapi.dll"
if (Test-Path $alreadyThere) {
    Write-Host "hidapi.dll is already in the publication folder:"
    Write-Host $alreadyThere
    exit 0
}

$nugetRoot = if ($env:NUGET_PACKAGES) {
    $env:NUGET_PACKAGES
} else {
    Join-Path $env:USERPROFILE ".nuget\packages"
}

$packageRoots = @(
    (Join-Path $nugetRoot "hidapi-vc.redist\0.15.0"),
    (Join-Path $nugetRoot "hidapi-vc\0.15.0")
) | Where-Object { Test-Path $_ }

if ($packageRoots.Count -eq 0) {
    throw "The hidapi-VC.Redist 0.15.0 package was not found in the NuGet cache: $nugetRoot"
}

$candidates = foreach ($root in $packageRoots) {
    Get-ChildItem -Path $root -Recurse -File -Filter "hidapi.dll" -ErrorAction SilentlyContinue
}

if (-not $candidates) {
    throw "NuGet restored the package, but no hidapi.dll file was found inside it."
}

$architecturePattern = if ($RuntimeIdentifier -eq "win-x86") {
    "(?i)(x86|win32)"
} else {
    "(?i)(x64|amd64|win64)"
}

$selected = $candidates |
    Where-Object { $_.FullName -match $architecturePattern } |
    Select-Object -First 1

if (-not $selected) {
    $all = ($candidates | ForEach-Object FullName) -join [Environment]::NewLine
    throw "No hidapi.dll compatible with $RuntimeIdentifier was found:`n$all"
}

Copy-Item -LiteralPath $selected.FullName -Destination $alreadyThere -Force

if (-not (Test-Path $alreadyThere)) {
    throw "Copying hidapi.dll failed."
}

Write-Host "Native DLL copied:"
Write-Host "Architecture: $RuntimeIdentifier"
Write-Host "Source:       $($selected.FullName)"
Write-Host "Destination:  $alreadyThere"
