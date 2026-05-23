param(
    [switch]$NoStart,
    [switch]$DesktopParent
)

$ErrorActionPreference = "Stop"

$logDir = Join-Path $env:LOCALAPPDATA "DesktopPerfWidget"
$log = Join-Path $logDir "install.log"
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

function Limit-LogDirectory {
    param(
        [string]$DirectoryPath,
        [string]$ActiveLogPath
    )

    $maxBytes = 10MB
    $files = @(Get-ChildItem -LiteralPath $DirectoryPath -Filter "*.log" -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc, Name)
    $total = [int64](($files | Measure-Object Length -Sum).Sum)
    foreach ($file in $files) {
        if ($total -le $maxBytes) {
            break
        }

        if ($file.FullName -ieq $ActiveLogPath) {
            continue
        }

        $total -= [int64]$file.Length
        Remove-Item -LiteralPath $file.FullName -Force -ErrorAction SilentlyContinue
    }

    $active = Get-Item -LiteralPath $ActiveLogPath -ErrorAction SilentlyContinue
    if ($active -and $total -gt $maxBytes) {
        $otherTotal = [int64]((Get-ChildItem -LiteralPath $DirectoryPath -Filter "*.log" -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -ine $ActiveLogPath } |
            Measure-Object Length -Sum).Sum)
        $keepBytes = [Math]::Max(4096, $maxBytes - $otherTotal)
        $bytes = [IO.File]::ReadAllBytes($active.FullName)
        if ($bytes.Length -gt $keepBytes) {
            $tail = New-Object byte[] $keepBytes
            [Array]::Copy($bytes, $bytes.Length - $keepBytes, $tail, 0, $keepBytes)
            [IO.File]::WriteAllBytes($active.FullName, $tail)
        }
    }
}

function Write-InstallLog {
    param([string]$Message)
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message"
    Add-Content -LiteralPath $log -Value $line
    Limit-LogDirectory -DirectoryPath $logDir -ActiveLogPath $log
    Write-Host $Message
}

$exe = Join-Path $PSScriptRoot "DesktopPerfWidget.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    Write-InstallLog "Executable missing; building $exe"
    & (Join-Path $PSScriptRoot "Build-Arm64.ps1") -OutputPath $exe
}

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runValue = "DesktopPerfWidgetArm64"
$startupCommand = '"' + $exe + '"'
if ($DesktopParent) {
    $startupCommand += " --desktop-parent"
}

New-Item -Path $runKey -Force | Out-Null
New-ItemProperty -Path $runKey -Name $runValue -PropertyType String -Value $startupCommand -Force | Out-Null
Write-InstallLog "Startup entry set: $startupCommand"

Start-Process -FilePath $exe -ArgumentList "--stop" -Wait
Write-InstallLog "Stop signal sent to any existing widget instance."

if (-not $NoStart) {
    $arguments = @()
    if ($DesktopParent) {
        $arguments += "--desktop-parent"
    }

    if ($arguments.Count -gt 0) {
        Start-Process -FilePath $exe -ArgumentList $arguments
    }
    else {
        Start-Process -FilePath $exe
    }
    Write-InstallLog "Widget started."
}

Write-InstallLog "Install completed. Log: $log"
