param(
    [string]$OutputRoot = "",
    [switch]$CreateArchive
)

$ErrorActionPreference = "Stop"

function Normalize-AbsoluteDirectoryPath([string]$path) {
    $fullPath = [System.IO.Path]::GetFullPath($path)
    $rootPath = [System.IO.Path]::GetPathRoot($fullPath)
    if ($fullPath.Length -le $rootPath.Length) {
        return $rootPath
    }
    return $fullPath.TrimEnd([char[]]@('\', '/'))
}

$scripts = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scripts
$windows = Join-Path $root "src\windows"
$sharedSpecPath = Join-Path $root "src\shared\spec\agent-halo.v2.json"
$sharedSpec = Get-Content -LiteralPath $sharedSpecPath -Raw | ConvertFrom-Json
$packageVersion = [string]$sharedSpec.releaseVersion
if ($packageVersion -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]*$') {
    throw "The shared releaseVersion contains unsupported filename characters."
}
$rootPackagePath = Join-Path $root "package.json"
if (-not (Test-Path -LiteralPath $rootPackagePath -PathType Leaf)) {
    throw "The root DSH package manifest is missing."
}
$rootPackage = Get-Content -LiteralPath $rootPackagePath -Raw | ConvertFrom-Json
if ([string]$rootPackage.version -ne $packageVersion) {
    throw "The DSH package version must match the shared releaseVersion ($packageVersion)."
}

$packageRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $root "outputs"
} else {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
$packageName = "dsh-agenthalo"
$output = Join-Path $packageRoot $packageName
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

# Clean only the exact package directory owned by this script. Keep sibling
# outputs under OutputRoot, and refuse to recurse through a reparse point.
$normalizedPackageRoot = Normalize-AbsoluteDirectoryPath $packageRoot
$normalizedOutput = [System.IO.Path]::GetFullPath($output)
$outputParent = Normalize-AbsoluteDirectoryPath `
    ([System.IO.Path]::GetDirectoryName($normalizedOutput))
if (-not $outputParent.Equals(
        $normalizedPackageRoot,
        [System.StringComparison]::OrdinalIgnoreCase) -or
    [System.IO.Path]::GetFileName($normalizedOutput) -ne $packageName) {
    throw "The build output must be the $packageName child of OutputRoot."
}
if (Test-Path -LiteralPath $normalizedOutput) {
    $existingOutput = Get-Item -LiteralPath $normalizedOutput -Force
    if (-not $existingOutput.PSIsContainer -or
        ($existingOutput.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to clean a non-directory or reparse-point build output."
    }
    Remove-Item -LiteralPath $normalizedOutput -Recurse -Force
}

# Keep the existing embedded-locale loading behavior in the native app, and
# also ship the same files in a sibling locales directory for the DSH launcher.
$sharedLocales = Join-Path $root "src\shared\locales"
$windowsLocales = Join-Path $windows "locales"
New-Item -ItemType Directory -Force -Path $windowsLocales | Out-Null
Copy-Item -LiteralPath (Join-Path $sharedLocales "zh.json") `
    -Destination (Join-Path $windowsLocales "zh.json") -Force
Copy-Item -LiteralPath (Join-Path $sharedLocales "en.json") `
    -Destination (Join-Path $windowsLocales "en.json") -Force

if (-not (Test-Path -LiteralPath $csc)) {
    throw "The Windows C# compiler was not found at $csc"
}

$distribution = Join-Path $output "dist\windows"
$distributionLocales = Join-Path $distribution "locales"
New-Item -ItemType Directory -Force -Path $distributionLocales | Out-Null
Copy-Item -LiteralPath (Join-Path $sharedLocales "zh.json") `
    -Destination (Join-Path $distributionLocales "zh.json") -Force
Copy-Item -LiteralPath (Join-Path $sharedLocales "en.json") `
    -Destination (Join-Path $distributionLocales "en.json") -Force

$framework = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = Join-Path $framework "WPF"
$references = @(
    (Join-Path $framework "System.dll"),
    (Join-Path $framework "System.Core.dll"),
    (Join-Path $framework "System.Drawing.dll"),
    (Join-Path $framework "System.Windows.Forms.dll"),
    (Join-Path $framework "System.Web.Extensions.dll"),
    (Join-Path $framework "Microsoft.CSharp.dll"),
    (Join-Path $wpf "WindowsBase.dll"),
    (Join-Path $wpf "PresentationCore.dll"),
    (Join-Path $wpf "PresentationFramework.dll"),
    (Join-Path $framework "System.Xaml.dll")
)

$iconPath = Join-Path $env:TEMP ("AgentHalo-build-" +
    [guid]::NewGuid().ToString("N") + ".ico")
try {
    Add-Type -AssemblyName System.Drawing
    $bitmap = New-Object System.Drawing.Bitmap 64, 64
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $glow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(80, 43, 200, 255)), 11
    $ring = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 43, 200, 255)), 6
    $glow.StartCap = $glow.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $ring.StartCap = $ring.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    foreach ($pen in @($glow, $ring)) {
        $graphics.DrawArc($pen, 10, 10, 44, 44, -52, 140)
        $graphics.DrawArc($pen, 10, 10, 44, 44, 106, 194)
    }
    $handle = $bitmap.GetHicon()
    $icon = [System.Drawing.Icon]::FromHandle($handle)
    $stream = [System.IO.File]::Create($iconPath)
    $icon.Save($stream)
    $stream.Dispose()
    $icon.Dispose()
    $glow.Dispose()
    $ring.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()

    $referenceArgs = $references | ForEach-Object { "/reference:$_" }
    $resourceArgs = @(
        "/resource:$(Join-Path $windowsLocales "zh.json"),CodexHalo.locales.zh.json",
        "/resource:$(Join-Path $windowsLocales "en.json"),CodexHalo.locales.en.json"
    )
    $exe = Join-Path $distribution "AgentHalo.Dsh.exe"
    $sources = Get-ChildItem -LiteralPath $windows -Filter *.cs |
        Sort-Object Name |
        ForEach-Object { $_.FullName }

    & $csc /nologo /target:winexe /platform:anycpu /optimize+ /main:CodexHalo.Program `
        /out:$exe /win32manifest:"$windows\app.manifest" /win32icon:$iconPath `
        $referenceArgs $resourceArgs $sources

    if ($LASTEXITCODE -ne 0) {
        throw "Compilation failed with exit code $LASTEXITCODE"
    }

    foreach ($requiredRootFile in @("package.json", "README.md", "README.zh-CN.md", "LICENSE")) {
        $sourcePath = Join-Path $root $requiredRootFile
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Required DSH package file is missing: $requiredRootFile"
        }
        Copy-Item -LiteralPath $sourcePath -Destination $output -Force
    }

    $dshBridgeSource = Join-Path $root "src\integrations\deepseek-harness"
    foreach ($requiredBridgeFile in @("index.mjs", "launcher.mjs", "package.json", "cordis.patch.yml")) {
        if (-not (Test-Path -LiteralPath (Join-Path $dshBridgeSource $requiredBridgeFile) -PathType Leaf)) {
            throw "Required DeepSeek Harness package file is missing: $requiredBridgeFile"
        }
    }
    $packageSourceDirectory = Join-Path $output "src\integrations"
    New-Item -ItemType Directory -Force -Path $packageSourceDirectory | Out-Null
    Copy-Item -LiteralPath $dshBridgeSource -Destination $packageSourceDirectory -Recurse -Force

    $packageDocs = Join-Path $output "docs"
    New-Item -ItemType Directory -Force -Path $packageDocs | Out-Null
    Copy-Item -LiteralPath (Join-Path $root "docs\DEEPSEEK_HARNESS_INTEGRATION.zh-CN.md") `
        -Destination $packageDocs -Force
} finally {
    Remove-Item -LiteralPath $iconPath -Force -ErrorAction SilentlyContinue
}

Write-Host "Built $exe"
Write-Host "Staged installable DSH package at $output"
if ($CreateArchive) {
    $archive = Join-Path $packageRoot ("dsh-agenthalo-v" + $packageVersion + ".zip")
    $normalizedArchive = [System.IO.Path]::GetFullPath($archive)
    $archiveParent = Normalize-AbsoluteDirectoryPath `
        ([System.IO.Path]::GetDirectoryName($normalizedArchive))
    if (-not $archiveParent.Equals(
            $normalizedPackageRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($normalizedArchive) -ne
            ("dsh-agenthalo-v" + $packageVersion + ".zip")) {
        throw "The optional archive must be a direct child of OutputRoot."
    }
    if (Test-Path -LiteralPath $normalizedArchive) {
        $existingArchive = Get-Item -LiteralPath $normalizedArchive -Force
        if ($existingArchive.PSIsContainer -or
            ($existingArchive.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to replace a directory or reparse-point archive."
        }
        Remove-Item -LiteralPath $normalizedArchive -Force
    }
    Compress-Archive -Path (Join-Path $output "*") -DestinationPath $normalizedArchive `
        -CompressionLevel Optimal
    Write-Host "Packaged $normalizedArchive"
}
