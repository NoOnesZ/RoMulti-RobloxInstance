$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$framework = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
$csc = Join-Path $framework "csc.exe"
$wpf = Join-Path $framework "WPF"
$manifest = Join-Path $root "app.manifest"
$out = Join-Path $root "RoMulti.exe"

if (!(Test-Path -LiteralPath $csc -PathType Leaf)) {
    throw "The x64 .NET Framework C# compiler was not found: $csc"
}

$refs = @(
    "/r:$wpf\PresentationFramework.dll",
    "/r:$wpf\PresentationCore.dll",
    "/r:$wpf\WindowsBase.dll",
    "/r:$framework\System.Xaml.dll",
    "/r:$framework\System.Windows.Forms.dll",
    "/r:$framework\System.Drawing.dll",
    "/r:$framework\System.Core.dll"
)

$sources = @("NativeMethods", "Models", "AfkOptimizer", "RobloxHandleEngine", "MainWindow", "MainWindow.Theme", "Program") |
    ForEach-Object { Join-Path $root "src\$_.cs" }

& $csc /nologo /target:winexe /platform:x64 /optimize+ "/win32manifest:$manifest" "/out:$out" @refs @sources
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

Write-Host "Built the RoMulti instance manager: $out"
