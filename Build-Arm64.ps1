param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "DesktopPerfWidget.exe")
)

$ErrorActionPreference = "Stop"

$compilerCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\17\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe")
)
$source = Join-Path $PSScriptRoot "DesktopPerfWidget.cs"

$compiler = $compilerCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $compiler) {
    throw "A Roslyn C# compiler with /platform:arm64 support was not found. Install Visual Studio Build Tools 2022 or newer."
}

if (-not (Test-Path -LiteralPath $source)) {
    throw "Source file was not found: $source"
}

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory -and -not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

& $compiler `
    /nologo `
    /target:winexe `
    /platform:arm64 `
    /optimize+ `
    /reference:System.dll `
    /reference:System.Drawing.dll `
    /reference:System.Management.dll `
    /reference:System.Windows.Forms.dll `
    /out:$OutputPath `
    $source

if ($LASTEXITCODE -ne 0) {
    throw "csc.exe failed with exit code $LASTEXITCODE"
}

Write-Host "Built $OutputPath"
