[CmdletBinding()]
param(
    [string] $IdentityName = 'KeilerHirsch.WOLPERTINGER.R0.Local',
    [string] $Publisher = 'CN=KeilerHirsch WOLPERTINGER R0 Local',
    [string] $Version = '0.1.0.0',
    [switch] $SkipKernelBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactRoot = Join-Path $repoRoot 'artifacts\r0-package'
$publishRoot = Join-Path $artifactRoot 'publish'
$stageRoot = Join-Path $artifactRoot 'layout'
$packagePath = Join-Path $artifactRoot 'WOLPERTINGER-R0-local-x64.msix'
$manifestSource = Join-Path $repoRoot 'packaging\Wolpertinger.Package\Package.appxmanifest'
$assetsSource = Join-Path $repoRoot 'packaging\Wolpertinger.Package\Assets'
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'

function Remove-OwnedDirectory([string] $Path, [string] $OwnedRoot) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $resolvedRoot = [IO.Path]::GetFullPath($OwnedRoot).TrimEnd('\')
    $resolvedPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $resolvedPath.StartsWith($resolvedRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove path outside package artifact root: $resolvedPath"
    }

    Remove-Item -LiteralPath $resolvedPath -Recurse -Force
}

function Find-WindowsSdkTool([string] $ToolName) {
    $kitsRoot = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits\10\bin'
    $pattern = Join-Path $kitsRoot ("10.0.*\x64\" + $ToolName)
    $candidates = @(Get-ChildItem -Path $pattern -File -ErrorAction SilentlyContinue)
    if ($candidates.Count -eq 0) {
        throw "Windows SDK tool not found: $ToolName"
    }

    return $candidates |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending |
        Select-Object -First 1
}

function Copy-PublishTree([string] $Source, [string] $Destination) {
    $sourceRoot = [IO.Path]::GetFullPath($Source).TrimEnd('\')
    Get-ChildItem -LiteralPath $Source -File -Recurse |
        Where-Object Extension -ne '.pdb' |
        ForEach-Object {
            $relative = $_.FullName.Substring($sourceRoot.Length + 1)
            $target = Join-Path $Destination $relative
            $targetDirectory = Split-Path -Parent $target
            New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null

            if (Test-Path -LiteralPath $target) {
                $sourceHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
                if ($sourceHash -ne $targetHash) {
                    throw "Conflicting publish outputs share package path '$relative'."
                }
                return
            }

            Copy-Item -LiteralPath $_.FullName -Destination $target
        }
}

function Write-StagedManifest(
    [string] $Source,
    [string] $Destination,
    [string] $Name,
    [string] $PublisherValue,
    [string] $PackageVersion) {

    [xml] $manifest = Get-Content -LiteralPath $Source -Raw
    $identity = $manifest.Package.Identity
    if ($null -eq $identity) {
        throw 'Package manifest has no Identity element.'
    }

    $identity.Name = $Name
    $identity.Publisher = $PublisherValue
    $identity.Version = $PackageVersion

    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $writer = [Xml.XmlWriter]::Create($Destination, $settings)
    try {
        $manifest.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $dotnet)) {
    throw "Repository-pinned dotnet host not found: $dotnet"
}

$requiredSdk = (Get-Content (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$actualSdk = (& $dotnet --version).Trim()
if ($actualSdk -ne $requiredSdk) {
    throw "Pinned SDK mismatch. Required $requiredSdk, observed $actualSdk."
}

$sourceRevision = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceRevision)) {
    throw 'Unable to resolve package source revision.'
}
if (@(& git -C $repoRoot status --porcelain).Count -ne 0) {
    throw 'Package build requires a clean source worktree.'
}

if (-not (Test-Path -LiteralPath $manifestSource)) {
    throw "Package manifest missing: $manifestSource"
}
if (-not (Test-Path -LiteralPath $assetsSource)) {
    throw "Package assets missing: $assetsSource"
}

$parsedVersion = $null
if (-not [version]::TryParse($Version, [ref]$parsedVersion)) {
    throw "Package version is invalid: $Version"
}

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
Remove-OwnedDirectory $publishRoot $artifactRoot
Remove-OwnedDirectory $stageRoot $artifactRoot
New-Item -ItemType Directory -Path $publishRoot,$stageRoot -Force | Out-Null

$appHostArgs = @(
    'publish',
    (Join-Path $repoRoot 'src\Wolpertinger.AppHost\Wolpertinger.AppHost.csproj'),
    '-c','Release',
    '-r','win-x64',
    '--self-contained','true',
    '-o',(Join-Path $publishRoot 'apphost'),
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    "-p:SourceRevisionId=$sourceRevision",
    '-p:ContinuousIntegrationBuild=true'
)
& $dotnet @appHostArgs
if ($LASTEXITCODE -ne 0) {
    throw "AppHost self-contained publish failed with exit code $LASTEXITCODE."
}

$presentationArgs = @(
    'publish',
    (Join-Path $repoRoot 'src\Wolpertinger.Presentation.App\Wolpertinger.Presentation.App.csproj'),
    '-c','Release',
    '-r','win-x64',
    '--self-contained','true',
    '-o',(Join-Path $publishRoot 'presentation'),
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    "-p:SourceRevisionId=$sourceRevision",
    '-p:ContinuousIntegrationBuild=true'
)
& $dotnet @presentationArgs
if ($LASTEXITCODE -ne 0) {
    throw "Presentation self-contained publish failed with exit code $LASTEXITCODE."
}

if (-not $SkipKernelBuild) {
    $alr = 'C:\Program Files\Alire\bin\alr.exe'
    if (-not (Test-Path -LiteralPath $alr)) {
        throw "Alire not found at the approved local path: $alr"
    }

    Push-Location (Join-Path $repoRoot 'kernel')
    try {
        & $alr build --validation
        if ($LASTEXITCODE -ne 0) {
            throw "Ada kernel build failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

$kernel = Join-Path $repoRoot 'kernel\bin\wolpertinger_kernel_main.exe'
if (-not (Test-Path -LiteralPath $kernel)) {
    throw "Trusted kernel executable missing: $kernel"
}

Copy-PublishTree (Join-Path $publishRoot 'apphost') $stageRoot
Copy-PublishTree (Join-Path $publishRoot 'presentation') $stageRoot
Copy-Item -LiteralPath $kernel -Destination (Join-Path $stageRoot 'wolpertinger_kernel.exe')

$stageAssets = Join-Path $stageRoot 'Assets'
New-Item -ItemType Directory -Path $stageAssets -Force | Out-Null
Get-ChildItem -LiteralPath $assetsSource -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $stageAssets
}

Write-StagedManifest $manifestSource (Join-Path $stageRoot 'AppxManifest.xml') $IdentityName $Publisher $Version

$makeAppx = Find-WindowsSdkTool 'MakeAppx.exe'
if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Force
}

& $makeAppx.FullName pack /d $stageRoot /p $packagePath /o
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx failed with exit code $LASTEXITCODE."
}

& (Join-Path $PSScriptRoot 'Verify-R0Package.ps1') -PackagePath $packagePath
if ($LASTEXITCODE -ne 0) {
    throw "R0 package verification failed with exit code $LASTEXITCODE."
}

$vsRoot = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools'
$desktopBridgeTargets = Join-Path $vsRoot 'MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets'
Write-Output "PackagePath=$packagePath"
Write-Output "PackageSHA256=$((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash)"
Write-Output "SourceRevision=$sourceRevision"
Write-Output "IdentityName=$IdentityName"
Write-Output "Publisher=$Publisher"
Write-Output "Signing=UNSIGNED_LOCAL_GATE"
Write-Output "DesktopBridgeTargetsPresent=$(Test-Path -LiteralPath $desktopBridgeTargets)"
