param(
    [string]$Configuration = 'Release',
    [switch]$SkipSelfContained,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = [IO.Path]::Combine($root, 'dist')
$version = '0.3.2'

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist -Force | Out-Null

function Publish-Project([string]$project, [string]$outDir, [bool]$selfContained, [bool]$singleFile) {
    if ([string]::IsNullOrWhiteSpace($outDir)) { throw "outDir is empty" }
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null

    $publishArgs = @(
        'publish', $project,
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained', $(if ($selfContained) { 'true' } else { 'false' }),
        ('-p:PublishSingleFile=' + $(if ($singleFile) { 'true' } else { 'false' })),
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-o', $outDir
    )
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $project" }
}

function Pack([string]$folder, [string]$zipName) {
    Copy-Item ([IO.Path]::Combine($root, 'README.md')) ([IO.Path]::Combine($folder, 'README.md')) -Force
    $zip = [IO.Path]::Combine($dist, $zipName)
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path ([IO.Path]::Combine($folder, '*')) -DestinationPath $zip -CompressionLevel Optimal
    "  -> $zip"
}

$cli = [IO.Path]::Combine($root, 'src', 'Setpiece', 'Setpiece.csproj')
$app = [IO.Path]::Combine($root, 'src', 'Setpiece.App', 'Setpiece.App.csproj')

# Portable: loose files + dotnet-host launchers.
$portable = [IO.Path]::Combine($dist, "setpiece-$version")
"publish portable (framework-dependent, loose) ..."
Publish-Project $cli $portable $false $false
Publish-Project $app $portable $false $false

@'
@echo off
cd /d "%~dp0"
dotnet "Setpiece.App.dll"
'@ | Set-Content ([IO.Path]::Combine($portable, 'run-app.cmd')) -Encoding ASCII

@'
@echo off
cd /d "%~dp0"
dotnet "setpiece.dll" %*
'@ | Set-Content ([IO.Path]::Combine($portable, 'run-setpiece.cmd')) -Encoding ASCII

Pack $portable "setpiece-$version-win-x64.zip"

if (-not $SkipSelfContained) {
    $sc = [IO.Path]::Combine($dist, "setpiece-$version-selfcontained")
    "publish self-contained (single-file) ..."
    Publish-Project $cli $sc $true $true
    Publish-Project $app $sc $true $true
    Pack $sc "setpiece-$version-selfcontained-win-x64.zip"

    if (-not $SkipInstaller) {
        "publish installer payload (self-contained, loose) ..."
        $payload = [IO.Path]::Combine($dist, 'payload-loose')
        Publish-Project $cli $payload $true $false
        Publish-Project $app $payload $true $false
        Copy-Item ([IO.Path]::Combine($root, 'README.md')) ([IO.Path]::Combine($payload, 'README.md')) -Force

        $iscc = @(
            [IO.Path]::Combine($root, 'tools', 'innosetup6', 'tools', 'ISCC.exe'),
            [IO.Path]::Combine($root, 'tools', 'innosetup', 'tools', 'ISCC.exe'),
            'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
            'C:\Program Files\Inno Setup 6\ISCC.exe'
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1

        if ($iscc) {
            "compile installer with $iscc ..."
            & $iscc ([IO.Path]::Combine($root, 'installer', 'setpiece.iss'))
            if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed" }
            "  -> " + ([IO.Path]::Combine($dist, "Setpiece-Setup-$version.exe"))
        }
        else {
            "WARN: Inno Setup (ISCC.exe) not found, skipping installer."
            "      Put it in tools\innosetup6\tools\ISCC.exe or install Inno Setup 6."
        }
    }
}

# Source archive (GitHub "source" release asset).
"pack source ..."
$stage = [IO.Path]::Combine($dist, 'source')
New-Item -ItemType Directory -Path $stage -Force | Out-Null
robocopy $root $stage /E /XD bin obj dist .vs .git fixtures tools /XF *.user | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed: $LASTEXITCODE" }
$sourceZip = [IO.Path]::Combine($dist, "setpiece-$version-source.zip")
if (Test-Path $sourceZip) { Remove-Item $sourceZip -Force }
Compress-Archive -Path ([IO.Path]::Combine($stage, '*')) -DestinationPath $sourceZip -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force
"  -> $sourceZip"

"done. output: $dist"

# Keep only release artifacts; remove intermediate folders.
foreach ($dir in @('payload-loose', "setpiece-$version", "setpiece-$version-selfcontained", 'setup', 'source')) {
    $path = [IO.Path]::Combine($dist, $dir)
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}

Get-ChildItem $dist | Select-Object Name, Length | Format-Table -AutoSize
