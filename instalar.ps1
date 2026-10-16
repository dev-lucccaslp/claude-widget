# Instala o Claude Widget para o seu usuário (não precisa de administrador).
#
#   powershell -ExecutionPolicy Bypass -File .\instalar.ps1              instala, cria os atalhos e abre
#   powershell -ExecutionPolicy Bypass -File .\instalar.ps1 -ComWindows  também abre sozinho quando o Windows liga
#
# Copia o widget para %LOCALAPPDATA%\ClaudeWidget e cria atalhos na Área de Trabalho e no menu Iniciar.
# Rodar de novo atualiza a instalação e mantém suas escolhas (cor, chapéu, posição).

param([switch]$ComWindows)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$origem = Join-Path $PSScriptRoot 'claude-widget.ps1'
$destino = Join-Path $env:LOCALAPPDATA 'ClaudeWidget'
$script = Join-Path $destino 'claude-widget.ps1'
$icone = Join-Path $destino 'claude-widget.ico'

if (-not (Test-Path (Join-Path $env:USERPROFILE '.claude\.credentials.json'))) {
    Write-Warning 'Não achei o login do Claude Code. Instale o Claude Code e entre com sua conta do Claude (Pro, Max, Team ou Enterprise) para o widget ter o que mostrar.'
}

# Fecha a cópia aberta antes de trocar o arquivo
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -like '*\ClaudeWidget\claude-widget.ps1*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

# UTF-8 com BOM: o Windows PowerShell 5.1 precisa disso para ler os acentos
New-Item -ItemType Directory -Force $destino | Out-Null
$texto = [IO.File]::ReadAllText($origem, [Text.Encoding]::UTF8)
[IO.File]::WriteAllText($script, $texto, (New-Object Text.UTF8Encoding($true)))

# Ícone do mascote (mesmo desenho do widget), PNG dentro do .ico em 16/32/48/256
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

# Atalhos: abrem o widget sem janela de terminal
function New-Atalho([string]$caminho) {
    $lnk = (New-Object -ComObject WScript.Shell).CreateShortcut($caminho)
    $lnk.TargetPath = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
    $lnk.Arguments = "-NoProfile -STA -WindowStyle Hidden -File `"$script`""
    $lnk.WorkingDirectory = $destino
    $lnk.WindowStyle = 7
    $lnk.IconLocation = "$icone,0"
    $lnk.Description = 'Claude Widget: uso do Claude na barra de tarefas'
    $lnk.Save()
    "  atalho: $caminho"
}
$atalhos = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Claude Widget.lnk')
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Claude Widget.lnk')
)
$inicializacao = Join-Path ([Environment]::GetFolderPath('Startup')) 'Claude Widget.lnk'
if ($ComWindows) { $atalhos += $inicializacao } elseif (Test-Path $inicializacao) { Remove-Item $inicializacao }

"Claude Widget instalado em $destino"
foreach ($a in $atalhos) { New-Atalho $a }
Start-Process -FilePath $atalhos[0]
''
'Pronto! Em alguns segundos o mascote aparece na barra de tarefas.'
'Clique no mascote para trocar cor, chapéu e posição, ou segure e arraste o widget pela barra.'
if (-not $ComWindows) { 'Para abrir junto com o Windows, rode de novo com -ComWindows.' }
