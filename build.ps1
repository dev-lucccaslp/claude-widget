# Compila o Claude Widget com o compilador C# que já vem no Windows (.NET Framework 4.x).
# Não precisa de Visual Studio nem de SDK. Gera dist\ClaudeWidget.exe.
#
#   powershell -ExecutionPolicy Bypass -File .\build.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path "$fw\csc.exe")) { $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$wpf = Join-Path $fw 'WPF'
$dist = Join-Path $PSScriptRoot 'dist'
$icone = Join-Path $dist 'claude-widget.ico'
New-Item -ItemType Directory -Force $dist | Out-Null

# Ícone do .exe: o mascote em 16/32/48/256 (PNG dentro do .ico)
function New-Png([int]$lado) {
    $bmp = New-Object Drawing.Bitmap $lado, $lado
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.Clear([Drawing.Color]::Transparent)
    $g.SmoothingMode = 'None'; $g.PixelOffsetMode = 'Half'
    $k = $lado / 32.0
    $y0 = ($lado - 20 * $k) / 2
    $laranja = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(215, 119, 87))
    $olho = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(20, 20, 19))
    foreach ($r in @(@(4, 0, 24, 16), @(0, 8, 32, 4), @(6, 16, 2, 4), @(10, 16, 2, 4), @(20, 16, 2, 4), @(24, 16, 2, 4))) {
        $g.FillRectangle($laranja, [single]($r[0] * $k), [single]($y0 + $r[1] * $k), [single]($r[2] * $k), [single]($r[3] * $k))
    }
    foreach ($r in @(@(8, 4, 2, 4), @(22, 4, 2, 4))) {
        $g.FillRectangle($olho, [single]($r[0] * $k), [single]($y0 + $r[1] * $k), [single]($r[2] * $k), [single]($r[3] * $k))
    }
    $g.Dispose(); $laranja.Dispose(); $olho.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}
$tamanhos = 16, 32, 48, 256
$pngs = foreach ($t in $tamanhos) { , (New-Png $t) }
$fs = [IO.File]::Create($icone)
$w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$tamanhos.Count)
$offset = 6 + 16 * $tamanhos.Count
for ($i = 0; $i -lt $tamanhos.Count; $i++) {
    $lado = if ($tamanhos[$i] -ge 256) { 0 } else { $tamanhos[$i] }
    $w.Write([byte]$lado); $w.Write([byte]$lado); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()

$opcoes = @(
    '/nologo', '/target:winexe', '/optimize+', '/codepage:65001',
    "/out:$dist\ClaudeWidget.exe", "/win32icon:$icone", "/win32manifest:$PSScriptRoot\src\app.manifest",
    "/r:$wpf\PresentationFramework.dll", "/r:$wpf\PresentationCore.dll", "/r:$wpf\WindowsBase.dll",
    "/r:$wpf\UIAutomationClient.dll", "/r:$wpf\UIAutomationTypes.dll",
    "/r:$fw\System.Xaml.dll", "/r:$fw\System.Web.Extensions.dll", "/r:$fw\System.Windows.Forms.dll", "/r:$fw\System.Drawing.dll", "/r:$fw\System.Management.dll",
    "$PSScriptRoot\src\ClaudeWidget.cs"
)
& "$fw\csc.exe" @opcoes
if ($LASTEXITCODE) { throw 'A compilação falhou.' }
"Gerado: $dist\ClaudeWidget.exe ($([math]::Round((Get-Item "$dist\ClaudeWidget.exe").Length / 1KB)) KB)"
