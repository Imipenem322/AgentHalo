param(
    [string]$OutputRoot = "",
    [string]$PackageVersion = "0.16.0"
)

$ErrorActionPreference = "Stop"

$scripts = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scripts
$windows = Join-Path $root "src\windows"
$packageRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $root "outputs"
} else {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
if ($PackageVersion -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]*$') {
    throw "PackageVersion contains unsupported filename characters."
}
$output = Join-Path $packageRoot "AgentHalo"
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

# Every build starts from an empty, script-owned package directory. This keeps
# stale binaries or diagnostics from leaking into a later ZIP while preserving
# every sibling under the caller-selected OutputRoot.
$normalizedPackageRoot = [System.IO.Path]::GetFullPath($packageRoot).TrimEnd('\')
$normalizedOutput = [System.IO.Path]::GetFullPath($output).TrimEnd('\')
if ([System.IO.Path]::GetDirectoryName($normalizedOutput) -ne $normalizedPackageRoot -or
    [System.IO.Path]::GetFileName($normalizedOutput) -ne "AgentHalo") {
    throw "The build output must be the AgentHalo child of OutputRoot."
}
if (Test-Path -LiteralPath $normalizedOutput) {
    $existingOutput = Get-Item -LiteralPath $normalizedOutput -Force
    if (-not $existingOutput.PSIsContainer -or
        ($existingOutput.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to clean a non-directory or reparse-point build output."
    }
    Remove-Item -LiteralPath $normalizedOutput -Recurse -Force
}

# Sync shared locale JSON into the Windows target so the build copies real
# file contents rather than relying on cross-platform symlink semantics.
$sharedLocales = Join-Path $root "src\shared\locales"
$windowsLocales = Join-Path $windows "locales"
New-Item -ItemType Directory -Force -Path $windowsLocales | Out-Null
Copy-Item -LiteralPath (Join-Path $sharedLocales "zh.json") -Destination (Join-Path $windowsLocales "zh.json") -Force
Copy-Item -LiteralPath (Join-Path $sharedLocales "en.json") -Destination (Join-Path $windowsLocales "en.json") -Force

if (-not (Test-Path -LiteralPath $csc)) {
    throw "The Windows C# compiler was not found at $csc"
}

New-Item -ItemType Directory -Force -Path $output | Out-Null

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
        "/resource:$(Join-Path $windowsLocales "en.json"),CodexHalo.locales.en.json",
        "/resource:$(Join-Path $root "src\integrations\deepseek-harness\index.mjs"),CodexHalo.integrations.deepseek-harness.index.mjs"
    )
    $exe = Join-Path $output "AgentHalo.exe"
    $sources = Get-ChildItem -LiteralPath $windows -Filter *.cs |
        Sort-Object Name |
        ForEach-Object { $_.FullName }

    & $csc /nologo /target:exe /platform:anycpu /optimize+ /main:CodexHalo.Program `
        /out:$exe /win32manifest:"$windows\app.manifest" /win32icon:$iconPath `
        $referenceArgs $resourceArgs $sources

    if ($LASTEXITCODE -ne 0) {
        throw "Compilation failed with exit code $LASTEXITCODE"
    }

    Copy-Item -LiteralPath "$root\README.md" -Destination "$output\README.md" -Force
    Copy-Item -LiteralPath "$root\README.zh-CN.md" -Destination "$output\README.zh-CN.md" -Force
    $dshBridgeSource = Join-Path $root "src\integrations\deepseek-harness"
    $dshBridgeOutput = Join-Path $output "integrations\deepseek-harness"
    New-Item -ItemType Directory -Force -Path $dshBridgeOutput | Out-Null
    foreach ($bridgeFile in @("index.mjs", "package.json", "cordis.patch.yml")) {
        $bridgeSource = Join-Path $dshBridgeSource $bridgeFile
        if (-not (Test-Path -LiteralPath $bridgeSource -PathType Leaf)) {
            throw "DeepSeek Harness bridge file is missing: $bridgeFile"
        }
        Copy-Item -LiteralPath $bridgeSource -Destination $dshBridgeOutput -Force
    }
    $packageDocs = Join-Path $output "docs"
    New-Item -ItemType Directory -Force -Path $packageDocs | Out-Null
    Copy-Item -LiteralPath (Join-Path $root "docs\DEEPSEEK_HARNESS_INTEGRATION.zh-CN.md") `
        -Destination $packageDocs -Force
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    $hashLine = "$hash  AgentHalo.exe"
    Set-Content -LiteralPath (Join-Path $output "SHA256.txt") -Value $hashLine `
        -Encoding ascii -NoNewline

    $archive = Join-Path $packageRoot `
        ("AgentHalo-Windows-v" + $PackageVersion + ".zip")
    Remove-Item -LiteralPath $archive -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $output "*") -DestinationPath $archive `
        -CompressionLevel Optimal
} finally {
    Remove-Item -LiteralPath $iconPath -Force -ErrorAction SilentlyContinue
}

Write-Host "Built $exe"
Write-Host "Packaged $archive"
