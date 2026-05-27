param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "Direct2DDock.exe")
)

$ErrorActionPreference = "Stop"

$compilerCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\17\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe")
)
$source = Join-Path $PSScriptRoot "Direct2DDock.cs"
$sharpDx = Join-Path $PSScriptRoot "SharpDX.dll"
$sharpDxD2D = Join-Path $PSScriptRoot "SharpDX.Direct2D1.dll"
$sharpDxDxgi = Join-Path $PSScriptRoot "SharpDX.DXGI.dll"

$compiler = $compilerCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $compiler) {
    throw "A Roslyn C# compiler with /platform:arm64 support was not found. Install Visual Studio Build Tools 2022 or newer."
}

foreach ($path in @($source, $sharpDx, $sharpDxD2D, $sharpDxDxgi)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required file was not found: $path"
    }
}

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory -and -not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

$compilerArgs = @(
    "/nologo",
    "/target:winexe",
    "/platform:arm64",
    "/optimize+",
    "/reference:System.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.Windows.Forms.dll",
    "/reference:$sharpDx",
    "/reference:$sharpDxD2D",
    "/reference:$sharpDxDxgi",
    "/out:$OutputPath",
    $source
)

& $compiler @compilerArgs

if ($LASTEXITCODE -ne 0) {
    throw "csc.exe failed with exit code $LASTEXITCODE"
}

Write-Host "Built $OutputPath"
