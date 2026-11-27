$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$appx = "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe"
$scratch = Join-Path $env:RUNNER_TEMP arn94-release
$out = Join-Path $scratch output
$layout = Join-Path $scratch layout
$expanded = Join-Path $scratch bundle

$local = [Environment]::GetFolderPath('LocalApplicationData')
$name = 'Screeny.Verification.ARN94'
$real = 'ArnoGevorkyan.ScreenyScreenTime'
$product = (Resolve-Path product).Path

function Registered([string]$n) {
    $command = '$ErrorActionPreference = "Stop"; (Get-AppxPackage -Name "{0}").PackageFamilyName' -f $n
    & $appx -NoProfile -NonInteractive -Command $command
    if ($LASTEXITCODE -ne 0) { throw 'Package state query failed' }
}

if ((Registered $real) -or (Registered $name) -or (Get-Process Screeny -ErrorAction SilentlyContinue) -or
    (Test-Path (Join-Path $local ScreenTimeTracker)) -or (Test-Path (Join-Path $local Screeny))) {
    throw 'Not a fresh disposable runner profile. No app data opened.'
}
New-Item -ItemType Directory -Path $out | Out-Null
dotnet build "$product/ScreenTimeTracker.csproj" -c Release -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false -p:GenerateTemporaryStoreCertificate=false -p:AppxAutoIncrementPackageRevision=false -p:AppxSymbolPackageEnabled=false "-p:AppxPackageDir=$out\"
if ($LASTEXITCODE -ne 0) { throw 'Release package build failed' }
$artifacts = @(Get-ChildItem -LiteralPath $out -Recurse -File)
"Release output names: $($artifacts.Name -join ', ')"
$bundles = @($artifacts | Where-Object Extension -in '.appxbundle', '.msixbundle')
if ($bundles.Count -gt 1) { throw 'Unexpected multiple bundles' }
$sdk = "${env:ProgramFiles(x86)}/Windows Kits/10/bin"
$packer = Get-ChildItem $sdk -Directory | Where-Object Name -match '^\d+(\.\d+){3}$' | Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName x64/makeappx.exe } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (!$packer) { throw 'x64 MakeAppx unavailable' }
$bundlePath = '-'
$files = $artifacts
if ($bundles.Count -eq 1) {
    $bundlePath = $bundles[0].FullName
    & $packer unbundle /p $bundlePath /d $expanded /o | Select-Object -Last 4
    $files = Get-ChildItem -LiteralPath $expanded -Recurse -File
}
$packages = @($files | Where-Object Extension -in '.appx', '.msix')
if ($packages.Count -ne 1) { throw "Expected one application package; found $($packages.Count)" }
& $packer unpack /p $packages[0].FullName /d $layout /o | Select-Object -Last 4
$manifestPath = Join-Path $layout AppxManifest.xml
[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
$manifest.Package.Identity.Name = $name
$manifest.Package.Identity.Publisher = 'CN=Screeny Verification ARN94, OID.2.25.311729368913984317654407730594956997722=1'
$manifest.Package.Properties.PublisherDisplayName = 'Screeny Verification'
$manifest.Save($manifestPath)
foreach ($metadata in @('AppxBlockMap.xml', '[Content_Types].xml', 'AppxSignature.p7x')) {
    $file = Join-Path $layout $metadata
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
}
if ($manifest.Package.Identity.ProcessorArchitecture -ne 'x64') { throw 'Not an x64 package' }
$normalPackage = Join-Path $scratch Screeny.Verification.ARN94.msix
& $packer pack /d $layout /p $normalPackage /o | Select-Object -Last 2
"ARN94_NORMAL_PACKAGE=$normalPackage" >> $env:GITHUB_ENV
if ($env:CASE -ne 'baseline') {
    $faultPackage = Join-Path $scratch Screeny.Verification.ARN94Fault.msix
    $manifest.Package.Identity.Name = 'Screeny.Verification.ARN94Fault'
    $manifest.Save($manifestPath)
    $xbf = @(Get-ChildItem -LiteralPath $layout -Recurse -File -Filter MainWindow.xbf)
    if ($xbf.Count -ne 1) { throw 'Expected exactly one packaged MainWindow.xbf resource' }
    [IO.File]::WriteAllText($xbf[0].FullName, 'ARN94_SYNTHETIC_812f678dbca14b5fad357a994f68d6d0')
    & $packer pack /d $layout /p $faultPackage /o | Select-Object -Last 2
    "ARN94_FAULT_PACKAGE=$faultPackage" >> $env:GITHUB_ENV
}
