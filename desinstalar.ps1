# Remove o Claude Widget: fecha, tira do "Iniciar com o Windows", apaga os atalhos e a pasta
# %LOCALAPPDATA%\ClaudeWidget (com suas escolhas).
#
#   powershell -ExecutionPolicy Bypass -File .\desinstalar.ps1

$destino = Join-Path $env:LOCALAPPDATA 'ClaudeWidget'
$exe = Join-Path $destino 'ClaudeWidget.exe'
if (Test-Path $exe) { Start-Process $exe -ArgumentList '--sair' -Wait }
Get-Process ClaudeWidget -ErrorAction SilentlyContinue | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
Get-Process ClaudeWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -like '*\ClaudeWidget\claude-widget.ps1*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ClaudeWidget' -ErrorAction SilentlyContinue
foreach ($pasta in 'Desktop', 'Programs', 'Startup') {
    $atalho = Join-Path ([Environment]::GetFolderPath($pasta)) 'Claude Widget.lnk'
    if (Test-Path $atalho) { Remove-Item $atalho; "  removido: $atalho" }
}
if (Test-Path $destino) { Remove-Item $destino -Recurse -Force; "  removido: $destino" }
'Claude Widget desinstalado.'
