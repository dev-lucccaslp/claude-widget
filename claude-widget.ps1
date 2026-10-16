# Claude Widget: o mascote do Claude Code na barra de tarefas do Windows, com um anel mostrando o % de uso do plano.
# https://github.com/dev-lucccaslp/claude-widget
#
# - Aparece em todas as barras (uma cópia por monitor) e some no monitor que estiver em tela cheia.
# - Passando o mouse, outro mascote chega andando trazendo um balão com o uso da sessão (5h) e da semana.
# - Clique no anel: atualiza · Clique no mascote ou botão direito: cor, chapéu, óculos, posição, movimento, Fechar.
# - Arraste o widget para qualquer ponto da barra; a posição fica salva por monitor.
#
# Lê o login do Claude Code em %USERPROFILE%\.claude\.credentials.json e só conversa com api.anthropic.com.
# Não renova nem grava o login: se o token expirar, abrir o Claude Code já renova e o widget volta sozinho.
# As escolhas do menu ficam em config.json, na mesma pasta deste arquivo.
#
# Uso: powershell -NoProfile -STA -WindowStyle Hidden -File claude-widget.ps1 [-IntervaloMinutos 5] [-Mostrar maior|sessao|semanal] [-Margem 12]

param(
    [int]$IntervaloMinutos = 5,
    [ValidateSet('maior', 'sessao', 'semanal')] [string]$Mostrar = 'maior',
    [int]$Margem = 12,
    # Para testar sem mouse: chama o visitante e o balão logo ao abrir
    [switch]$Demonstrar,
    # Gera as imagens do README nesta pasta e sai, sem abrir o widget
    [string]$Previa
)

Add-Type -Namespace ClaudeWidget -Name Win -MemberDefinition @'
public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public class MONITORINFOEX {
    public int cbSize = 104; public RECT rcMonitor; public RECT rcWork; public int dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
[DllImport("user32.dll")] public static extern uint GetDpiForSystem();
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string windowName);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
[DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
[DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] public static extern bool GetMonitorInfo(IntPtr monitor, [In, Out] MONITORINFOEX info);
[DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
[DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

// Ordem na tela: widgets por cima da barra e o balão por cima de tudo.
// Com o menu aberto (Pausado) não mexe em nada, senão o widget cobriria o menu.
public static readonly System.Collections.Generic.List<IntPtr> Widgets = new System.Collections.Generic.List<IntPtr>();
public static IntPtr Balao = IntPtr.Zero;
public static bool Pausado = false;
public static void Elevar() {
    if (Pausado) return;
    foreach (IntPtr h in Widgets) SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0213);
    if (Balao != IntPtr.Zero) SetWindowPos(Balao, new IntPtr(-1), 0, 0, 0, 0, 0x0213);
}

// Quando a barra (ou qualquer janela) vem para a frente, reorganiza na hora, sem esperar o timer
public delegate void WinEventProc(IntPtr hook, uint evento, IntPtr hwnd, int objeto, int filho, uint thread, uint tempo);
[DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
static readonly WinEventProc procFrente = AoMudarFrente;
static void AoMudarFrente(IntPtr hook, uint evento, IntPtr hwnd, int objeto, int filho, uint thread, uint tempo) { Elevar(); }
public static void VigiarFrente() { SetWinEventHook(3, 3, IntPtr.Zero, procFrente, 0, 0, 0); }

// Só para o GIF do README: o retângulo que mudou entre dois quadros BGRA (x, y, largura, altura), ou null se iguais
public static int[] Diferenca(byte[] a, byte[] b, int largura, int altura) {
    int x0 = largura, y0 = altura, x1 = -1, y1 = -1;
    for (int y = 0; y < altura; y++) {
        for (int x = 0; x < largura; x++) {
            int i = (y * largura + x) * 4;
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) {
                if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
            }
        }
    }
    if (x1 < 0) return null;
    return new int[] { x0, y0, x1 - x0 + 1, y1 - y0 + 1 };
}
'@

# Nítido em telas com escala (precisa vir antes de carregar o WPF)
try { [void][ClaudeWidget.Win]::SetProcessDpiAwarenessContext([IntPtr]::new(-2)) } catch {}
$escala = 1.0
try { $escala = [ClaudeWidget.Win]::GetDpiForSystem() / 96 } catch {}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, UIAutomationClient, UIAutomationTypes
# Desenho pela CPU: as janelas são minúsculas, e pela placa de vídeo o driver reserva ~300 MB à toa
[Windows.Media.RenderOptions]::ProcessRenderMode = [Windows.Interop.RenderMode]::SoftwareOnly
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'

# Só uma cópia rodando por vez
if (-not $Previa) {
    $criou = $false
    $mutex = New-Object System.Threading.Mutex($true, 'Local\ClaudeWidget', [ref]$criou)
    if (-not $criou) { return }
}

$credPath = Join-Path $env:USERPROFILE '.claude\.credentials.json'
$ptBR = [Globalization.CultureInfo]::GetCultureInfo('pt-BR')
$inv = [Globalization.CultureInfo]::InvariantCulture
$conversor = New-Object Windows.Media.BrushConverter
$altura = 40          # altura do widget
$folga = 6            # espaço acima do widget para chapéu e pulo
$palco = 56           # largura da área à direita por onde o visitante chega
$chegada = 8          # onde o visitante para dentro dessa área
$larguraBarra = 206   # barras de % do balão
$classesDaShell = 'Progman', 'WorkerW', 'Shell_TrayWnd', 'Shell_SecondaryTrayWnd'

# --- Escolhas do menu, salvas entre uma abertura e outra ---
$configPath = Join-Path $PSScriptRoot 'config.json'
$config = @{ Cor = '#D77757'; Chapeu = 'nenhum'; Oculos = $false; Movimento = $true; Visitante = $true; Posicoes = @{} }
try {
    $salvo = Get-Content $configPath -Raw -ErrorAction Stop | ConvertFrom-Json
    foreach ($p in $salvo.PSObject.Properties) {
        if ($p.Name -eq 'Posicoes') {
            foreach ($m in $p.Value.PSObject.Properties) {
                $config.Posicoes[$m.Name] = @{ Modo = [string]$m.Value.Modo; Lado = [string]$m.Value.Lado; Desvio = [double]$m.Value.Desvio }
            }
        }
        elseif ($config.ContainsKey($p.Name)) { $config[$p.Name] = $p.Value }
    }
} catch {}
function Save-Config {
    if ($Previa) { return }
    try { $config | ConvertTo-Json -Depth 4 | Set-Content -Path $configPath -Encoding UTF8 } catch {}
}

$cores = [ordered]@{
    'Laranja Claude' = '#D77757'; 'Azul' = '#4F8EF7'; 'Verde' = '#3FB950'; 'Roxo' = '#A371F7'
    'Rosa' = '#F778BA'; 'Dourado' = '#E3B341'; 'Cinza' = '#9DA5B4'
}
# Chapéus em pixel art acima da cabeça (y negativo); cada parte é cor + desenho
$chapeus = [ordered]@{
    nenhum  = @{ Nome = 'Nenhum'; Partes = @() }
    bone    = @{ Nome = 'Boné'; Partes = @(
            @('#2F6FEB', 'M8,-6 H22 V-4 H24 V0 H6 V-4 H8 Z'),
            @('#1F4FB8', 'M22,-2 H32 V0 H22 Z'),
            @('#FFFFFF', 'M12,-4 h2 v2 h-2 Z')) }
    cartola = @{ Nome = 'Cartola'; Partes = @(
            @('#4A4A4A', 'M8,-10 H24 V-2 H8 Z'),
            @('#E5484D', 'M8,-4 H24 V-2 H8 Z'),
            @('#5C5C5C', 'M4,-2 H28 V0 H4 Z')) }
    festa   = @{ Nome = 'Festa'; Partes = @(
            @('#EC4899', 'M10,-2 H22 V0 H10 Z M13,-6 H19 V-4 H13 Z'),
            @('#FACC15', 'M12,-4 H20 V-2 H12 Z M15,-8 H17 V-6 H15 Z'),
            @('#FFFFFF', 'M14,-10 H18 V-8 H14 Z')) }
    coroa   = @{ Nome = 'Coroa'; Partes = @(
            @('#F5C542', 'M6,-4 H26 V0 H6 Z M6,-8 H9 V-4 H6 Z M14,-9 H18 V-4 H14 Z M23,-8 H26 V-4 H23 Z'),
            @('#E5484D', 'M15,-3 H17 V-1 H15 Z'),
            @('#4F8EF7', 'M9,-3 H11 V-1 H9 Z M21,-3 H23 V-1 H21 Z')) }
    bruxa   = @{ Nome = 'Bruxa'; Partes = @(
            @('#6D28D9', 'M2,-2 H30 V0 H2 Z M10,-6 H22 V-4 H10 Z M12,-8 H20 V-6 H12 Z M16,-10 H22 V-8 H16 Z'),
            @('#22C55E', 'M8,-4 H24 V-2 H8 Z')) }
    natal   = @{ Nome = 'Natal'; Partes = @(
            @('#DC2626', 'M6,-6 H24 V-2 H6 Z M12,-8 H26 V-6 H12 Z'),
            @('#F5F5F5', 'M4,-2 H28 V0 H4 Z M26,-10 H30 V-6 H26 Z')) }
}

# --- Desenho ---
# Mascote: o mesmo desenho em blocos da tela de boas-vindas do Claude Code (pixel 2x4)
function Get-XamlBoneco([string]$p) {
    @"
<Canvas x:Name="$p" Width="32" Height="20">
  <Canvas.RenderTransform><TranslateTransform/></Canvas.RenderTransform>
  <Path x:Name="${p}Corpo" Data="M4,0 H28 V8 H32 V12 H28 V16 H4 V12 H0 V8 H4 Z"/>
  <Path x:Name="${p}Pernas" Data="M6,16 h2 v4 h-2 Z M10,16 h2 v4 h-2 Z M20,16 h2 v4 h-2 Z M24,16 h2 v4 h-2 Z"/>
  <Rectangle x:Name="${p}OlhoE" Canvas.Left="8" Canvas.Top="4" Width="2" Height="4" Fill="#141413"/>
  <Rectangle x:Name="${p}OlhoD" Canvas.Left="22" Canvas.Top="4" Width="2" Height="4" Fill="#141413"/>
  <Path x:Name="${p}Oculos" Fill="#0F0F0F" Visibility="Collapsed" Data="M6,3 h6 v5 h-6 Z M20,3 h6 v5 h-6 Z M12,4 h8 v2 h-8 Z"/>
  <Canvas x:Name="${p}Chapeu"/>
</Canvas>
"@
}

[xml]$xaml = @"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" ShowActivated="False" Focusable="False"
        ResizeMode="NoResize" SizeToContent="WidthAndHeight" UseLayoutRounding="True">
  <StackPanel Orientation="Horizontal" Margin="0,$folga,0,0">
    <Border x:Name="Fundo" CornerRadius="5" Height="$altura" Padding="8,0,8,0" Cursor="Hand">
      <Border.Style>
        <Style TargetType="Border">
          <Setter Property="Background" Value="#01FFFFFF"/>
          <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter Property="Background" Value="#1AFFFFFF"/>
            </Trigger>
          </Style.Triggers>
        </Style>
      </Border.Style>
      <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
        <Canvas x:Name="AreaMascote" Width="32" Height="20" Background="Transparent" VerticalAlignment="Center" SnapsToDevicePixels="True">
          $(Get-XamlBoneco 'M')
        </Canvas>
        <Grid Width="32" Height="32" Margin="8,0,0,0" VerticalAlignment="Center">
          <Ellipse x:Name="Trilho" StrokeThickness="3" Stroke="#3DFFFFFF"/>
          <Path x:Name="Arco" StrokeThickness="3" StrokeStartLineCap="Round" StrokeEndLineCap="Round" Stroke="#D77757"/>
          <TextBlock x:Name="Texto" Text="…" Foreground="White" FontSize="10" FontWeight="SemiBold"
                     FontFamily="Segoe UI Variable Display, Segoe UI"
                     HorizontalAlignment="Center" VerticalAlignment="Center"/>
        </Grid>
      </StackPanel>
    </Border>
    <Canvas Width="$palco" Height="$altura" ClipToBounds="True" IsHitTestVisible="False" SnapsToDevicePixels="True">
      $(Get-XamlBoneco 'V')
    </Canvas>
  </StackPanel>
</Window>
"@

# Balão com o uso: janela própria que fica acima dos widgets e deixa o mouse passar
[xml]$xamlBalao = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" ShowActivated="False" Focusable="False" IsHitTestVisible="False"
        ResizeMode="NoResize" SizeToContent="WidthAndHeight" UseLayoutRounding="True"
        FontFamily="Segoe UI Variable Text, Segoe UI">
  <Grid Margin="8">
    <Grid.Effect><DropShadowEffect BlurRadius="10" ShadowDepth="2" Opacity="0.5" Color="Black"/></Grid.Effect>
    <StackPanel>
      <Border Background="#2B2B2B" BorderBrush="#454545" BorderThickness="1" CornerRadius="8" Padding="12,10,12,10" Width="232">
        <StackPanel>
          <TextBlock Text="Uso do Claude" Foreground="#F3F3F3" FontSize="13" FontWeight="SemiBold"/>
          <Grid Margin="0,10,0,0">
            <TextBlock Text="Sessão (5h)" Foreground="#D6D6D6" FontSize="12"/>
            <TextBlock x:Name="PctSessao" HorizontalAlignment="Right" Foreground="#F3F3F3" FontSize="12" FontWeight="SemiBold"/>
          </Grid>
          <Border Height="4" CornerRadius="2" Background="#40FFFFFF" Margin="0,5,0,0">
            <Border x:Name="BarraSessao" HorizontalAlignment="Left" CornerRadius="2" Width="0"/>
          </Border>
          <TextBlock x:Name="RenovaSessao" Foreground="#9A9A9A" FontSize="11" Margin="0,4,0,0"/>
          <Grid Margin="0,10,0,0">
            <TextBlock Text="Semanal" Foreground="#D6D6D6" FontSize="12"/>
            <TextBlock x:Name="PctSemana" HorizontalAlignment="Right" Foreground="#F3F3F3" FontSize="12" FontWeight="SemiBold"/>
          </Grid>
          <Border Height="4" CornerRadius="2" Background="#40FFFFFF" Margin="0,5,0,0">
            <Border x:Name="BarraSemana" HorizontalAlignment="Left" CornerRadius="2" Width="0"/>
          </Border>
          <TextBlock x:Name="RenovaSemana" Foreground="#9A9A9A" FontSize="11" Margin="0,4,0,0"/>
          <TextBlock x:Name="Rodape" Foreground="#9A9A9A" FontSize="11" Margin="0,10,0,0" TextWrapping="Wrap"/>
          <TextBlock Text="Anel: atualizar · Bonequinho: personalizar" Foreground="#7A7A7A" FontSize="10.5" Margin="0,2,0,0"/>
        </StackPanel>
      </Border>
      <Path x:Name="Bico" Data="M0,0 L8,8 L16,0" Fill="#2B2B2B" Stroke="#454545" StrokeThickness="1" HorizontalAlignment="Left" Margin="24,-1,0,0"/>
    </StackPanel>
  </Grid>
</Window>
'@

$app = New-Object Windows.Application
$app.ShutdownMode = 'OnExplicitShutdown'

# O que todos os widgets mostram; Update-Uso muda e Show-Estado desenha em cada um
$estado = @{
    Pct = 0; Cor = '#D77757'; Texto = '…'; Fonte = 10; Claro = $false
    SessaoPct = 0; SemanaPct = 0; SessaoRenova = ''; SemanaRenova = ''
    Sessao = 'Sessão (5h): …'; Semana = 'Semanal: …'; Hora = 'Carregando…'
}
$widgets = @{}

function New-Pincel([string]$hex) { $conversor.ConvertFromString($hex) }

function Get-Cor([double]$pct) {
    if ($pct -ge 90) { '#E5484D' } elseif ($pct -ge 75) { '#E8B339' } else { '#D77757' }
}

function Get-Arco([double]$pct) {
    $c = 16; $r = 14.5
    $p = [math]::Max(0, [math]::Min(100, $pct)) / 100
    if ($p -ge 0.999) { return New-Object Windows.Media.EllipseGeometry((New-Object Windows.Point($c, $c)), $r, $r) }
    if ($p -le 0.001) { return $null }
    $ang = $p * 2 * [math]::PI - [math]::PI / 2
    $grande = if ($p -gt 0.5) { 1 } else { 0 }
    $dados = [object[]]@($c, ($c - $r), $r, $grande, ($c + $r * [math]::Cos($ang)), ($c + $r * [math]::Sin($ang)))
    [Windows.Media.Geometry]::Parse([string]::Format($inv, 'M {0},{1} A {2},{2} 0 {3} 1 {4},{5}', $dados))
}

function Format-Renova($valor, [string]$formato) {
    if (-not $valor) { return '' }
    $quando = if ($valor -is [datetime]) { $valor.ToLocalTime() } else { [DateTimeOffset]::Parse([string]$valor).LocalDateTime }
    'renova ' + $quando.ToString($formato, $ptBR)
}

# --- Balão ---
$balao = [Windows.Markup.XamlReader]::Load((New-Object Xml.XmlNodeReader $xamlBalao))
$b = @{}
foreach ($nome in 'PctSessao', 'BarraSessao', 'RenovaSessao', 'PctSemana', 'BarraSemana', 'RenovaSemana', 'Rodape', 'Bico') { $b[$nome] = $balao.FindName($nome) }
$cartaoBalao = $balao.Content
$hBalao = (New-Object Windows.Interop.WindowInteropHelper $balao).EnsureHandle()
# fora do Alt+Tab, sem foco e transparente para o mouse
[void][ClaudeWidget.Win]::SetWindowLong($hBalao, -20, ([ClaudeWidget.Win]::GetWindowLong($hBalao, -20) -bor 0x080000A0))
[ClaudeWidget.Win]::Balao = $hBalao
$donoBalao = $null

function Update-Balao {
    foreach ($par in @(@('Sessao', $estado.SessaoPct, $estado.SessaoRenova), @('Semana', $estado.SemanaPct, $estado.SemanaRenova))) {
        $b["Pct$($par[0])"].Text = "$($par[1])%"
        $b["Barra$($par[0])"].Width = $larguraBarra * [math]::Min(100, $par[1]) / 100
        $b["Barra$($par[0])"].Background = New-Pincel (Get-Cor $par[1])
        $b["Renova$($par[0])"].Text = $par[2]
    }
    $b.Rodape.Text = $estado.Hora
}

# Onde o bico do balão aponta: o visitante, ou o anel se o visitante estiver desligado
function Get-AlvoBalao($w) {
    if ($w.Visita -eq 'parado') { $w.Janela.Left + $w.Fundo.ActualWidth + $w.VisitaX + 16 } else { $w.Janela.Left + 64 }
}

# Posiciona o balão acima do alvo sem sair da tela; o bico acompanha
function Set-Balao([double]$alvo, [double]$topoWidget, $tela) {
    $cartaoBalao.Measure((New-Object Windows.Size([double]::PositiveInfinity, [double]::PositiveInfinity)))
    $tamanho = $cartaoBalao.DesiredSize
    $esquerda = $alvo - 40
    if ($tela) { $esquerda = [math]::Max($tela[0] + 4, [math]::Min($tela[1] - $tamanho.Width - 4, $esquerda)) }
    $b.Bico.Margin = New-Object Windows.Thickness([math]::Max(12, [math]::Min(204, $alvo - $esquerda - 16)), -1, 0, 0)
    $balao.Left = $esquerda
    $balao.Top = $topoWidget + $folga + 6 - ($tamanho.Height - 8)
}

function Show-Balao($w) {
    Update-Balao
    $script:donoBalao = $w
    Set-Balao (Get-AlvoBalao $w) $w.Janela.Top $w.Tela
    if (-not $Previa -and -not $balao.IsVisible) { $balao.Show() }
    [ClaudeWidget.Win]::Elevar()
}

function Hide-Balao($w) {
    if ($null -ne $script:donoBalao -and ($null -eq $w -or $script:donoBalao -eq $w)) {
        $balao.Hide()
        $script:donoBalao = $null
    }
}

# --- Uso ---
function Show-Estado($w) {
    $w.Arco.Data = Get-Arco $estado.Pct
    $w.Arco.Stroke = New-Pincel $estado.Cor
    $w.Texto.Text = $estado.Texto
    $w.Texto.FontSize = $estado.Fonte
    $w.Texto.Foreground = New-Pincel $(if ($estado.Claro) { '#1A1A1A' } else { '#FFFFFF' })
    $w.Trilho.Stroke = New-Pincel $(if ($estado.Claro) { '#26000000' } else { '#3DFFFFFF' })
    $w.MiSessao.Header = $estado.Sessao
    $w.MiSemana.Header = $estado.Semana
    $w.MiHora.Header = $estado.Hora
}

function Set-Uso([int]$sessao, [int]$semana, [string]$renovaSessao, [string]$renovaSemana, [string]$hora) {
    $valor = switch ($Mostrar) {
        'sessao' { $sessao }
        'semanal' { $semana }
        default { [math]::Max($sessao, $semana) }
    }
    $estado.Pct = $valor
    $estado.Cor = Get-Cor $valor
    $estado.Texto = "$valor%"
    $estado.Fonte = if ($valor -ge 100) { 8.5 } else { 10 }
    $estado.SessaoPct = $sessao
    $estado.SemanaPct = $semana
    $estado.SessaoRenova = $renovaSessao
    $estado.SemanaRenova = $renovaSemana
    $estado.Sessao = "Sessão (5h): $sessao%" + $(if ($renovaSessao) { " · $renovaSessao" })
    $estado.Semana = "Semanal: $semana%" + $(if ($renovaSemana) { " · $renovaSemana" })
    $estado.Hora = $hora
}

function Update-Uso {
    $estado.Claro = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -ErrorAction SilentlyContinue).SystemUsesLightTheme -eq 1
    try {
        $cred = Get-Content $credPath -Raw | ConvertFrom-Json
        $cabecalhos = @{
            Authorization    = "Bearer $($cred.claudeAiOauth.accessToken)"
            'anthropic-beta' = 'oauth-2025-04-20'
        }
        $u = Invoke-RestMethod -Uri 'https://api.anthropic.com/api/oauth/usage' -Headers $cabecalhos -TimeoutSec 15
        $sessao = if ($u.five_hour) { [int][math]::Round([double]$u.five_hour.utilization) } else { 0 }
        $semana = if ($u.seven_day) { [int][math]::Round([double]$u.seven_day.utilization) } else { 0 }
        Set-Uso $sessao $semana (Format-Renova $u.five_hour.resets_at "'às' HH:mm") (Format-Renova $u.seven_day.resets_at 'ddd HH:mm') ('Atualizado às ' + (Get-Date).ToString('HH:mm'))
        $relogio.Interval = [TimeSpan]::FromMinutes($IntervaloMinutos)
    }
    catch {
        $codigo = $null
        if ($_.Exception.Response) { $codigo = [int]$_.Exception.Response.StatusCode }
        $motivo = if (-not (Test-Path $credPath)) { 'Entre no Claude Code para ver o uso' }
        elseif ($codigo -eq 401) { 'Login expirado: abra o Claude Code' }
        else { 'Sem resposta (' + (Get-Date).ToString('HH:mm') + ')' }
        $estado.Pct = 0
        $estado.Texto = '?'
        $estado.Fonte = 12
        $estado.Hora = $motivo
        # Logo depois de ligar o PC a rede pode ainda não estar pronta: tenta de novo em 1 minuto
        $relogio.Interval = [TimeSpan]::FromMinutes(1)
    }
    foreach ($w in $widgets.Values) { Show-Estado $w }
    if ($script:donoBalao) { Show-Balao $script:donoBalao }
}

# --- Estilo do mascote ---
function Set-EstiloBoneco($boneco, [string]$cor, [string]$chapeuId, [bool]$oculos) {
    $pincel = New-Pincel $cor
    $boneco.Corpo.Fill = $pincel
    $boneco.Pernas.Fill = $pincel
    $boneco.Oculos.Visibility = if ($oculos) { 'Visible' } else { 'Collapsed' }
    $boneco.Chapeu.Children.Clear()
    $chapeu = $chapeus[$chapeuId]
    if (-not $chapeu) { return }
    foreach ($parte in $chapeu.Partes) {
        $forma = New-Object Windows.Shapes.Path
        $forma.Fill = New-Pincel $parte[0]
        $forma.Data = [Windows.Media.Geometry]::Parse($parte[1])
        [void]$boneco.Chapeu.Children.Add($forma)
    }
}

function Set-Estilo($w) {
    foreach ($boneco in $w.Principal, $w.Visitante) { Set-EstiloBoneco $boneco $config.Cor ([string]$config.Chapeu) ([bool]$config.Oculos) }
}

# --- Posição na barra ---
$condBotao = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)

# Pergunta à barra (UI Automation) onde estão o Iniciar, os Widgets, os apps e a bandeja
function Get-Layout([IntPtr]$barra) {
    $ocupados = New-Object System.Collections.ArrayList
    $bandeja = $null
    $inicio = $null
    try {
        $raiz = [Windows.Automation.AutomationElement]::FromHandle($barra)
        foreach ($botao in $raiz.FindAll([Windows.Automation.TreeScope]::Descendants, $condBotao)) {
            $c = $botao.Current
            $r = $c.BoundingRectangle
            if ($r.IsEmpty -or $r.Width -le 0) { continue }
            $faixa = @(($r.Left / $escala), ($r.Right / $escala))
            [void]$ocupados.Add($faixa)
            if ($c.ClassName -like 'SystemTray.*' -and ($null -eq $bandeja -or $faixa[0] -lt $bandeja)) { $bandeja = $faixa[0] }
            if ($c.AutomationId -eq 'StartButton') { $inicio = $faixa[0] }
        }
    } catch {}
    @{ Ocupados = $ocupados; Bandeja = $bandeja; Inicio = $inicio; Quando = Get-Date }
}

function Test-Livre($layout, [double]$de, [double]$ate) {
    foreach ($o in $layout.Ocupados) { if ($o[0] -lt $ate -and $o[1] -gt $de) { return $false } }
    $true
}

# Automático: canto esquerdo se estiver livre (ou logo depois do botão de Widgets);
# com o Iniciar no canto esquerdo, encosta na bandeja; senão, o maior espaço livre
function Get-XAuto($layout, [double]$esq, [double]$dir, [double]$largura) {
    $livres = New-Object System.Collections.ArrayList
    $cursor = $esq
    foreach ($o in ($layout.Ocupados | Sort-Object { $_[0] })) {
        if ($o[0] -gt $cursor) { [void]$livres.Add(@($cursor, $o[0])) }
        $cursor = [math]::Max($cursor, $o[1])
    }
    if ($dir -gt $cursor) { [void]$livres.Add(@($cursor, $dir)) }
    $precisa = $largura + 2 * $Margem
    $inicioNoCanto = $null -ne $layout.Inicio -and ($layout.Inicio - $esq) -lt 80
    if (-not $inicioNoCanto) {
        foreach ($l in $livres) { if ($l[1] - $l[0] -ge $precisa) { return $l[0] + $Margem } }
    }
    if ($null -ne $layout.Bandeja) {
        $x = $layout.Bandeja - $largura - $Margem
        if (Test-Livre $layout $x ($x + $largura)) { return $x }
    }
    $maior = $livres | Sort-Object { $_[1] - $_[0] } -Descending | Select-Object -First 1
    if ($maior) { return $maior[0] + $Margem }
    $esq + $Margem
}

function Get-PosicaoSalva([string]$monitor) {
    if ($monitor -and $config.Posicoes.ContainsKey($monitor)) { $config.Posicoes[$monitor] } else { @{ Modo = 'auto' } }
}

# Depois de arrastar: guarda a distância até o lado mais perto, para sobreviver a mudanças de resolução
function Save-PosicaoArrastada($w) {
    if (-not $w.Monitor -or -not $w.Limites) { return }
    $esq, $dir = $w.Limites
    $largura = $w.Janela.ActualWidth
    $config.Posicoes[$w.Monitor] = if ($w.Janela.Left + $largura / 2 -lt ($esq + $dir) / 2) {
        @{ Modo = 'manual'; Lado = 'esquerda'; Desvio = [math]::Round($w.Janela.Left - $esq) }
    }
    else {
        @{ Modo = 'manual'; Lado = 'direita'; Desvio = [math]::Round($dir - $w.Janela.Left - $largura) }
    }
    Save-Config
}

# --- Menu (clique no mascote ou botão direito) ---
function Add-Opcao($pai, [string]$titulo, [scriptblock]$acao, [string]$tag, [switch]$info) {
    $opcao = New-Object Windows.Controls.MenuItem
    $opcao.Header = $titulo
    if ($tag) { $opcao.Tag = $tag }
    if ($acao) { $opcao.Add_Click($acao) }
    if ($info) { $opcao.IsEnabled = $false }
    [void]$pai.Items.Add($opcao)
    $opcao
}

function Get-WidgetDe($elemento) {
    foreach ($w in $widgets.Values) { if ($w.Fundo -eq $elemento -or $w.AreaMascote -eq $elemento) { return $w } }
}

function Get-WidgetDoMenu($item) {
    while ($item -and $item -isnot [Windows.Controls.ContextMenu]) { $item = $item.Parent }
    if ($item) { Get-WidgetDe $item.PlacementTarget }
}

$escolher = { param($s, $e) Set-Opcao $s.Tag (Get-WidgetDoMenu $s) }

# Tag "grupo=valor" para cor, chapéu e posição; só o nome para as opções liga/desliga
function Test-Opcao([string]$tag, $w) {
    $grupo, $valor = $tag -split '=', 2
    switch ($grupo) {
        'cor' { $config.Cor -eq $valor }
        'chapeu' { $config.Chapeu -eq $valor }
        'posicao' { (Get-PosicaoSalva $w.Monitor).Modo -eq $valor }
        default { [bool]$config[$grupo] }
    }
}

function Set-Opcao([string]$tag, $w) {
    $grupo, $valor = $tag -split '=', 2
    switch ($grupo) {
        'cor' { $config.Cor = $valor }
        'chapeu' { $config.Chapeu = $valor }
        'posicao' {
            if ($w -and $w.Monitor) {
                if ($valor -eq 'auto') { $config.Posicoes.Remove($w.Monitor) }
                else { $config.Posicoes[$w.Monitor] = @{ Modo = $valor; Lado = 'esquerda'; Desvio = $Margem } }
                $w.Layout = $null
            }
        }
        default { $config[$grupo] = -not [bool]$config[$grupo] }
    }
    Save-Config
    foreach ($x in $widgets.Values) { Set-Estilo $x }
    if (-not $config.Movimento) {
        $fila.Clear()
        foreach ($x in $widgets.Values) { Set-Boneco $x.Principal (Q) }
    }
    Start-Animacao 'pular'
}

function Update-Marcas($itens, $w) {
    foreach ($item in $itens) {
        if ($item -isnot [Windows.Controls.MenuItem]) { continue }
        if ($item.Tag) { $item.IsChecked = Test-Opcao $item.Tag $w }
        if ($item.Items.Count) { Update-Marcas $item.Items $w }
    }
}

function Open-Menu($w) {
    if (-not $w) { return }
    $menu = $w.Fundo.ContextMenu
    $menu.PlacementTarget = $w.Fundo
    $menu.Placement = [Windows.Controls.Primitives.PlacementMode]::Top
    $menu.IsOpen = $true
}

function New-Menu {
    $menu = New-Object Windows.Controls.ContextMenu
    $info = @(
        (Add-Opcao $menu '' -info)
        (Add-Opcao $menu '' -info)
        (Add-Opcao $menu '' -info)
    )
    [void]$menu.Items.Add((New-Object Windows.Controls.Separator))
    [void](Add-Opcao $menu 'Atualizar agora' { Update-Uso; Start-Animacao 'pular' })
    [void](Add-Opcao $menu 'Abrir uso no claude.ai' { Start-Process 'https://claude.ai/settings/usage' })
    [void]$menu.Items.Add((New-Object Windows.Controls.Separator))
    $cor = Add-Opcao $menu 'Cor'
    foreach ($nome in $cores.Keys) { [void](Add-Opcao $cor $nome $escolher "cor=$($cores[$nome])") }
    $chapeu = Add-Opcao $menu 'Chapéu'
    foreach ($id in $chapeus.Keys) { [void](Add-Opcao $chapeu $chapeus[$id].Nome $escolher "chapeu=$id") }
    [void](Add-Opcao $menu 'Óculos escuros' $escolher 'Oculos')
    $posicao = Add-Opcao $menu 'Posição neste monitor'
    [void](Add-Opcao $posicao 'Automático (desvia do Iniciar e dos Widgets)' $escolher 'posicao=auto')
    [void](Add-Opcao $posicao 'Canto esquerdo' $escolher 'posicao=esquerda')
    [void](Add-Opcao $posicao 'Perto do relógio' $escolher 'posicao=direita')
    [void](Add-Opcao $posicao 'Onde eu arrastar (segure e arraste o widget)' -tag 'posicao=manual' -info)
    [void]$menu.Items.Add((New-Object Windows.Controls.Separator))
    [void](Add-Opcao $menu 'Movimento' $escolher 'Movimento')
    [void](Add-Opcao $menu 'Visitante ao passar o mouse' $escolher 'Visitante')
    [void]$menu.Items.Add((New-Object Windows.Controls.Separator))
    [void](Add-Opcao $menu 'Fechar widget' { $app.Shutdown() })

    $menu.Add_Opened({
            param($s, $e)
            $w = Get-WidgetDe $s.PlacementTarget
            Update-Marcas $s.Items $w
            [ClaudeWidget.Win]::Pausado = $true
            Hide-Balao $null
            Stop-Visita $w
            # O widget nunca fica ativo (para não roubar o foco), e aí o menu não percebe o clique fora.
            # Enquanto o menu está aberto, ele fica ativo; o vigia fecha o menu se outra janela vier para a frente.
            if ($w) {
                [void][ClaudeWidget.Win]::SetWindowLong($w.Hwnd, -20, ([ClaudeWidget.Win]::GetWindowLong($w.Hwnd, -20) -band (-bnot 0x08000000)))
                [void][ClaudeWidget.Win]::SetForegroundWindow($w.Hwnd)
            }
            $script:menuAberto = $s
            $script:frenteAoAbrir = [ClaudeWidget.Win]::GetForegroundWindow()
        })
    $menu.Add_Closed({
            param($s, $e)
            [ClaudeWidget.Win]::Pausado = $false
            $script:menuAberto = $null
            $w = Get-WidgetDe $s.PlacementTarget
            if ($w) {
                [void][ClaudeWidget.Win]::SetWindowLong($w.Hwnd, -20, ([ClaudeWidget.Win]::GetWindowLong($w.Hwnd, -20) -bor 0x08000000))
                if ($w.Fundo.IsMouseOver) { Start-Visita $w }
            }
            [ClaudeWidget.Win]::Elevar()
        })
    @{ Menu = $menu; Info = $info }
}

$menuAberto = $null
$frenteAoAbrir = [IntPtr]::Zero

# --- Arrastar pela barra ---
$arrasto = $null
function Get-CursorX {
    $p = New-Object 'ClaudeWidget.Win+POINT'
    [void][ClaudeWidget.Win]::GetCursorPos([ref]$p)
    $p.X / $escala
}

function Stop-Arrasto($w, [switch]$salvar) {
    $w.Arrastando = $false
    if ($salvar) { Save-PosicaoArrastada $w; Start-Animacao 'pular' }
}

# --- Widgets, um por barra ---
function Get-Boneco($janela, [string]$p) {
    $raiz = $janela.FindName($p)
    $boneco = @{ Raiz = $raiz; Pulo = $raiz.RenderTransform; Ultimo = '' }
    foreach ($parte in 'Corpo', 'Pernas', 'OlhoE', 'OlhoD', 'Oculos', 'Chapeu') { $boneco[$parte] = $janela.FindName("$p$parte") }
    $boneco
}

function New-Widget([IntPtr]$barra) {
    $janela = [Windows.Markup.XamlReader]::Load((New-Object Xml.XmlNodeReader $xaml))
    $menu = New-Menu
    $w = [pscustomobject]@{
        Barra       = $barra
        Janela      = $janela
        Hwnd        = [IntPtr]::Zero
        Fundo       = $janela.FindName('Fundo')
        AreaMascote = $janela.FindName('AreaMascote')
        Trilho      = $janela.FindName('Trilho')
        Arco        = $janela.FindName('Arco')
        Texto       = $janela.FindName('Texto')
        Principal   = Get-Boneco $janela 'M'
        Visitante   = Get-Boneco $janela 'V'
        Visita      = 'fora'
        VisitaX     = $palco
        Passo       = 0
        MiSessao    = $menu.Info[0]
        MiSemana    = $menu.Info[1]
        MiHora      = $menu.Info[2]
        Monitor     = $null
        Tela        = $null
        Limites     = $null
        Layout      = $null
        RectBarra   = ''
        Arrastando  = $false
    }
    [Windows.Controls.Canvas]::SetTop($w.Visitante.Raiz, ($altura - 20) / 2)
    [Windows.Controls.Canvas]::SetLeft($w.Visitante.Raiz, $palco)
    $w.Visitante.Raiz.Visibility = 'Hidden'

    $w.Fundo.ContextMenu = $menu.Menu
    $w.Fundo.Add_MouseLeftButtonUp({ Update-Uso; Start-Animacao 'pular' })
    $w.Fundo.Add_MouseEnter({ param($s, $e) Start-Visita (Get-WidgetDe $s) })
    $w.Fundo.Add_MouseLeave({ param($s, $e) Stop-Visita (Get-WidgetDe $s) })
    $w.AreaMascote.Add_MouseLeftButtonUp({ param($s, $e) $e.Handled = $true; Open-Menu (Get-WidgetDe $s) })

    # Segurar e arrastar move o widget pela barra; um clique normal continua sendo clique
    $w.Fundo.Add_PreviewMouseLeftButtonDown({ param($s, $e) $script:arrasto = @{ W = (Get-WidgetDe $s); X0 = (Get-CursorX); L0 = 0; Movendo = $false } })
    $w.Fundo.Add_PreviewMouseMove({
            param($s, $e)
            $a = $script:arrasto
            if (-not $a -or -not $a.W -or $e.LeftButton -ne 'Pressed') { return }
            $dx = (Get-CursorX) - $a.X0
            if (-not $a.Movendo) {
                if ([math]::Abs($dx) -lt 5 -or -not $a.W.Limites) { return }
                $a.Movendo = $true
                $a.L0 = $a.W.Janela.Left
                $a.W.Arrastando = $true
                Stop-Visita $a.W
                [void]$s.CaptureMouse()
            }
            $esq, $dir = $a.W.Limites
            $a.W.Janela.Left = [math]::Max($esq, [math]::Min($dir - $a.W.Janela.ActualWidth, $a.L0 + $dx))
        })
    $w.Fundo.Add_PreviewMouseLeftButtonUp({
            param($s, $e)
            $a = $script:arrasto
            $script:arrasto = $null
            if (-not $a -or -not $a.Movendo) { return }
            $e.Handled = $true
            $s.ReleaseMouseCapture()
            Stop-Arrasto $a.W -salvar
        })
    $w.Fundo.Add_LostMouseCapture({
            param($s, $e)
            $a = $script:arrasto
            if ($a -and $a.Movendo) { $script:arrasto = $null; Stop-Arrasto $a.W -salvar }
        })

    # Fora do Alt+Tab e sem roubar o foco
    $w.Hwnd = (New-Object Windows.Interop.WindowInteropHelper $janela).EnsureHandle()
    $estilo = [ClaudeWidget.Win]::GetWindowLong($w.Hwnd, -20)
    [void][ClaudeWidget.Win]::SetWindowLong($w.Hwnd, -20, ($estilo -bor 0x08000080))
    [ClaudeWidget.Win]::Widgets.Add($w.Hwnd)
    Show-Estado $w
    Set-Estilo $w
    $w
}

function Remove-Widget($chave) {
    $w = $widgets[$chave]
    [void][ClaudeWidget.Win]::Widgets.Remove($w.Hwnd)
    Hide-Balao $w
    $w.Janela.Close()
    $widgets.Remove($chave)
}

# Barra principal e as dos outros monitores
function Get-Barras {
    $principal = [ClaudeWidget.Win]::FindWindow('Shell_TrayWnd', [NullString]::Value)
    if ($principal -ne [IntPtr]::Zero) { $principal }
    $outra = [IntPtr]::Zero
    while ($true) {
        $outra = [ClaudeWidget.Win]::FindWindowEx([IntPtr]::Zero, $outra, 'Shell_SecondaryTrayWnd', [NullString]::Value)
        if ($outra -eq [IntPtr]::Zero) { break }
        $outra
    }
}

# A janela da frente cobre o monitor inteiro (vídeo, apresentação, jogo)?
function Get-MonitorTelaCheia {
    $frente = [ClaudeWidget.Win]::GetForegroundWindow()
    if ($frente -eq [IntPtr]::Zero) { return [IntPtr]::Zero }
    $classe = New-Object System.Text.StringBuilder 64
    [void][ClaudeWidget.Win]::GetClassName($frente, $classe, 64)
    if ($classesDaShell -contains $classe.ToString()) { return [IntPtr]::Zero }
    $monitor = [ClaudeWidget.Win]::MonitorFromWindow($frente, 0)
    $info = New-Object 'ClaudeWidget.Win+MONITORINFOEX'
    $r = New-Object 'ClaudeWidget.Win+RECT'
    if ($monitor -eq [IntPtr]::Zero -or -not [ClaudeWidget.Win]::GetMonitorInfo($monitor, $info) -or -not [ClaudeWidget.Win]::GetWindowRect($frente, [ref]$r)) { return [IntPtr]::Zero }
    $m = $info.rcMonitor
    if ($r.Left -le $m.Left -and $r.Top -le $m.Top -and $r.Right -ge $m.Right -and $r.Bottom -ge $m.Bottom) { $monitor } else { [IntPtr]::Zero }
}

# Coloca o widget na sua barra conforme a posição escolhida; devolve $false se ele deve ficar escondido
function Update-Posicao($w, [IntPtr]$telaCheia) {
    $r = New-Object 'ClaudeWidget.Win+RECT'
    if (-not [ClaudeWidget.Win]::GetWindowRect($w.Barra, [ref]$r)) { return $false }
    $monitor = [ClaudeWidget.Win]::MonitorFromWindow($w.Barra, 2)
    if ($monitor -eq $telaCheia) { return $false }
    $info = New-Object 'ClaudeWidget.Win+MONITORINFOEX'
    if (-not [ClaudeWidget.Win]::GetMonitorInfo($monitor, $info)) { return $false }
    if ($r.Top -ge $info.rcMonitor.Bottom - 4) { return $false }  # barra escondida (ocultar automaticamente)

    $w.Monitor = $info.szDevice
    $w.Tela = @(($info.rcMonitor.Left / $escala), ($info.rcMonitor.Right / $escala))
    $esq = $r.Left / $escala
    $dir = $r.Right / $escala
    $w.Limites = @($esq, $dir)
    $topo = $r.Top / $escala + (($r.Bottom - $r.Top) / $escala - $altura) / 2 - $folga
    if (-not ([math]::Abs($w.Janela.Top - $topo) -le 0.5)) { $w.Janela.Top = $topo }  # -not (-le) também cobre o NaN inicial
    if ($w.Arrastando) { return $true }

    $largura = if ($w.Janela.ActualWidth -gt 0) { $w.Janela.ActualWidth } else { 144 }
    $pos = Get-PosicaoSalva $w.Monitor
    if ($pos.Modo -in 'auto', 'direita') {
        # Refaz o mapa da barra a cada 30 s ou quando ela muda de tamanho
        $chaveBarra = "$($r.Left),$($r.Top),$($r.Right),$($r.Bottom)"
        if (-not $w.Layout -or $w.RectBarra -ne $chaveBarra -or ((Get-Date) - $w.Layout.Quando).TotalSeconds -gt 30) {
            $w.Layout = Get-Layout $w.Barra
            $w.RectBarra = $chaveBarra
        }
    }
    $esquerda = switch ($pos.Modo) {
        'esquerda' { $esq + $Margem }
        'direita' { $(if ($null -ne $w.Layout.Bandeja) { $w.Layout.Bandeja } else { $dir - 300 }) - $largura - $Margem }
        'manual' { if ($pos.Lado -eq 'direita') { $dir - $pos.Desvio - $largura } else { $esq + $pos.Desvio } }
        default { Get-XAuto $w.Layout $esq $dir $largura }
    }
    $esquerda = [math]::Max($esq, [math]::Min($dir - $largura, $esquerda))
    if (-not ([math]::Abs($w.Janela.Left - $esquerda) -le 0.5)) { $w.Janela.Left = $esquerda }
    $true
}

# --- Animação: quadros de 120 ms com pulo, pernas e olhos ---
function Get-Pernas([int[]]$alturas) {
    $x = 6, 10, 20, 24
    $g = [Windows.Media.Geometry]::Parse((0..3 | ForEach-Object { "M$($x[$_]),16 h2 v$($alturas[$_]) h-2 Z" }) -join ' ')
    $g.Freeze()
    $g
}
$pernas = @{
    N = Get-Pernas 4, 4, 4, 4   # paradas
    A = Get-Pernas 2, 4, 2, 4   # passo com um par
    B = Get-Pernas 4, 2, 4, 2   # passo com o outro par
    T = Get-Pernas 2, 2, 2, 2   # encolhidas no pulo
}

function Q([int]$x = 0, [int]$y = 0, [int]$olhar = 0, [switch]$fechado, [string]$pe = 'N', [int]$n = 1) {
    for ($i = 0; $i -lt $n; $i++) { @{ X = $x; Y = $y; Olhar = $olhar; Fechado = [bool]$fechado; Pe = $pe } }
}
$sequencias = @{
    piscar   = @(Q -fechado; Q -n 2)
    respirar = @(Q -n 3; Q -y -1 -n 3; Q -n 3; Q -y -1 -n 3; Q -fechado; Q -n 2)
    olhar    = @(Q -olhar -2 -n 6; Q -olhar -2 -fechado; Q -olhar -2 -n 3; Q -n 2; Q -olhar 2 -n 8; Q)
    # anda para a esquerda, volta, vai para a direita e volta, olhando para onde vai
    andar    = @(
        foreach ($lado in -1, 1) {
            Q -x $lado -olhar ($lado * 2) -pe A -n 2
            Q -x (2 * $lado) -y -1 -olhar ($lado * 2) -pe B -n 2
            Q -x (3 * $lado) -olhar ($lado * 2) -pe A -n 2
            Q -x (2 * $lado) -y -1 -olhar (-$lado * 2) -pe B -n 2
            Q -x $lado -olhar (-$lado * 2) -pe A -n 2
            Q -y -1 -pe B -n 2
        }
        Q
    )
    pular    = @(1..2 | ForEach-Object { Q -y -1; Q -y -3 -pe T -fechado -n 2; Q -y -1; Q }) + @(Q)
    dancar   = @(1..3 | ForEach-Object { Q -x -2 -y -2 -olhar -2 -pe T; Q -x -2 -pe A -n 2; Q -x 2 -y -2 -olhar 2 -pe T; Q -x 2 -pe B -n 2 }) + @(Q)
}
# Peso de cada movimento no sorteio (soma 100); sempre emenda um no outro
$pesos = [ordered]@{ andar = 30; respirar = 25; olhar = 15; pular = 10; dancar = 10; piscar = 10 }
$fila = New-Object System.Collections.Queue
$sorteio = New-Object System.Random

function Start-Animacao([string]$nome) {
    $fila.Clear()
    foreach ($q in $sequencias[$nome]) { $fila.Enqueue($q) }
}

function Get-Sorteio {
    $sorte = $sorteio.Next(100)
    foreach ($nome in $pesos.Keys) {
        $sorte -= $pesos[$nome]
        if ($sorte -lt 0) { return $nome }
    }
    'respirar'
}

function Set-Boneco($boneco, $q) {
    $chave = "$($q.X)|$($q.Y)|$($q.Olhar)|$($q.Fechado)|$($q.Pe)"
    if ($boneco.Ultimo -eq $chave) { return }
    $boneco.Ultimo = $chave
    $boneco.Pulo.X = $q.X
    $boneco.Pulo.Y = $q.Y
    $boneco.Pernas.Data = $pernas[$q.Pe]
    $alturaOlho = if ($q.Fechado) { 1 } else { 4 }
    $topoOlho = if ($q.Fechado) { 6 } else { 4 }
    foreach ($olho in @(@($boneco.OlhoE, 8), @($boneco.OlhoD, 22))) {
        [Windows.Controls.Canvas]::SetLeft($olho[0], $olho[1] + $q.Olhar)
        [Windows.Controls.Canvas]::SetTop($olho[0], $topoOlho)
        $olho[0].Height = $alturaOlho
    }
}

# Visitante: chega andando pela direita quando o mouse entra e vai embora quando sai
function Start-Visita($w) {
    if (-not $w -or $w.Arrastando -or [ClaudeWidget.Win]::Pausado) { return }
    if (-not $config.Visitante -or $w.Visita -eq 'parado') { Show-Balao $w; return }
    if ($w.Visita -ne 'entrando') {
        $w.Visita = 'entrando'
        $w.Visitante.Raiz.Visibility = 'Visible'
    }
}

function Stop-Visita($w) {
    if (-not $w) { return }
    Hide-Balao $w
    if ($w.Visita -in 'entrando', 'parado') { $w.Visita = 'saindo' }
}

function Step-Visita($w, [bool]$mexer) {
    if ($w.Visita -eq 'fora') { return }
    $w.Passo++
    $par = ($w.Passo % 2) -eq 0
    $passo = if ($mexer) { 6 } else { 100 }
    switch ($w.Visita) {
        'entrando' {
            $w.VisitaX = [math]::Max($chegada, $w.VisitaX - $passo)
            if ($w.VisitaX -le $chegada) {
                $w.Visita = 'parado'
                $w.Passo = 0
                $q = Q -olhar -2
                Show-Balao $w
            }
            else { $q = Q -y $(if ($par) { -1 } else { 0 }) -olhar -2 -pe $(if ($par) { 'A' } else { 'B' }) }
        }
        'parado' { $q = Q -olhar -2 -fechado:(($w.Passo % 30) -ge 28) }
        'saindo' {
            $w.VisitaX = [math]::Min($palco, $w.VisitaX + $passo)
            $q = Q -y $(if ($par) { -1 } else { 0 }) -olhar 2 -pe $(if ($par) { 'A' } else { 'B' })
            if ($w.VisitaX -ge $palco) {
                $w.Visita = 'fora'
                $w.Visitante.Raiz.Visibility = 'Hidden'
                $q = Q
            }
        }
    }
    [Windows.Controls.Canvas]::SetLeft($w.Visitante.Raiz, $w.VisitaX)
    Set-Boneco $w.Visitante $q
}

# --- Imagens do README (-Previa): desenha cenas falsas fora da tela, sem capturar nada do seu PC ---
function Save-Png($bitmap, [string]$arquivo) {
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $fs = [IO.File]::Create($arquivo); $enc.Save($fs); $fs.Close()
}

function Get-Foto($visual, [double]$largura, [double]$alturaFoto, [double]$zoom = 2) {
    $visual.Measure((New-Object Windows.Size($largura, $alturaFoto)))
    $visual.Arrange((New-Object Windows.Rect(0, 0, $largura, $alturaFoto)))
    $visual.UpdateLayout()
    $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap([int]($largura * $zoom), [int]($alturaFoto * $zoom), (96 * $zoom), (96 * $zoom), [Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($visual)
    $rtb.Freeze()
    $rtb
}

# GIF animado: guarda só o pedaço que mudou em cada quadro (quadros iguais viram mais tempo no anterior);
# cada pedaço vira um GIF de 1 quadro com paleta própria, e os pedaços são emendados com atraso e repetição
function Save-Gif($quadros, [string]$arquivo, [int]$atraso = 12) {
    $partes = New-Object System.Collections.ArrayList
    $anterior = $null
    foreach ($quadro in $quadros) {
        $l = $quadro.PixelWidth; $h = $quadro.PixelHeight
        $px = New-Object byte[] ($l * $h * 4)
        $quadro.CopyPixels($px, $l * 4, 0)
        $ret = if ($null -eq $anterior) { @(0, 0, $l, $h) } else { [ClaudeWidget.Win]::Diferenca($anterior, $px, $l, $h) }
        $anterior = $px
        if ($null -eq $ret) { $partes[$partes.Count - 1].Atraso += $atraso; continue }
        $pedaco = New-Object Windows.Media.Imaging.CroppedBitmap($quadro, (New-Object Windows.Int32Rect($ret[0], $ret[1], $ret[2], $ret[3])))
        [void]$partes.Add(@{ Imagem = $pedaco; X = $ret[0]; Y = $ret[1]; Atraso = $atraso })
    }

    $saida = [IO.File]::Create($arquivo)
    $bw = New-Object IO.BinaryWriter $saida
    $primeiro = $true
    foreach ($parte in $partes) {
        $paleta = New-Object Windows.Media.Imaging.BitmapPalette($parte.Imagem, 256)
        $indexado = New-Object Windows.Media.Imaging.FormatConvertedBitmap($parte.Imagem, [Windows.Media.PixelFormats]::Indexed8, $paleta, 0)
        $enc = New-Object Windows.Media.Imaging.GifBitmapEncoder
        $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($indexado))
        $ms = New-Object IO.MemoryStream
        $enc.Save($ms)
        $g = $ms.ToArray()
        $pack = $g[10]
        $pos = 13
        $tabela = $null
        if ($pack -band 0x80) {
            $tam = 3 * (1 -shl (($pack -band 7) + 1))
            $tabela = New-Object byte[] $tam
            [Array]::Copy($g, $pos, $tabela, 0, $tam)
            $pos += $tam
        }
        while ($g[$pos] -eq 0x21) { $pos += 2; while ($g[$pos] -ne 0) { $pos += $g[$pos] + 1 }; $pos++ }
        if ($primeiro) {
            $bw.Write([Text.Encoding]::ASCII.GetBytes('GIF89a'))
            $bw.Write($g, 6, 4)                                   # largura e altura
            $bw.Write([byte[]](0x70, 0, 0))                       # sem tabela global
            $bw.Write([byte[]](0x21, 0xFF, 0x0B)); $bw.Write([Text.Encoding]::ASCII.GetBytes('NETSCAPE2.0')); $bw.Write([byte[]](3, 1, 0, 0, 0))
            $primeiro = $false
        }
        $bw.Write([byte[]](0x21, 0xF9, 4, 4)); $bw.Write([uint16]$parte.Atraso); $bw.Write([byte[]](0, 0))
        $descritor = New-Object byte[] 10
        [Array]::Copy($g, $pos, $descritor, 0, 10)
        $pos += 10
        # o pedaço vai para o lugar dele no quadro inteiro
        $descritor[1] = [byte]($parte.X -band 0xFF); $descritor[2] = [byte]($parte.X -shr 8)
        $descritor[3] = [byte]($parte.Y -band 0xFF); $descritor[4] = [byte]($parte.Y -shr 8)
        if ($descritor[9] -band 0x80) {
            $tamLocal = 3 * (1 -shl (($descritor[9] -band 7) + 1))
            $bw.Write($descritor); $bw.Write($g, $pos, $tamLocal); $pos += $tamLocal
        }
        else {
            $descritor[9] = [byte](($descritor[9] -band 0x40) -bor 0x80 -bor ($pack -band 7))   # a tabela global vira local
            $bw.Write($descritor); $bw.Write($tabela)
        }
        $inicioDados = $pos
        $pos++
        while ($g[$pos] -ne 0) { $pos += $g[$pos] + 1 }
        $pos++
        $bw.Write($g, $inicioDados, $pos - $inicioDados)
    }
    $bw.Write([byte]0x3B)
    $bw.Close()
}

function Export-Previa([string]$pasta) {
    New-Item -ItemType Directory -Force $pasta | Out-Null
    Set-Uso 42 68 'renova às 15:40' 'renova sáb 14:00' 'Atualizado às 10:32'
    $estado.Claro = $false
    Update-Balao

    # Cena: área de trabalho, barra de tarefas com uns apps no meio e o widget no canto
    $largura = 460; $alturaCena = 270; $topoBarra = $alturaCena - 48
    $cena = New-Object Windows.Controls.Canvas
    $cena.Background = New-Pincel '#14161A'
    $barraFalsa = New-Object Windows.Controls.Border
    $barraFalsa.Width = $largura; $barraFalsa.Height = 48; $barraFalsa.Background = New-Pincel '#202020'
    $barraFalsa.BorderBrush = New-Pincel '#2E2E2E'; $barraFalsa.BorderThickness = New-Object Windows.Thickness(0, 1, 0, 0)
    [Windows.Controls.Canvas]::SetTop($barraFalsa, $topoBarra)
    [void]$cena.Children.Add($barraFalsa)
    foreach ($i in 0..4) {
        $app = New-Object Windows.Controls.Border
        $app.Width = 24; $app.Height = 24; $app.CornerRadius = New-Object Windows.CornerRadius(6)
        $app.Background = New-Pincel $(if ($i -eq 0) { '#3B82F6' } else { '#3A3A3A' })
        [Windows.Controls.Canvas]::SetLeft($app, 290 + $i * 34); [Windows.Controls.Canvas]::SetTop($app, $topoBarra + 12)
        [void]$cena.Children.Add($app)
    }
    $w = New-Widget ([IntPtr]::Zero)
    $conteudo = $w.Janela.Content
    $w.Janela.Content = $null
    [Windows.Controls.Canvas]::SetLeft($conteudo, $Margem)
    [Windows.Controls.Canvas]::SetTop($conteudo, $topoBarra + 4 - $folga)
    [void]$cena.Children.Add($conteudo)
    $cartao = $cartaoBalao
    $balao.Content = $null
    $cartao.Visibility = 'Hidden'
    [void]$cena.Children.Add($cartao)
    $w.Janela.Left = $Margem; $w.Janela.Top = $topoBarra + 4 - $folga

    $quadros = New-Object System.Collections.ArrayList
    function Add-Quadro { [void]$quadros.Add((Get-Foto $cena $largura $alturaCena)) }
    function Show-Sequencia($nome) { foreach ($q in $sequencias[$nome]) { Set-Boneco $w.Principal $q; Add-Quadro } }

    Show-Sequencia 'andar'
    Show-Sequencia 'piscar'
    # o visitante chega, o balão aparece e ele vai embora
    $w.Visita = 'entrando'; $w.Visitante.Raiz.Visibility = 'Visible'
    while ($w.Visita -eq 'entrando') {
        Step-Visita $w $true
        if ($w.Visita -eq 'parado') { break }
        Set-Boneco $w.Principal (Q -olhar 2)
        Add-Quadro
    }
    Hide-Balao $w
    Set-Balao (Get-AlvoBalao $w) $w.Janela.Top $null
    [Windows.Controls.Canvas]::SetLeft($cartao, $balao.Left); [Windows.Controls.Canvas]::SetTop($cartao, $balao.Top)
    $cartao.Visibility = 'Visible'
    for ($i = 0; $i -lt 22; $i++) { Step-Visita $w $true; Set-Boneco $w.Principal (Q -olhar 2 -y $(if ($i % 8 -lt 4) { 0 } else { -1 })); Add-Quadro }
    Save-Png $quadros[$quadros.Count - 1] (Join-Path $pasta 'balao.png')
    $cartao.Visibility = 'Hidden'
    $w.Visita = 'saindo'
    while ($w.Visita -ne 'fora') { Step-Visita $w $true; Set-Boneco $w.Principal (Q -olhar 2); Add-Quadro }
    # troca de chapéu a cada pulinho
    foreach ($id in 'cartola', 'coroa', 'festa', 'bruxa', 'natal', 'bone') {
        Set-EstiloBoneco $w.Principal '#D77757' $id ($id -eq 'bone')
        foreach ($q in $sequencias['pular']) { Set-Boneco $w.Principal $q; Add-Quadro }
    }
    Set-EstiloBoneco $w.Principal '#D77757' 'nenhum' $false
    Show-Sequencia 'dancar'
    Save-Gif $quadros (Join-Path $pasta 'demo.gif')

    # Vitrine: os chapéus e as cores
    $vitrine = New-Object Windows.Controls.Canvas
    $vitrine.Background = New-Pincel '#202020'
    $itens = @(foreach ($id in @($chapeus.Keys | Where-Object { $_ -ne 'nenhum' })) { , @($chapeus[$id].Nome, '#D77757', $id, $false) }) + @(, @('Óculos', '#D77757', 'nenhum', $true))
    $itens += @(foreach ($nome in $cores.Keys) { , @(($nome -replace ' Claude', ''), $cores[$nome], 'nenhum', $false) })
    for ($i = 0; $i -lt $itens.Count; $i++) {
        $linha = [math]::Floor($i / 7); $coluna = $i % 7
        $canvasBoneco = [Windows.Markup.XamlReader]::Parse(@"
<Canvas xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="32" Height="20">
$(Get-XamlBoneco 'P')
</Canvas>
"@)
        $boneco = @{ Raiz = $canvasBoneco.FindName('P'); Ultimo = '' }
        foreach ($parte in 'Corpo', 'Pernas', 'OlhoE', 'OlhoD', 'Oculos', 'Chapeu') { $boneco[$parte] = $canvasBoneco.FindName("P$parte") }
        Set-EstiloBoneco $boneco $itens[$i][1] $itens[$i][2] $itens[$i][3]
        [Windows.Controls.Canvas]::SetLeft($canvasBoneco, 20 + $coluna * 74); [Windows.Controls.Canvas]::SetTop($canvasBoneco, 22 + $linha * 70)
        [void]$vitrine.Children.Add($canvasBoneco)
        $rotulo = New-Object Windows.Controls.TextBlock
        $rotulo.Text = $itens[$i][0]; $rotulo.Foreground = New-Pincel '#C8C8C8'; $rotulo.FontSize = 11; $rotulo.Width = 74
        $rotulo.TextAlignment = 'Center'; $rotulo.FontFamily = New-Object Windows.Media.FontFamily('Segoe UI Variable Text, Segoe UI')
        [Windows.Controls.Canvas]::SetLeft($rotulo, -1 + $coluna * 74); [Windows.Controls.Canvas]::SetTop($rotulo, 48 + $linha * 70)
        [void]$vitrine.Children.Add($rotulo)
    }
    Save-Png (Get-Foto $vitrine (7 * 74) 140 3) (Join-Path $pasta 'personalizar.png')
}

$animacao = New-Object Windows.Threading.DispatcherTimer
$animacao.Interval = [TimeSpan]::FromMilliseconds(120)
$animacao.Add_Tick({
        # Respeita a opção do menu e "Efeitos de animação" desligado no Windows
        $mexer = [bool]$config.Movimento -and [Windows.SystemParameters]::ClientAreaAnimation
        if ($mexer) {
            if ($fila.Count -eq 0) { Start-Animacao (Get-Sorteio) }
            $q = $fila.Dequeue()
            foreach ($w in $widgets.Values) {
                $quadro = $q
                # com visita, o mascote olha para o visitante
                if ($w.Visita -ne 'fora') { $quadro = $q.Clone(); $quadro.Olhar = 2 }
                Set-Boneco $w.Principal $quadro
            }
        }
        foreach ($w in $widgets.Values) { Step-Visita $w $mexer }
    })

# --- Posição e ordem na tela ---
$vigia = New-Object Windows.Threading.DispatcherTimer
$vigia.Interval = [TimeSpan]::FromMilliseconds(300)
$vigia.Add_Tick({
        # Clicou em outra janela com o menu aberto: fecha o menu
        if ($script:menuAberto -and $script:menuAberto.IsOpen) {
            $frente = [ClaudeWidget.Win]::GetForegroundWindow()
            $dono = [uint32]0
            [void][ClaudeWidget.Win]::GetWindowThreadProcessId($frente, [ref]$dono)
            if ($frente -ne $script:frenteAoAbrir -and $dono -ne $PID) { $script:menuAberto.IsOpen = $false }
        }
        $barras = @(Get-Barras)
        $chaves = @($barras | ForEach-Object { $_.ToInt64() })
        foreach ($chave in @($widgets.Keys)) {
            if ($chaves -notcontains $chave) { Remove-Widget $chave }
        }
        $telaCheia = Get-MonitorTelaCheia
        foreach ($barra in $barras) {
            $chave = $barra.ToInt64()
            if (-not $widgets.ContainsKey($chave)) { $widgets[$chave] = New-Widget $barra }
            $w = $widgets[$chave]
            if (Update-Posicao $w $telaCheia) {
                if (-not $w.Janela.IsVisible) { $w.Janela.Show() }
            }
            elseif ($w.Janela.IsVisible) {
                Hide-Balao $w
                $w.Visita = 'fora'
                $w.VisitaX = $palco
                $w.Visitante.Raiz.Visibility = 'Hidden'
                $w.Janela.Hide()
            }
        }
        [ClaudeWidget.Win]::Elevar()
    })

$relogio = New-Object Windows.Threading.DispatcherTimer
$relogio.Interval = [TimeSpan]::FromMinutes($IntervaloMinutos)
$relogio.Add_Tick({ Update-Uso })

if ($Previa) {
    Export-Previa $Previa
    return
}

if ($Demonstrar) {
    $demo = New-Object Windows.Threading.DispatcherTimer
    $demo.Interval = [TimeSpan]::FromSeconds(2)
    $demo.Add_Tick({ $demo.Stop(); foreach ($w in $widgets.Values) { Start-Visita $w } })
    $demo.Start()
}

Update-Uso
[ClaudeWidget.Win]::VigiarFrente()
$vigia.Start()
$relogio.Start()
$animacao.Start()
[void]$app.Run()
$mutex.ReleaseMutex()
