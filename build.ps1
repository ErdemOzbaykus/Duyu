# Builds Duyu with what Visual Studio Build Tools 2022 and Windows already ship. No packages.
#   .\build.ps1          build dist\
#   .\build.ps1 -Test    also run the offline DSP tests
param([switch]$Test)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = Join-Path $root 'build'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $build, $dist | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio 2022 Build Tools (C++) bulunamadi.' }

# cl.exe needs the vcvars environment, so the native part runs through a batch file.
$native = @(
    '@echo off'
    "call `"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul 2>nul"
    'cd /d "%~dp0"'
    'cl /nologo /LD /utf-8 /std:c++20 /EHsc /O2 /GL /guard:cf /MT /DUNICODE /DNOMINMAX ..\engine\engine.cpp /Fe:Duyu.Engine.dll /link /LTCG /guard:cf ole32.lib avrt.lib mmdevapi.lib propsys.lib uuid.lib runtimeobject.lib || exit /b 1'
)
if ($Test) {
    $native += 'cl /nologo /utf-8 /std:c++20 /EHsc /O2 /MT /DNOMINMAX ..\tests\dsp_tests.cpp /Fe:dsp_tests.exe || exit /b 1'
    $native += '"%~dp0dsp_tests.exe" || exit /b 1'
    $native += 'cl /nologo /utf-8 /std:c++20 /EHsc /O2 /MT /DUNICODE /DNOMINMAX ..\tests\routing_tests.cpp /Fe:routing_tests.exe /link ole32.lib avrt.lib mmdevapi.lib propsys.lib uuid.lib runtimeobject.lib || exit /b 1'
    $native += '"%~dp0routing_tests.exe" || exit /b 1'
}
Set-Content (Join-Path $build 'native.cmd') $native -Encoding Default
& (Join-Path $build 'native.cmd')
if ($LASTEXITCODE -ne 0) { throw 'Native derleme basarisiz.' }

# Icon: the same rings as the in-app mark, stored as PNG frames inside an .ico
Add-Type -AssemblyName System.Drawing
$ico = Join-Path $build 'Duyu.ico'
$frames = foreach ($size in 16, 24, 32, 48, 64, 256) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $color = [System.Drawing.Color]::FromArgb(255, 176, 103)
    $scale = $size / 32.0
    $g.ScaleTransform($scale, $scale)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $color), 11, 11, 10, 10)
    $g.DrawEllipse((New-Object System.Drawing.Pen $color, 2.5), 6.5, 6.5, 19, 19)
    $g.DrawEllipse((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(130, $color)), 2), 1.5, 1.5, 29, 29)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Data = $ms.ToArray() }
}
$w = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($ico))
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $side = [byte]($f.Size % 256)
    $w.Write($side); $w.Write($side); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$f.Data.Length); $w.Write([uint32]$offset)
    $offset += $f.Data.Length
}
foreach ($f in $frames) { $w.Write([byte[]]$f.Data) }
$w.Close()

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @(
    "$fw\mscorlib.dll", "$fw\System.dll", "$fw\System.Core.dll", "$fw\System.Xaml.dll", "$fw\System.Xml.dll", "$fw\System.Drawing.dll",
    "$fw\System.Windows.Forms.dll", "$fw\System.Web.Extensions.dll",
    "$fw\WPF\WindowsBase.dll", "$fw\WPF\PresentationCore.dll", "$fw\WPF\PresentationFramework.dll",
    "$fw\WPF\UIAutomationTypes.dll", "$fw\WPF\UIAutomationProvider.dll"
) | ForEach-Object { "/r:$_" }
$sources = Get-ChildItem (Join-Path $root 'ui') -Filter *.cs | ForEach-Object FullName

& "$vs\MSBuild\Current\Bin\Roslyn\csc.exe" /nologo /noconfig /nostdlib /langversion:7.3 /target:winexe /platform:x64 `
    /optimize /codepage:65001 /warn:4 /nowarn:1701,1702 `
    "/out:$dist\Duyu.exe" "/win32icon:$ico" "/win32manifest:$root\app.manifest" `
    "/resource:$root\ui\Theme.xaml,Duyu.Theme.xaml" "/resource:$root\ui\remote.html,Duyu.remote.html" $references $sources
if ($LASTEXITCODE -ne 0) { throw 'UI derleme basarisiz.' }

Copy-Item (Join-Path $build 'Duyu.Engine.dll'), $ico $dist -Force
if ($Test) {
    powershell -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $root 'tests\wheel_test.ps1') -Exe "$dist\Duyu.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Arayuz testi basarisiz.' }
}
Write-Host "Hazir: dist\Duyu.exe"
