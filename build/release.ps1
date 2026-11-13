# Builds a PuterScanny release: runs the tests, publishes a self-contained Windows x64 build, and makes
#   dist\PuterScanny-<version>-setup.exe          (installer, needs Inno Setup 6)
#   dist\PuterScanny-<version>-win-x64-portable.zip (no install needed)
#   dist\SHA256SUMS.txt
# Usage (from the repository root, in Windows PowerShell or PowerShell 7):
#   powershell -ExecutionPolicy Bypass -File build\release.ps1 [-SkipTests]
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
Set-Location $root

[xml]$props = Get-Content "$root\Directory.Build.props"
$version = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Version not found in Directory.Build.props' }
Write-Host "PuterScanny $version" -ForegroundColor Cyan

function Run($exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE" }
}

if (-not $SkipTests) {
    Write-Host 'Running tests...' -ForegroundColor Cyan
    Run dotnet @('test', 'PuterScanny.slnx', '-c', 'Release', '--nologo')
}

$publish = "$root\artifacts\publish"
$dist = "$root\dist"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
New-Item -ItemType Directory -Force $dist | Out-Null

Write-Host 'Publishing (self-contained, win-x64)...' -ForegroundColor Cyan
Run dotnet @('publish', 'src\PuterScanny.App\PuterScanny.App.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $publish, '--nologo')

# Sanity checks: the things a release can't work without.
foreach ($required in 'PuterScanny.exe', 'knowledge\file-types.json', 'knowledge\locations.json', 'knowledge\signatures.json',
                      'e_sqlite3.dll', 'runtimes\win-x64\native\avx2\llama.dll', 'runtimes\win-x64\native\noavx\llama.dll') {
    if (-not (Test-Path "$publish\$required")) { throw "Missing from the build: $required" }
}
$foreign = Get-ChildItem "$publish\runtimes" -Directory | Where-Object Name -ne 'win-x64'
if ($foreign) { throw "Unexpected native files for other platforms: $($foreign.Name -join ', ')" }

Write-Host 'Making the portable zip...' -ForegroundColor Cyan
$zip = "$dist\PuterScanny-$version-win-x64-portable.zip"
$staging = "$root\artifacts\portable\PuterScanny"
if (Test-Path "$root\artifacts\portable") { Remove-Item -Recurse -Force "$root\artifacts\portable" }
New-Item -ItemType Directory -Force $staging | Out-Null
Copy-Item "$publish\*" $staging -Recurse
Copy-Item "$root\LICENSE" "$staging\LICENSE.txt"
Copy-Item "$root\THIRD-PARTY-NOTICES.md" $staging
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $staging -DestinationPath $zip -CompressionLevel Optimal

Write-Host 'Building the installer...' -ForegroundColor Cyan
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with:  winget install JRSoftware.InnoSetup' }
Run $iscc @("/DAppVersion=$version", '/Q', "$root\installer\PuterScanny.iss")
$setup = "$dist\PuterScanny-$version-setup.exe"

$sums = foreach ($f in $setup, $zip) { "{0}  {1}" -f (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $f -Leaf) }
# Unix line endings, so "sha256sum -c SHA256SUMS.txt" works as well as Get-FileHash.
[IO.File]::WriteAllText("$dist\SHA256SUMS.txt", (($sums -join "`n") + "`n"), [Text.Encoding]::ASCII)

Write-Host ''
Write-Host 'Done:' -ForegroundColor Green
foreach ($f in $setup, $zip) { '{0}  ({1:N1} MB)' -f $f, ((Get-Item $f).Length / 1MB) }
$sums
