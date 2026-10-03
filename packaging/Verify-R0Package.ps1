[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,

    [string] $ExpectedIdentityName,

    [string] $ExpectedPublisher,

    [switch] $RequireSignature
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactRoot = Join-Path $repoRoot 'artifacts\r0-package'
$verifyRoot = Join-Path $artifactRoot 'verify-layout'
$inventoryPath = Join-Path $artifactRoot 'package-inventory.json'

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

function Require-SingleNode(
    [xml] $Document,
    [Xml.XmlNamespaceManager] $Namespaces,
    [string] $XPath,
    [string] $Description) {

    $nodes = $Document.SelectNodes($XPath, $Namespaces)
    if ($null -eq $nodes -or $nodes.Count -ne 1) {
        throw "Expected exactly one $Description; observed $($nodes.Count)."
    }

    return $nodes.Item(0)
}

$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
if (-not (Test-Path -LiteralPath $artifactRoot)) {
    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
}
if (Test-Path -LiteralPath $verifyRoot) {
    $resolvedVerify = [IO.Path]::GetFullPath($verifyRoot)
    $resolvedArtifact = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\')
    if (-not $resolvedVerify.StartsWith($resolvedArtifact + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove verification path outside artifact root: $resolvedVerify"
    }
    Remove-Item -LiteralPath $verifyRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $verifyRoot -Force | Out-Null

$makeAppx = Find-WindowsSdkTool 'MakeAppx.exe'
& $makeAppx.FullName unpack /p $resolvedPackage /d $verifyRoot /o
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx unpack failed with exit code $LASTEXITCODE."
}

$manifestPath = Join-Path $verifyRoot 'AppxManifest.xml'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw 'Unpacked package has no AppxManifest.xml.'
}

[xml] $manifest = Get-Content -LiteralPath $manifestPath -Raw
$ns = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
$ns.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$ns.AddNamespace('uap3', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/3')
$ns.AddNamespace('uap10', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10')
$ns.AddNamespace('desktop', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10')
$ns.AddNamespace('rescap', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')

$identity = Require-SingleNode $manifest $ns '/f:Package/f:Identity' 'package Identity'
$identityName = $identity.GetAttribute('Name')
$publisher = $identity.GetAttribute('Publisher')
if ($identity.GetAttribute('ProcessorArchitecture') -ne 'x64') {
    throw 'Package ProcessorArchitecture is not x64.'
}
if ($ExpectedIdentityName -and $identityName -ne $ExpectedIdentityName) {
    throw "Package identity mismatch. Expected '$ExpectedIdentityName', observed '$identityName'."
}
if ($ExpectedPublisher -and $publisher -ne $ExpectedPublisher) {
    throw "Package publisher mismatch. Expected '$ExpectedPublisher', observed '$publisher'."
}

$application = Require-SingleNode $manifest $ns '/f:Package/f:Applications/f:Application' 'Application'
if ($application.GetAttribute('Executable') -ne 'Wolpertinger.AppHost.exe') {
    throw 'Package entry executable is not Wolpertinger.AppHost.exe.'
}
if ($application.GetAttribute('EntryPoint') -ne 'Windows.FullTrustApplication') {
    throw 'Package entry point is not Windows.FullTrustApplication.'
}
if ($application.GetAttribute('RuntimeBehavior', $ns.LookupNamespace('uap10')) -ne 'packagedClassicApp') {
    throw 'Package runtime behavior is not packagedClassicApp.'
}
if ($application.GetAttribute('TrustLevel', $ns.LookupNamespace('uap10')) -ne 'mediumIL') {
    throw 'Package trust level is not mediumIL.'
}

$protocol = Require-SingleNode $manifest $ns "/f:Package/f:Applications/f:Application/f:Extensions/uap3:Extension[@Category='windows.protocol']/uap3:Protocol" 'wolpertinger protocol'
if ($protocol.GetAttribute('Name') -ne 'wolpertinger') {
    throw 'Package protocol scheme is not wolpertinger.'
}
if ($protocol.GetAttribute('Parameters') -ne '--protocol "%1"') {
    throw 'Package protocol parameters do not match the bounded AppHost activation shape.'
}

$startup = Require-SingleNode $manifest $ns "/f:Package/f:Applications/f:Application/f:Extensions/desktop:Extension[@Category='windows.startupTask']/desktop:StartupTask" 'startup task'
if ($startup.GetAttribute('TaskId') -ne 'WolpertingerStartup') {
    throw 'Package startup task id is not WolpertingerStartup.'
}
if ($startup.GetAttribute('Enabled') -ne 'false') {
    throw 'Package startup task must be disabled by default.'
}

if ($null -eq $manifest.SelectSingleNode("/f:Package/f:Capabilities/f:Capability[@Name='internetClient']", $ns)) {
    throw 'Package is missing internetClient capability.'
}
if ($null -eq $manifest.SelectSingleNode("/f:Package/f:Capabilities/rescap:Capability[@Name='runFullTrust']", $ns)) {
    throw 'Package is missing runFullTrust capability.'
}

$requiredFiles = @(
    'Wolpertinger.AppHost.exe',
    'Wolpertinger.Presentation.App.exe',
    'wolpertinger_kernel.exe',
    'hostfxr.dll',
    'hostpolicy.dll',
    'coreclr.dll',
    'System.Private.CoreLib.dll',
    'fixtures\r0\sample-flow.jsonl',
    'Assets\StoreLogo.png',
    'Assets\Square150x150Logo.png',
    'Assets\Square44x44Logo.png',
    'Assets\ProtocolLogo.png'
)
foreach ($relative in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $verifyRoot $relative))) {
        throw "Required package payload is missing: $relative"
    }
}

$files = @(Get-ChildItem -LiteralPath $verifyRoot -File -Recurse)
$verifyPrefix = [IO.Path]::GetFullPath($verifyRoot).TrimEnd('\')
$forbidden = @(
    '(^|/)Wolpertinger\.Host\.exe$',
    '\.Tests\.dll$',
    '\.pdb$',
    '\.(pfx|p12|pem|key)$',
    'frontier-token',
    'evidence-key',
    'frontier-oauth\.json$',
    '(^|/)(src|tests|\.git)(/|$)',
    '(^|/)kernel/(src|proof|tests|alire)(/|$)',
    '\.(cs|adb|ads|gpr|ps1)$'
)
foreach ($file in $files) {
    $relative = $file.FullName.Substring($verifyPrefix.Length + 1).Replace('\', '/')
    foreach ($pattern in $forbidden) {
        if ($relative -match $pattern) {
            throw "Forbidden package payload detected: $relative"
        }
    }
}

$inventory = $files |
    Sort-Object FullName |
    ForEach-Object {
        [PSCustomObject]@{
            path = $_.FullName.Substring($verifyPrefix.Length + 1).Replace('\', '/')
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }
$inventory | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $inventoryPath -Encoding utf8

$signature = Get-AuthenticodeSignature -FilePath $resolvedPackage
if ($RequireSignature -and $signature.Status -ne 'Valid') {
    throw "Package signature is not valid: $($signature.Status)"
}

$identityMode = if ($identityName -eq 'KeilerHirsch.WOLPERTINGER.R0.Local') {
    'LOCAL_DEVELOPMENT'
}
else {
    'EXTERNAL_IDENTITY'
}

Write-Output "Verification=PASS"
Write-Output "PackageSHA256=$((Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256).Hash)"
Write-Output "IdentityName=$identityName"
Write-Output "Publisher=$publisher"
Write-Output "IdentityMode=$identityMode"
Write-Output "SignatureStatus=$($signature.Status)"
Write-Output "PayloadFiles=$($files.Count)"
Write-Output "InventoryPath=$inventoryPath"
