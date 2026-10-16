# Remove o Claude Widget: fecha o widget, apaga os atalhos e a pasta %LOCALAPPDATA%\ClaudeWidget (com suas escolhas).
#
#   powershell -ExecutionPolicy Bypass -File .\desinstalar.ps1

Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -like '*\ClaudeWidget\claude-widget.ps1*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

foreach ($pasta in 'Desktop', 'Programs', 'Startup') {
    $atalho = Join-Path ([Environment]::GetFolderPath($pasta)) 'Claude Widget.lnk'
    if (Test-Path $atalho) { Remove-Item $atalho; "  removido: $atalho" }
}

$destino = Join-Path $env:LOCALAPPDATA 'ClaudeWidget'
if (Test-Path $destino) { Remove-Item $destino -Recurse -Force; "  removido: $destino" }
'Claude Widget desinstalado.'
