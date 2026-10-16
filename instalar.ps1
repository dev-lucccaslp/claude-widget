# Instala o Claude Widget para o seu usuário (não precisa de administrador).
#
#   powershell -ExecutionPolicy Bypass -File .\instalar.ps1              instala, cria os atalhos e abre
#   powershell -ExecutionPolicy Bypass -File .\instalar.ps1 -ComWindows  também abre sozinho quando o Windows liga
#
# Usa o ClaudeWidget.exe que estiver ao lado deste arquivo (zip da página de Releases) ou em dist\.
# Se não achar nenhum, compila com build.ps1. Rodar de novo atualiza e mantém suas escolhas.

param([switch]$ComWindows)
$ErrorActionPreference = 'Stop'

$destino = Join-Path $env:LOCALAPPDATA 'ClaudeWidget'
$exe = Join-Path $destino 'ClaudeWidget.exe'
$origem = @((Join-Path $PSScriptRoot 'ClaudeWidget.exe'), (Join-Path $PSScriptRoot 'dist\ClaudeWidget.exe')) |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $origem) {
    & (Join-Path $PSScriptRoot 'build.ps1')
    $origem = Join-Path $PSScriptRoot 'dist\ClaudeWidget.exe'
}

if (-not (Test-Path (Join-Path $env:USERPROFILE '.claude\.credentials.json'))) {
    Write-Warning 'Não achei o login do Claude Code. Instale o Claude Code e entre com sua conta do Claude (Pro, Max, Team ou Enterprise) para o widget ter o que mostrar.'
}

# Fecha o que estiver aberto: o .exe e a versão antiga em PowerShell
if (Test-Path $exe) { Start-Process $exe -ArgumentList '--sair' -Wait }
Get-Process ClaudeWidget -ErrorAction SilentlyContinue | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
Get-Process ClaudeWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -like '*\ClaudeWidget\claude-widget.ps1*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

New-Item -ItemType Directory -Force $destino | Out-Null
Copy-Item $origem $exe -Force
# Restos da versão em PowerShell (as escolhas em config.json continuam)
Remove-Item (Join-Path $destino 'claude-widget.ps1'), (Join-Path $destino 'claude-widget.ico') -ErrorAction SilentlyContinue

function New-Atalho([string]$caminho) {
    $lnk = (New-Object -ComObject WScript.Shell).CreateShortcut($caminho)
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $destino
    $lnk.Description = 'Claude Widget: uso do Claude na barra de tarefas'
    $lnk.Save()
    "  atalho: $caminho"
}
"Claude Widget instalado em $destino"
New-Atalho (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Claude Widget.lnk')
New-Atalho (Join-Path ([Environment]::GetFolderPath('Programs')) 'Claude Widget.lnk')

# Abrir com o Windows: a mesma opção do ícone nos ícones ocultos (a versão antiga usava um atalho em Inicializar)
Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) 'Claude Widget.lnk') -ErrorAction SilentlyContinue
if ($ComWindows) { Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ClaudeWidget' -Value "`"$exe`"" }

Start-Process $exe
''
'Pronto! Em alguns segundos o mascote aparece na barra de tarefas, e o ícone dele fica nos ícones ocultos.'
'Clique no mascote para trocar cor, chapéu, posição e o que o anel mostra, ou segure e arraste o widget pela barra.'
if (-not $ComWindows) { 'Para abrir junto com o Windows, use "Iniciar com o Windows" no ícone, ou rode de novo com -ComWindows.' }
