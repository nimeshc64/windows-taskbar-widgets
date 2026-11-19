param(
    [string]$Version = '1.0.0',
    [switch]$SkipMsi
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$stage = Join-Path $root 'artifacts\app'
$single = Join-Path $root 'artifacts\single'

$widgets = @(
    'BluetoothBatteryWidget',
    'AudioControlWidget',
    'SystemMonitorWidget',
    'NowPlayingWidget',
    'DevToolsWidget',
    'NetworkWidget',
    'SystemToolsWidget',
    'NotificationsWidget'
)

function Invoke-Checked([scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE" }
}

Remove-Item $dist, (Join-Path $root 'artifacts') -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist, $stage, $single | Out-Null

$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', "-p:Version=$Version", '-p:DebugType=none', '-nologo')

foreach ($w in $widgets) {
    $proj = Join-Path $root "src\$w\$w.csproj"
    Write-Host "==> $w" -ForegroundColor Cyan

    Invoke-Checked { dotnet publish $proj @common -o $stage }

    $out = Join-Path $single $w
    Invoke-Checked {
        dotnet publish $proj @common -o $out `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true
    }
    Copy-Item (Join-Path $out "$w.exe") (Join-Path $dist "$w-$Version-win-x64.exe")
}

Compress-Archive -Path "$stage\*" -DestinationPath (Join-Path $dist "TaskbarWidgets-$Version-win-x64-portable.zip")

if (-not $SkipMsi) {
    Write-Host '==> MSI' -ForegroundColor Cyan
    Invoke-Checked { dotnet tool restore }
    Invoke-Checked { dotnet tool run wix extension add -g WixToolset.Util.wixext/5.0.2 }
    Invoke-Checked {
        dotnet tool run wix build (Join-Path $root 'installer\TaskbarWidgets.wxs') `
            -arch x64 `
            -ext WixToolset.Util.wixext `
            -d "Version=$Version" `
            -d "AppDir=$stage" `
            -d "Root=$root" `
            -o (Join-Path $dist "TaskbarWidgets-$Version-x64.msi")
    }
    Remove-Item (Join-Path $dist '*.wixpdb') -ErrorAction SilentlyContinue
}

Get-ChildItem $dist | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
