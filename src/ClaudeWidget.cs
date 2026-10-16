// Claude Widget: o mascote do Claude Code na barra de tarefas do Windows, com um anel mostrando o % de uso do plano.
// https://github.com/dev-lucccaslp/claude-widget
//
// Compilado com o csc do .NET Framework 4.x que já vem no Windows (por isso C# 5): veja build.ps1.
// Lê o login do Claude Code em %USERPROFILE%\.claude\.credentials.json e só conversa com api.anthropic.com.
// Não renova nem grava o login. As escolhas do menu ficam em %LOCALAPPDATA%\ClaudeWidget\config.json.
//
// Linha de comando: ClaudeWidget.exe [--sair] [--demo]
//   sem nada  abre; se já estiver aberto, só mostra o widget de novo
//   --sair    fecha a cópia aberta (o instalador usa antes de trocar o .exe)
//   --demo    chama o visitante e o balão logo ao abrir, para testar sem mouse

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Shapes = System.Windows.Shapes;

[assembly: AssemblyTitle("Claude Widget")]
[assembly: AssemblyProduct("Claude Widget")]
[assembly: AssemblyDescription("O mascote do Claude Code na barra de tarefas, com o uso do plano")]
[assembly: AssemblyCopyright("MIT - github.com/dev-lucccaslp/claude-widget")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

namespace ClaudeWidget
{
    static class Win32
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public class MONITORINFOEX
        {
            public int cbSize = 104;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        public delegate void WinEventProc(IntPtr hook, uint evento, IntPtr hwnd, int objeto, int filho, uint thread, uint tempo);

        [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
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
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000;
    }

    // Ordem na tela: widgets por cima da barra e o balão por cima de tudo.
    // Com o menu aberto (Pausado) não mexe em nada, senão o widget cobriria o menu.
    static class Ordem
    {
        public static readonly List<IntPtr> Widgets = new List<IntPtr>();
        public static IntPtr Balao = IntPtr.Zero;
        public static bool Pausado;
        static readonly IntPtr SempreVisivel = new IntPtr(-1);

        public static void Elevar()
        {
            if (Pausado) return;
            foreach (var h in Widgets) Win32.SetWindowPos(h, SempreVisivel, 0, 0, 0, 0, 0x0213);
            if (Balao != IntPtr.Zero) Win32.SetWindowPos(Balao, SempreVisivel, 0, 0, 0, 0, 0x0213);
        }
    }

    // --- Escolhas do menu, salvas entre uma abertura e outra ---
    public class Posicao
    {
        public string Modo { get; set; }   // auto, esquerda, direita ou manual
        public string Lado { get; set; }   // manual: medido a partir da esquerda ou da direita da barra
        public double Desvio { get; set; }
    }

    public class Config
    {
        public string Cor { get; set; }
        public string Chapeu { get; set; }
        public bool Oculos { get; set; }
        public bool Movimento { get; set; }
        public bool Visitante { get; set; }
        public string Mostrar { get; set; }  // maior, sessao ou semanal
        public string Transporte { get; set; }  // como o visitante chega: pe, skate, bike, carro ou foguete
        public Dictionary<string, Posicao> Posicoes { get; set; }

        public Config()
        {
            Cor = Arte.Laranja; Chapeu = "nenhum"; Movimento = true; Visitante = true; Mostrar = "maior"; Transporte = "pe";
            Posicoes = new Dictionary<string, Posicao>();
        }

        public static string Pasta { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeWidget"); } }
        static string Arquivo { get { return System.IO.Path.Combine(Pasta, "config.json"); } }

        public static Config Carregar()
        {
            try
            {
                var c = new JavaScriptSerializer().Deserialize<Config>(File.ReadAllText(Arquivo));
                if (c.Posicoes == null) c.Posicoes = new Dictionary<string, Posicao>();
                if (string.IsNullOrEmpty(c.Cor)) c.Cor = Arte.Laranja;
                if (string.IsNullOrEmpty(c.Chapeu)) c.Chapeu = "nenhum";
                if (c.Mostrar != "sessao" && c.Mostrar != "semanal") c.Mostrar = "maior";
                c.Transporte = Arte.AcharTransporte(c.Transporte).Id;
                return c;
            }
            catch { return new Config(); }
        }

        public void Salvar()
        {
            try
            {
                Directory.CreateDirectory(Pasta);
                File.WriteAllText(Arquivo, new JavaScriptSerializer().Serialize(this), new UTF8Encoding(false));
            }
            catch { }
        }

        public Posicao PosicaoDe(string monitor)
        {
            Posicao p;
            if (monitor != null && Posicoes.TryGetValue(monitor, out p) && p != null && p.Modo != null) return p;
            return new Posicao { Modo = "auto" };
        }
    }

    // --- Uso do plano ---
    class Uso
    {
        public int Sessao, Semana;
        public string RenovaSessao = "", RenovaSemana = "", Hora = "Carregando…";
        public string Erro;
    }

    static class ApiUso
    {
        static readonly CultureInfo ptBR = new CultureInfo("pt-BR");

        public static string Credenciais
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json"); }
        }

        public static Uso Buscar()
        {
            var uso = new Uso();
            try
            {
                if (!File.Exists(Credenciais)) { uso.Erro = "Entre no Claude Code para ver o uso"; return uso; }
                var js = new JavaScriptSerializer();
                var cred = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(Credenciais));
                var oauth = cred["claudeAiOauth"] as Dictionary<string, object>;
                var token = oauth["accessToken"] as string;

                var req = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/api/oauth/usage");
                req.Headers["Authorization"] = "Bearer " + token;
                req.Headers["anthropic-beta"] = "oauth-2025-04-20";
                req.UserAgent = "claude-widget";
                req.Timeout = 15000;
                string json;
                using (var resp = req.GetResponse())
                using (var leitor = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    json = leitor.ReadToEnd();

                var u = js.Deserialize<Dictionary<string, object>>(json);
                uso.Sessao = Pct(u, "five_hour");
                uso.Semana = Pct(u, "seven_day");
                uso.RenovaSessao = Renova(u, "five_hour", "'às' HH:mm");
                uso.RenovaSemana = Renova(u, "seven_day", "ddd HH:mm");
                uso.Hora = "Atualizado às " + DateTime.Now.ToString("HH:mm");
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                uso.Erro = r != null && r.StatusCode == HttpStatusCode.Unauthorized
                    ? "Login expirado: abra o Claude Code"
                    : "Sem resposta (" + DateTime.Now.ToString("HH:mm") + ")";
            }
            catch { uso.Erro = "Sem resposta (" + DateTime.Now.ToString("HH:mm") + ")"; }
            return uso;
        }

        static Dictionary<string, object> Limite(Dictionary<string, object> u, string nome)
        {
            object o;
            return u.TryGetValue(nome, out o) ? o as Dictionary<string, object> : null;
        }

        static int Pct(Dictionary<string, object> u, string nome)
        {
            var l = Limite(u, nome);
            object v;
            if (l == null || !l.TryGetValue("utilization", out v) || v == null) return 0;
            return (int)Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture));
        }

        static string Renova(Dictionary<string, object> u, string nome, string formato)
        {
            var l = Limite(u, nome);
            object v;
            DateTimeOffset quando;
            if (l == null || !l.TryGetValue("resets_at", out v) || v == null) return "";
            if (!DateTimeOffset.TryParse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out quando)) return "";
            return "renova " + quando.LocalDateTime.ToString(formato, ptBR);
        }
    }

    // --- Desenho: cores, chapéus, pernas e os movimentos ---
    class Quadro
    {
        public int X, Y, Olhar;
        public bool Fechado;
        public string Pe = "N";
        public Quadro Copia() { return (Quadro)MemberwiseClone(); }
        public string Chave() { return X + "|" + Y + "|" + Olhar + "|" + Fechado + "|" + Pe; }
    }

    class Roteiro : List<Quadro>
    {
        public Roteiro Q(int x = 0, int y = 0, int olhar = 0, bool fechado = false, string pe = "N", int n = 1)
        {
            for (int i = 0; i < n; i++) Add(new Quadro { X = x, Y = y, Olhar = olhar, Fechado = fechado, Pe = pe });
            return this;
        }
    }

    class Chapeu
    {
        public string Id, Nome;
        public string[][] Partes;  // cor + desenho, em pixel art acima da cabeça (y negativo)
    }

    // Uma peça do transporte. Camada: atras ou frente do boneco. Quadro: "" sempre; "A"/"B" se revezam andando
    // (rodas girando) e ficam no "A" parado; "MA"/"MB" só aparecem andando (fumaça, chama, risquinhos).
    class Peca
    {
        public string Cor, Desenho, Camada = "frente", Quadro = "";
        public double Traco;  // > 0: desenha só o contorno, com essa espessura
    }

    // Como o visitante chega. Coordenadas no mesmo sistema do boneco (corpo de 0,0 a 32,20).
    class Transporte
    {
        public string Id, Nome;
        public double Velocidade, Parada, AssentoY;
        public string Pernas = "andar";  // andar, pedalar, paradas ou encolhidas
        public bool Balanca;             // sobe e desce enquanto anda
        public Peca[] Pecas = new Peca[0];
    }

    static class Arte
    {
        public const string Laranja = "#D77757";
        static readonly BrushConverter conversor = new BrushConverter();
        static readonly Dictionary<string, Brush> pinceis = new Dictionary<string, Brush>();

        public static Brush Pincel(string hex)
        {
            Brush b;
            if (!pinceis.TryGetValue(hex, out b))
            {
                b = (Brush)conversor.ConvertFromString(hex);
                b.Freeze();
                pinceis[hex] = b;
            }
            return b;
        }

        public static string CorDoUso(double pct) { return pct >= 90 ? "#E5484D" : pct >= 75 ? "#E8B339" : Laranja; }

        public static readonly string[][] Cores =
        {
            new[] { "Laranja Claude", "#D77757" }, new[] { "Azul", "#4F8EF7" }, new[] { "Verde", "#3FB950" }, new[] { "Roxo", "#A371F7" },
            new[] { "Rosa", "#F778BA" }, new[] { "Dourado", "#E3B341" }, new[] { "Cinza", "#9DA5B4" }
        };

        public static readonly Chapeu[] Chapeus =
        {
            new Chapeu { Id = "nenhum", Nome = "Nenhum", Partes = new string[0][] },
            new Chapeu { Id = "bone", Nome = "Boné", Partes = new[] {
                new[] { "#2F6FEB", "M8,-6 H22 V-4 H24 V0 H6 V-4 H8 Z" }, new[] { "#1F4FB8", "M22,-2 H32 V0 H22 Z" }, new[] { "#FFFFFF", "M12,-4 h2 v2 h-2 Z" } } },
            new Chapeu { Id = "cartola", Nome = "Cartola", Partes = new[] {
                new[] { "#4A4A4A", "M8,-10 H24 V-2 H8 Z" }, new[] { "#E5484D", "M8,-4 H24 V-2 H8 Z" }, new[] { "#5C5C5C", "M4,-2 H28 V0 H4 Z" } } },
            new Chapeu { Id = "festa", Nome = "Festa", Partes = new[] {
                new[] { "#EC4899", "M10,-2 H22 V0 H10 Z M13,-6 H19 V-4 H13 Z" }, new[] { "#FACC15", "M12,-4 H20 V-2 H12 Z M15,-8 H17 V-6 H15 Z" },
                new[] { "#FFFFFF", "M14,-10 H18 V-8 H14 Z" } } },
            new Chapeu { Id = "coroa", Nome = "Coroa", Partes = new[] {
                new[] { "#F5C542", "M6,-4 H26 V0 H6 Z M6,-8 H9 V-4 H6 Z M14,-9 H18 V-4 H14 Z M23,-8 H26 V-4 H23 Z" },
                new[] { "#E5484D", "M15,-3 H17 V-1 H15 Z" }, new[] { "#4F8EF7", "M9,-3 H11 V-1 H9 Z M21,-3 H23 V-1 H21 Z" } } },
            new Chapeu { Id = "bruxa", Nome = "Bruxa", Partes = new[] {
                new[] { "#6D28D9", "M2,-2 H30 V0 H2 Z M10,-6 H22 V-4 H10 Z M12,-8 H20 V-6 H12 Z M16,-10 H22 V-8 H16 Z" },
                new[] { "#22C55E", "M8,-4 H24 V-2 H8 Z" } } },
            new Chapeu { Id = "natal", Nome = "Natal", Partes = new[] {
                new[] { "#DC2626", "M6,-6 H24 V-2 H6 Z M12,-8 H26 V-6 H12 Z" }, new[] { "#F5F5F5", "M4,-2 H28 V0 H4 Z M26,-10 H30 V-6 H26 Z" } } },
        };

        static Peca P(string cor, string desenho, string camada = "frente", string quadro = "", double traco = 0)
        {
            return new Peca { Cor = cor, Desenho = desenho, Camada = camada, Quadro = quadro, Traco = traco };
        }

        public static readonly Transporte[] Transportes =
        {
            new Transporte { Id = "pe", Nome = "A pé", Velocidade = 6, Parada = 8, Balanca = true },
            new Transporte { Id = "skate", Nome = "Skate", Velocidade = 8, Parada = 10, Pernas = "paradas", Pecas = new[] {
                P("#F59E0B", "M-2,20 H34 V22 H-2 Z M-4,18 H-2 V20 H-4 Z M34,18 H36 V20 H34 Z", "atras"),
                P("#9CA3AF", "M4,22 h4 v1 h-4 Z M24,22 h4 v1 h-4 Z", "atras"),
                P("#F3F4F6", "M3,23 h3 v3 h-3 Z M26,23 h3 v3 h-3 Z", "atras"),
                P("#9CA3AF", "M38,6 h6 v1 h-6 Z M40,12 h6 v1 h-6 Z", "atras", "MA"),
                P("#9CA3AF", "M37,9 h6 v1 h-6 Z M39,15 h5 v1 h-5 Z", "atras", "MB") } },
            new Transporte { Id = "bike", Nome = "Bicicleta", Velocidade = 7, Parada = 12, AssentoY = -4, Pernas = "pedalar", Pecas = new[] {
                P("#E5E7EB", "M-3,22 A5,5 0 1 0 7,22 A5,5 0 1 0 -3,22 M25,22 A5,5 0 1 0 35,22 A5,5 0 1 0 25,22", "atras", "", 2),
                P("#38BDF8", "M30,22 L18,15 L5,15 L2,22 M18,15 L14,22 L30,22 M5,15 L4,10 L0,10", "atras", "", 2),
                P("#9CA3AF", "M2,18 V26 M-2,22 H6 M30,18 V26 M26,22 H34", "atras", "A", 1),
                P("#9CA3AF", "M-1,19 L5,25 M5,19 L-1,25 M27,19 L33,25 M33,19 L27,25", "atras", "B", 1) } },
            new Transporte { Id = "carro", Nome = "Carro", Velocidade = 10, Parada = 12, Pernas = "paradas", Balanca = true, Pecas = new[] {
                P("#93C5FD", "M-3,4 H-1 V12 H-3 Z", "atras"),
                P("#E5484D", "M-8,12 H40 V21 H-8 Z M-10,15 H-8 V21 H-10 Z M40,14 H42 V21 H40 Z"),
                P("#B91C1C", "M14,13 h1 v7 h-1 Z"),
                P("#FDE68A", "M-10,15 h3 v2 h-3 Z"),
                P("#7F1D1D", "M40,15 h2 v2 h-2 Z"),
                P("#1F1F1F", "M-5,19 h8 v7 h-8 Z M25,19 h8 v7 h-8 Z"),
                P("#9CA3AF", "M-2,21 h2 v3 h-2 Z M28,21 h2 v3 h-2 Z", "frente", "A"),
                P("#9CA3AF", "M-3,22 h4 v1 h-4 Z M27,22 h4 v1 h-4 Z", "frente", "B"),
                P("#9CA3AF", "M44,18 h3 v3 h-3 Z", "frente", "MA"),
                P("#9CA3AF", "M46,15 h2 v2 h-2 Z M43,19 h2 v2 h-2 Z", "frente", "MB") } },
            new Transporte { Id = "foguete", Nome = "Foguete", Velocidade = 12, Parada = 14, AssentoY = -6, Pernas = "encolhidas", Balanca = true, Pecas = new[] {
                P("#E5E7EB", "M0,12 H32 V21 H0 Z"),
                P("#E5484D", "M-4,13 H0 V20 H-4 Z M-7,15 H-4 V18 H-7 Z M28,8 H34 V12 H28 Z M28,21 H34 V25 H28 Z"),
                P("#60A5FA", "M8,14 h4 v4 h-4 Z"),
                P("#9CA3AF", "M18,12 h1 v9 h-1 Z"),
                P("#F97316", "M32,13 H40 V20 H32 Z", "frente", "MA"),
                P("#FACC15", "M40,14 H45 V19 H40 Z", "frente", "MA"),
                P("#F97316", "M32,14 H38 V19 H32 Z", "frente", "MB"),
                P("#FACC15", "M38,15 H42 V18 H38 Z", "frente", "MB") } },
        };

        public static Transporte AcharTransporte(string id)
        {
            return Transportes.FirstOrDefault(t => t.Id == id) ?? Transportes[0];
        }

        public static readonly Dictionary<string, Geometry> Pernas = new Dictionary<string, Geometry>
        {
            { "N", FazerPernas(4, 4, 4, 4) },   // paradas
            { "A", FazerPernas(2, 4, 2, 4) },   // passo com um par
            { "B", FazerPernas(4, 2, 4, 2) },   // passo com o outro par
            { "T", FazerPernas(2, 2, 2, 2) },   // encolhidas no pulo
        };

        static Geometry FazerPernas(params int[] alturas)
        {
            int[] x = { 6, 10, 20, 24 };
            var partes = new List<string>();
            for (int i = 0; i < 4; i++) partes.Add("M" + x[i] + ",16 h2 v" + alturas[i] + " h-2 Z");
            var g = Geometry.Parse(string.Join(" ", partes));
            g.Freeze();
            return g;
        }

        public static readonly Dictionary<string, List<Quadro>> Sequencias = CriarSequencias();

        static Dictionary<string, List<Quadro>> CriarSequencias()
        {
            var s = new Dictionary<string, List<Quadro>>();
            s["piscar"] = new Roteiro().Q(fechado: true).Q(n: 2);
            s["respirar"] = new Roteiro().Q(n: 3).Q(y: -1, n: 3).Q(n: 3).Q(y: -1, n: 3).Q(fechado: true).Q(n: 2);
            s["olhar"] = new Roteiro().Q(olhar: -2, n: 6).Q(olhar: -2, fechado: true).Q(olhar: -2, n: 3).Q(n: 2).Q(olhar: 2, n: 8).Q();
            // anda para a esquerda, volta, vai para a direita e volta, olhando para onde vai
            var andar = new Roteiro();
            foreach (int lado in new[] { -1, 1 })
                andar.Q(x: lado, olhar: lado * 2, pe: "A", n: 2).Q(x: 2 * lado, y: -1, olhar: lado * 2, pe: "B", n: 2)
                     .Q(x: 3 * lado, olhar: lado * 2, pe: "A", n: 2).Q(x: 2 * lado, y: -1, olhar: -lado * 2, pe: "B", n: 2)
                     .Q(x: lado, olhar: -lado * 2, pe: "A", n: 2).Q(y: -1, pe: "B", n: 2);
            s["andar"] = andar.Q();
            var pular = new Roteiro();
            for (int i = 0; i < 2; i++) pular.Q(y: -1).Q(y: -3, pe: "T", fechado: true, n: 2).Q(y: -1).Q();
            s["pular"] = pular.Q();
            var dancar = new Roteiro();
            for (int i = 0; i < 3; i++) dancar.Q(x: -2, y: -2, olhar: -2, pe: "T").Q(x: -2, pe: "A", n: 2).Q(x: 2, y: -2, olhar: 2, pe: "T").Q(x: 2, pe: "B", n: 2);
            s["dancar"] = dancar.Q();
            return s;
        }

        // Peso de cada movimento no sorteio (soma 100); sempre emenda um no outro
        public static readonly string[] Movimentos = { "andar", "respirar", "olhar", "pular", "dancar", "piscar" };
        public static readonly int[] Pesos = { 30, 25, 15, 10, 10, 10 };

        // Mascote: o mesmo desenho em blocos da tela de boas-vindas do Claude Code (pixel 2x4)
        public static string XamlBoneco(string p)
        {
            // Atras e Frente guardam o transporte do visitante (o skate fica atrás, o carro na frente das pernas);
            // Espelho vira o boneco para ele ir embora de frente; Assento levanta ele na bicicleta e no foguete
            return (@"
<Canvas x:Name='§' Width='32' Height='20'>
  <Canvas.RenderTransform>
    <TransformGroup><ScaleTransform CenterX='16'/><TranslateTransform/></TransformGroup>
  </Canvas.RenderTransform>
  <Canvas x:Name='§Atras'/>
  <Canvas x:Name='§Corpinho'>
    <Canvas.RenderTransform><TranslateTransform/></Canvas.RenderTransform>
    <Path x:Name='§Corpo' Data='M4,0 H28 V8 H32 V12 H28 V16 H4 V12 H0 V8 H4 Z'/>
    <Path x:Name='§Pernas' Data='M6,16 h2 v4 h-2 Z M10,16 h2 v4 h-2 Z M20,16 h2 v4 h-2 Z M24,16 h2 v4 h-2 Z'/>
    <Rectangle x:Name='§OlhoE' Canvas.Left='8' Canvas.Top='4' Width='2' Height='4' Fill='#141413'/>
    <Rectangle x:Name='§OlhoD' Canvas.Left='22' Canvas.Top='4' Width='2' Height='4' Fill='#141413'/>
    <Path x:Name='§Oculos' Fill='#0F0F0F' Visibility='Collapsed' Data='M6,3 h6 v5 h-6 Z M20,3 h6 v5 h-6 Z M12,4 h8 v2 h-8 Z'/>
    <Canvas x:Name='§Chapeu'/>
  </Canvas>
  <Canvas x:Name='§Frente'/>
</Canvas>").Replace("§", p);
        }

        public const string XamlWidget = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        WindowStyle='None' AllowsTransparency='True' Background='Transparent' Topmost='True' ShowInTaskbar='False'
        ShowActivated='False' Focusable='False' ResizeMode='NoResize' SizeToContent='WidthAndHeight' UseLayoutRounding='True'>
  <StackPanel Orientation='Horizontal' Margin='0,6,0,0'>
    <Border x:Name='Fundo' CornerRadius='5' Height='40' Padding='8,0,8,0' Cursor='Hand'>
      <Border.Style>
        <Style TargetType='Border'>
          <Setter Property='Background' Value='#01FFFFFF'/>
          <Style.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter Property='Background' Value='#1AFFFFFF'/></Trigger>
          </Style.Triggers>
        </Style>
      </Border.Style>
      <StackPanel Orientation='Horizontal' VerticalAlignment='Center'>
        <Canvas x:Name='AreaMascote' Width='32' Height='20' Background='Transparent' VerticalAlignment='Center' SnapsToDevicePixels='True'>§M§</Canvas>
        <Grid Width='32' Height='32' Margin='8,0,0,0' VerticalAlignment='Center'>
          <Ellipse x:Name='Trilho' StrokeThickness='3' Stroke='#3DFFFFFF'/>
          <Path x:Name='Arco' StrokeThickness='3' StrokeStartLineCap='Round' StrokeEndLineCap='Round' Stroke='#D77757'/>
          <TextBlock x:Name='Texto' Text='…' Foreground='White' FontSize='10' FontWeight='SemiBold'
                     FontFamily='Segoe UI Variable Display, Segoe UI' HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Grid>
      </StackPanel>
    </Border>
    <Canvas Width='56' Height='46' Margin='0,-6,0,0' ClipToBounds='True' IsHitTestVisible='False' SnapsToDevicePixels='True'>§V§</Canvas>
  </StackPanel>
</Window>";

        // Balão com o uso: janela própria que fica acima dos widgets e deixa o mouse passar
        public const string XamlBalao = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        WindowStyle='None' AllowsTransparency='True' Background='Transparent' Topmost='True' ShowInTaskbar='False'
        ShowActivated='False' Focusable='False' IsHitTestVisible='False' ResizeMode='NoResize' SizeToContent='WidthAndHeight'
        UseLayoutRounding='True' FontFamily='Segoe UI Variable Text, Segoe UI'>
  <Grid Margin='8'>
    <Grid.Effect><DropShadowEffect BlurRadius='10' ShadowDepth='2' Opacity='0.5' Color='Black'/></Grid.Effect>
    <StackPanel>
      <Border Background='#2B2B2B' BorderBrush='#454545' BorderThickness='1' CornerRadius='8' Padding='12,10,12,10' Width='232'>
        <StackPanel>
          <TextBlock Text='Uso do Claude' Foreground='#F3F3F3' FontSize='13' FontWeight='SemiBold'/>
          <Grid Margin='0,10,0,0'>
            <TextBlock Text='Sessão (5h)' Foreground='#D6D6D6' FontSize='12'/>
            <TextBlock x:Name='PctSessao' HorizontalAlignment='Right' Foreground='#F3F3F3' FontSize='12' FontWeight='SemiBold'/>
          </Grid>
          <Border Height='4' CornerRadius='2' Background='#40FFFFFF' Margin='0,5,0,0'>
            <Border x:Name='BarraSessao' HorizontalAlignment='Left' CornerRadius='2' Width='0'/>
          </Border>
          <TextBlock x:Name='RenovaSessao' Foreground='#9A9A9A' FontSize='11' Margin='0,4,0,0'/>
          <Grid Margin='0,10,0,0'>
            <TextBlock Text='Semanal' Foreground='#D6D6D6' FontSize='12'/>
            <TextBlock x:Name='PctSemana' HorizontalAlignment='Right' Foreground='#F3F3F3' FontSize='12' FontWeight='SemiBold'/>
          </Grid>
          <Border Height='4' CornerRadius='2' Background='#40FFFFFF' Margin='0,5,0,0'>
            <Border x:Name='BarraSemana' HorizontalAlignment='Left' CornerRadius='2' Width='0'/>
          </Border>
          <TextBlock x:Name='RenovaSemana' Foreground='#9A9A9A' FontSize='11' Margin='0,4,0,0'/>
          <TextBlock x:Name='Rodape' Foreground='#9A9A9A' FontSize='11' Margin='0,10,0,0' TextWrapping='Wrap'/>
          <TextBlock Text='Anel: atualizar · Bonequinho: personalizar' Foreground='#7A7A7A' FontSize='10.5' Margin='0,2,0,0'/>
        </StackPanel>
      </Border>
      <Path x:Name='Bico' Data='M0,0 L8,8 L16,0' Fill='#2B2B2B' Stroke='#454545' StrokeThickness='1' HorizontalAlignment='Left' Margin='24,-1,0,0'/>
    </StackPanel>
  </Grid>
</Window>";
    }

    // Um mascote (o principal ou o visitante): aplica quadros de animação e o estilo escolhido
    class Boneco
    {
        public Canvas Raiz, Chapeu, Atras, Frente;
        public TranslateTransform Pulo, Assento;
        public ScaleTransform Espelho;
        public Shapes.Path Corpo, Pernas, Oculos;
        public Shapes.Rectangle OlhoE, OlhoD;
        string ultimo = "", transporte = "";
        readonly List<KeyValuePair<Shapes.Path, string>> pecas = new List<KeyValuePair<Shapes.Path, string>>();

        public Boneco(FrameworkElement escopo, string p)
        {
            Raiz = (Canvas)escopo.FindName(p);
            var transformacoes = (TransformGroup)Raiz.RenderTransform;
            Espelho = (ScaleTransform)transformacoes.Children[0];
            Pulo = (TranslateTransform)transformacoes.Children[1];
            Atras = (Canvas)escopo.FindName(p + "Atras");
            Frente = (Canvas)escopo.FindName(p + "Frente");
            Assento = (TranslateTransform)((Canvas)escopo.FindName(p + "Corpinho")).RenderTransform;
            Corpo = (Shapes.Path)escopo.FindName(p + "Corpo");
            Pernas = (Shapes.Path)escopo.FindName(p + "Pernas");
            Oculos = (Shapes.Path)escopo.FindName(p + "Oculos");
            OlhoE = (Shapes.Rectangle)escopo.FindName(p + "OlhoE");
            OlhoD = (Shapes.Rectangle)escopo.FindName(p + "OlhoD");
            Chapeu = (Canvas)escopo.FindName(p + "Chapeu");
        }

        public void Aplicar(Quadro q)
        {
            var chave = q.Chave();
            if (chave == ultimo) return;
            ultimo = chave;
            Pulo.X = q.X;
            Pulo.Y = q.Y;
            Pernas.Data = Arte.Pernas[q.Pe];
            double altura = q.Fechado ? 1 : 4, topo = q.Fechado ? 6 : 4;
            Canvas.SetLeft(OlhoE, 8 + q.Olhar); Canvas.SetTop(OlhoE, topo); OlhoE.Height = altura;
            Canvas.SetLeft(OlhoD, 22 + q.Olhar); Canvas.SetTop(OlhoD, topo); OlhoD.Height = altura;
        }

        public void Estilo(string cor, string chapeuId, bool oculos)
        {
            var pincel = Arte.Pincel(cor);
            Corpo.Fill = pincel;
            Pernas.Fill = pincel;
            Oculos.Visibility = oculos ? Visibility.Visible : Visibility.Collapsed;
            Chapeu.Children.Clear();
            var chapeu = Arte.Chapeus.FirstOrDefault(c => c.Id == chapeuId);
            if (chapeu == null) return;
            foreach (var parte in chapeu.Partes)
                Chapeu.Children.Add(new Shapes.Path { Fill = Arte.Pincel(parte[0]), Data = Geometry.Parse(parte[1]) });
        }

        // Monta as peças do transporte (só refaz quando ele muda)
        public void Montar(Transporte t)
        {
            Assento.Y = t.AssentoY;
            if (transporte == t.Id) return;
            transporte = t.Id;
            Atras.Children.Clear();
            Frente.Children.Clear();
            pecas.Clear();
            foreach (var peca in t.Pecas)
            {
                var pincel = Arte.Pincel(peca.Cor);
                var forma = new Shapes.Path { Data = Geometry.Parse(peca.Desenho), SnapsToDevicePixels = true };
                if (peca.Traco > 0) { forma.Stroke = pincel; forma.StrokeThickness = peca.Traco; }
                else forma.Fill = pincel;
                (peca.Camada == "atras" ? Atras : Frente).Children.Add(forma);
                pecas.Add(new KeyValuePair<Shapes.Path, string>(forma, peca.Quadro));
            }
        }

        // Rodas girando e chama/fumaça piscando enquanto anda; parado fica no quadro "A" e sem chama
        public void Fase(bool andando, bool alterna)
        {
            string fase = andando && alterna ? "B" : "A";
            foreach (var p in pecas)
            {
                bool ver = p.Value == "" || p.Value == fase || (andando && p.Value == "M" + fase);
                p.Key.Visibility = ver ? Visibility.Visible : Visibility.Hidden;
            }
        }

        // Vira para a direita (indo embora) ou volta para a esquerda
        public void Virar(bool paraDireita) { Espelho.ScaleX = paraDireita ? -1 : 1; }
    }

    // Onde estão o Iniciar, os Widgets, os apps e a bandeja numa barra (via UI Automation)
    class Layout
    {
        public List<double[]> Ocupados = new List<double[]>();
        public double? Bandeja, Inicio;
        public DateTime Quando;
    }

    class Widget
    {
        public IntPtr Barra, Hwnd;
        public Window Janela;
        public Border Fundo;
        public Canvas AreaMascote;
        public Shapes.Ellipse Trilho;
        public Shapes.Path Arco;
        public TextBlock Texto;
        public Boneco Principal, Visitante;
        public string Visita = "fora";
        public double VisitaX;
        public int Passo;
        public ContextMenu Menu;
        public MenuItem MiSessao, MiSemana, MiHora;
        public List<KeyValuePair<MenuItem, Func<bool>>> Marcas = new List<KeyValuePair<MenuItem, Func<bool>>>();
        public string Monitor;
        public double TelaE = double.NaN, TelaD, LimE = double.NaN, LimD;
        public Layout Layout;
        public string RectBarra = "";
        public bool BuscandoLayout, Arrastando;
    }

    class Arrasto
    {
        public Widget W;
        public double X0, L0;
        public bool Movendo;
    }

    class Controle
    {
        // Palco: área à direita do anel por onde o visitante chega; Fora: onde ele começa e termina, fora da vista
        const double Altura = 40, Folga = 6, Palco = 56, Fora = Palco + 12, LarguraBarra = 206, Margem = 12;
        const int IntervaloMinutos = 5;
        static readonly string[] ClassesDaShell = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };
        static readonly CultureInfo inv = CultureInfo.InvariantCulture;

        readonly Application app;
        readonly Config config = Config.Carregar();
        readonly Dictionary<long, Widget> widgets = new Dictionary<long, Widget>();
        readonly Queue<Quadro> fila = new Queue<Quadro>();
        readonly Random sorteio = new Random();
        readonly double escala = 1;
        readonly uint pid = (uint)Process.GetCurrentProcess().Id;
        readonly Win32.WinEventProc procFrente;

        Uso uso = new Uso();
        bool buscando, claro, escondido;
        int pct;
        string corAnel = Arte.Laranja, textoAnel = "…";
        double fonteAnel = 10;

        Window balao;
        FrameworkElement cartao;
        TextBlock pctSessao, pctSemana, renovaSessao, renovaSemana, rodape;
        Border barraSessao, barraSemana;
        Shapes.Path bico;
        Widget donoBalao;

        ContextMenu menuAberto;
        IntPtr frenteAoAbrir;
        Arrasto arrasto;
        string transporteDemo;  // no --demo, troca de transporte a cada visita sem mexer na escolha salva

        Forms.NotifyIcon bandeja;
        IntPtr iconeBandeja;
        DispatcherTimer animacao, vigia, relogio;

        public Controle(Application app, bool demo)
        {
            this.app = app;
            try { escala = Win32.GetDpiForSystem() / 96.0; } catch { }

            CriarBalao();
            CriarBandeja();

            animacao = NovoTimer(120, TicAnimacao);
            vigia = NovoTimer(300, Vigiar);
            relogio = NovoTimer(IntervaloMinutos * 60000, AtualizarUso);

            // Quando a barra (ou qualquer janela) vem para a frente, reorganiza na hora, sem esperar o timer
            procFrente = delegate { Ordem.Elevar(); };
            Win32.SetWinEventHook(3, 3, IntPtr.Zero, procFrente, 0, 0, 0);

            AtualizarUso();
            Vigiar();
            if (demo)
            {
                // Passa por todos os transportes, um depois do outro, no primeiro widget (o balão é um só)
                int proximo = 0;
                DateTime parouEm = DateTime.MinValue;
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                t.Tick += delegate
                {
                    t.Interval = TimeSpan.FromMilliseconds(300);
                    if (widgets.Count == 0) return;
                    var w = widgets.Values.First();
                    if (w.Visita == "fora")
                    {
                        transporteDemo = Arte.Transportes[proximo++ % Arte.Transportes.Length].Id;
                        IniciarVisita(w);
                    }
                    else if (w.Visita == "parado")
                    {
                        if (parouEm == DateTime.MinValue) parouEm = DateTime.Now;
                        else if ((DateTime.Now - parouEm).TotalSeconds > 2.5) { parouEm = DateTime.MinValue; PararVisita(w); }
                    }
                };
                t.Start();
            }
        }

        static DispatcherTimer NovoTimer(int ms, Action acao)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += delegate { acao(); };
            t.Start();
            return t;
        }

        // --- Mostrar, esconder, sair ---
        public void Mostrar()
        {
            escondido = false;
            Vigiar();
            Animar("pular");
        }

        public void Esconder(bool esconder)
        {
            if (!esconder) { Mostrar(); return; }
            escondido = true;
            if (menuAberto != null) menuAberto.IsOpen = false;
            EsconderBalao(null);
            foreach (var w in widgets.Values) { PararVisita(w); w.Janela.Hide(); }
        }

        public void Sair()
        {
            if (bandeja != null) { bandeja.Visible = false; bandeja.Dispose(); bandeja = null; }
            app.Shutdown();
        }

        // --- Uso ---
        void AtualizarUso()
        {
            claro = TemaClaro();
            if (buscando) return;
            buscando = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var novo = ApiUso.Buscar();
                app.Dispatcher.BeginInvoke(new Action(delegate { buscando = false; AplicarUso(novo); }));
            });
        }

        void AplicarUso(Uso novo)
        {
            if (novo.Erro == null)
            {
                uso = novo;
                relogio.Interval = TimeSpan.FromMinutes(IntervaloMinutos);
            }
            else
            {
                // mantém os últimos números no balão e avisa no rodapé; logo depois de ligar o PC a rede pode não estar pronta
                uso.Hora = novo.Erro;
                uso.Erro = novo.Erro;
                relogio.Interval = TimeSpan.FromMinutes(1);
            }
            MostrarUso();
        }

        // Redesenha o anel, o menu, o balão e o ícone com o uso atual e o limite escolhido em "Mostrar %"
        void MostrarUso()
        {
            if (uso.Erro == null)
            {
                pct = config.Mostrar == "sessao" ? uso.Sessao : config.Mostrar == "semanal" ? uso.Semana : Math.Max(uso.Sessao, uso.Semana);
                corAnel = Arte.CorDoUso(pct);
                textoAnel = pct + "%";
                fonteAnel = pct >= 100 ? 8.5 : 10;
            }
            else
            {
                pct = 0;
                textoAnel = "?";
                fonteAnel = 12;
            }
            foreach (var w in widgets.Values) MostrarEstado(w);
            if (bandeja != null)
            {
                var dica = uso.Erro != null ? "Claude Widget · " + uso.Erro : "Claude · 5h " + uso.Sessao + "% · semana " + uso.Semana + "%";
                bandeja.Text = dica.Length > 63 ? dica.Substring(0, 63) : dica;
            }
            if (donoBalao != null) MostrarBalao(donoBalao);
        }

        string LinhaSessao() { return "Sessão (5h): " + uso.Sessao + "%" + (uso.RenovaSessao != "" ? " · " + uso.RenovaSessao : ""); }
        string LinhaSemana() { return "Semanal: " + uso.Semana + "%" + (uso.RenovaSemana != "" ? " · " + uso.RenovaSemana : ""); }

        void MostrarEstado(Widget w)
        {
            w.Arco.Data = Arco(pct);
            w.Arco.Stroke = Arte.Pincel(corAnel);
            w.Texto.Text = textoAnel;
            w.Texto.FontSize = fonteAnel;
            w.Texto.Foreground = Arte.Pincel(claro ? "#1A1A1A" : "#FFFFFF");
            w.Trilho.Stroke = Arte.Pincel(claro ? "#26000000" : "#3DFFFFFF");
            w.MiSessao.Header = LinhaSessao();
            w.MiSemana.Header = LinhaSemana();
            w.MiHora.Header = uso.Hora;
        }

        static Geometry Arco(double valor)
        {
            const double c = 16, r = 14.5;
            double p = Math.Max(0, Math.Min(100, valor)) / 100;
            if (p >= 0.999) return new EllipseGeometry(new Point(c, c), r, r);
            if (p <= 0.001) return null;
            double ang = p * 2 * Math.PI - Math.PI / 2;
            return Geometry.Parse(string.Format(inv, "M {0},{1} A {2},{2} 0 {3} 1 {4},{5}", c, c - r, r, p > 0.5 ? 1 : 0, c + r * Math.Cos(ang), c + r * Math.Sin(ang)));
        }

        static bool TemaClaro()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k != null && Convert.ToInt32(k.GetValue("SystemUsesLightTheme", 0)) == 1;
            }
            catch { return false; }
        }

        static void AbrirSite() { try { Process.Start("https://claude.ai/settings/usage"); } catch { } }

        // --- Balão ---
        void CriarBalao()
        {
            balao = (Window)XamlReader.Parse(Arte.XamlBalao);
            cartao = (FrameworkElement)balao.Content;
            pctSessao = (TextBlock)balao.FindName("PctSessao");
            pctSemana = (TextBlock)balao.FindName("PctSemana");
            renovaSessao = (TextBlock)balao.FindName("RenovaSessao");
            renovaSemana = (TextBlock)balao.FindName("RenovaSemana");
            rodape = (TextBlock)balao.FindName("Rodape");
            barraSessao = (Border)balao.FindName("BarraSessao");
            barraSemana = (Border)balao.FindName("BarraSemana");
            bico = (Shapes.Path)balao.FindName("Bico");
            var h = new WindowInteropHelper(balao).EnsureHandle();
            // fora do Alt+Tab, sem foco e transparente para o mouse
            Win32.SetWindowLong(h, Win32.GWL_EXSTYLE, Win32.GetWindowLong(h, Win32.GWL_EXSTYLE) | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TRANSPARENT);
            Ordem.Balao = h;
        }

        void PreencherBalao()
        {
            pctSessao.Text = uso.Sessao + "%";
            pctSemana.Text = uso.Semana + "%";
            barraSessao.Width = LarguraBarra * Math.Min(100, uso.Sessao) / 100.0;
            barraSemana.Width = LarguraBarra * Math.Min(100, uso.Semana) / 100.0;
            barraSessao.Background = Arte.Pincel(Arte.CorDoUso(uso.Sessao));
            barraSemana.Background = Arte.Pincel(Arte.CorDoUso(uso.Semana));
            renovaSessao.Text = uso.RenovaSessao;
            renovaSemana.Text = uso.RenovaSemana;
            rodape.Text = uso.Hora;
        }

        // O bico aponta para o visitante (ou para o anel, se o visitante estiver desligado); o balão não sai da tela
        void MostrarBalao(Widget w)
        {
            PreencherBalao();
            donoBalao = w;
            double alvo = w.Visita == "parado" ? w.Janela.Left + w.Fundo.ActualWidth + w.VisitaX + 16 : w.Janela.Left + 64;
            cartao.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var tamanho = cartao.DesiredSize;
            double esquerda = alvo - 40;
            if (!double.IsNaN(w.TelaE)) esquerda = Math.Max(w.TelaE + 4, Math.Min(w.TelaD - tamanho.Width - 4, esquerda));
            bico.Margin = new Thickness(Math.Max(12, Math.Min(204, alvo - esquerda - 16)), -1, 0, 0);
            balao.Left = esquerda;
            balao.Top = w.Janela.Top + Folga + 6 - (tamanho.Height - 8);
            if (!balao.IsVisible) balao.Show();
            Ordem.Elevar();
        }

        void EsconderBalao(Widget w)
        {
            if (donoBalao == null || (w != null && donoBalao != w)) return;
            balao.Hide();
            donoBalao = null;
        }

        // --- Ícone nos ícones ocultos ---
        void CriarBandeja()
        {
            bandeja = new Forms.NotifyIcon { Text = "Claude Widget" };
            AtualizarIconeBandeja();
            var menu = new Forms.ContextMenuStrip();
            var itemMostrar = new Forms.ToolStripMenuItem("Esconder widget");
            itemMostrar.Click += delegate { Esconder(!escondido); };
            menu.Items.Add(itemMostrar);
            menu.Items.Add("Atualizar agora", null, delegate { AtualizarUso(); Animar("pular"); });
            menu.Items.Add("Abrir uso no claude.ai", null, delegate { AbrirSite(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            var anel = new Forms.ToolStripMenuItem("Mostrar %");
            foreach (var opcao in OpcoesDoAnel)
            {
                var valor = opcao[1];
                var item = new Forms.ToolStripMenuItem(opcao[0]);
                item.Click += delegate { EscolherAnel(valor); };
                anel.DropDownItems.Add(item);
            }
            menu.Items.Add(anel);
            var itemWindows = new Forms.ToolStripMenuItem("Iniciar com o Windows");
            itemWindows.Click += delegate { IniciarComWindows(!IniciaComWindows()); };
            menu.Items.Add(itemWindows);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Sair", null, delegate { Sair(); });
            menu.Opening += delegate
            {
                itemMostrar.Text = escondido ? "Mostrar widget" : "Esconder widget";
                itemWindows.Checked = IniciaComWindows();
                for (int i = 0; i < anel.DropDownItems.Count; i++)
                    ((Forms.ToolStripMenuItem)anel.DropDownItems[i]).Checked = config.Mostrar == OpcoesDoAnel[i][1];
            };
            bandeja.ContextMenuStrip = menu;
            bandeja.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) Esconder(!escondido); };
            bandeja.Visible = true;
        }

        // O ícone é o próprio mascote, na cor escolhida
        void AtualizarIconeBandeja()
        {
            int lado = Forms.SystemInformation.SmallIconSize.Width;
            var cor = (Color)ColorConverter.ConvertFromString(config.Cor);
            using (var bmp = new Drawing.Bitmap(lado, lado))
            {
                using (var g = Drawing.Graphics.FromImage(bmp))
                using (var corpo = new Drawing.SolidBrush(Drawing.Color.FromArgb(cor.R, cor.G, cor.B)))
                using (var olho = new Drawing.SolidBrush(Drawing.Color.FromArgb(20, 20, 19)))
                {
                    g.Clear(Drawing.Color.Transparent);
                    g.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.Half;
                    float k = lado / 32f, y0 = (lado - 20 * k) / 2;
                    int[][] partes = { new[] { 4, 0, 24, 16 }, new[] { 0, 8, 32, 4 }, new[] { 6, 16, 2, 4 }, new[] { 10, 16, 2, 4 }, new[] { 20, 16, 2, 4 }, new[] { 24, 16, 2, 4 } };
                    foreach (var p in partes) g.FillRectangle(corpo, p[0] * k, y0 + p[1] * k, p[2] * k, p[3] * k);
                    g.FillRectangle(olho, 8 * k, y0 + 4 * k, 2 * k, 4 * k);
                    g.FillRectangle(olho, 22 * k, y0 + 4 * k, 2 * k, 4 * k);
                }
                var antigo = iconeBandeja;
                iconeBandeja = bmp.GetHicon();
                bandeja.Icon = Drawing.Icon.FromHandle(iconeBandeja);
                if (antigo != IntPtr.Zero) Win32.DestroyIcon(antigo);
            }
        }

        const string ChaveRun = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static bool IniciaComWindows()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(ChaveRun)) return k != null && k.GetValue("ClaudeWidget") != null; }
            catch { return false; }
        }

        static void IniciarComWindows(bool ligar)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(ChaveRun))
                {
                    if (ligar) k.SetValue("ClaudeWidget", "\"" + Assembly.GetExecutingAssembly().Location + "\"");
                    else k.DeleteValue("ClaudeWidget", false);
                }
            }
            catch { }
        }

        static readonly string[][] OpcoesDoAnel = { new[] { "O maior dos dois", "maior" }, new[] { "Sessão (5h)", "sessao" }, new[] { "Semanal", "semanal" } };

        void EscolherAnel(string valor)
        {
            config.Mostrar = valor;
            config.Salvar();
            MostrarUso();
            Animar("pular");
        }

        // --- Menu do widget (clique no mascote ou botão direito) ---
        MenuItem Opcao(ItemsControl pai, string titulo, Action acao = null, Func<bool> marcada = null, Widget w = null, bool info = false)
        {
            var item = new MenuItem { Header = titulo };
            if (acao != null) item.Click += delegate { acao(); };
            if (info) item.IsEnabled = false;
            if (marcada != null && w != null) w.Marcas.Add(new KeyValuePair<MenuItem, Func<bool>>(item, marcada));
            pai.Items.Add(item);
            return item;
        }

        void MudouEstilo()
        {
            config.Salvar();
            foreach (var x in widgets.Values) EstiloDe(x);
            AtualizarIconeBandeja();
            if (!config.Movimento)
            {
                fila.Clear();
                foreach (var x in widgets.Values) x.Principal.Aplicar(new Quadro());
            }
            Animar("pular");
        }

        void EstiloDe(Widget w)
        {
            w.Principal.Estilo(config.Cor, config.Chapeu, config.Oculos);
            w.Visitante.Estilo(config.Cor, config.Chapeu, config.Oculos);
        }

        void CriarMenu(Widget w)
        {
            var menu = new ContextMenu();
            w.MiSessao = Opcao(menu, "", info: true);
            w.MiSemana = Opcao(menu, "", info: true);
            w.MiHora = Opcao(menu, "", info: true);
            menu.Items.Add(new Separator());
            Opcao(menu, "Atualizar agora", delegate { AtualizarUso(); Animar("pular"); });
            Opcao(menu, "Abrir uso no claude.ai", AbrirSite);
            menu.Items.Add(new Separator());

            var anel = Opcao(menu, "Mostrar %");
            foreach (var opcao in OpcoesDoAnel)
            {
                var valor = opcao[1];
                Opcao(anel, opcao[0], delegate { EscolherAnel(valor); }, () => config.Mostrar == valor, w);
            }
            var cores = Opcao(menu, "Cor");
            foreach (var cor in Arte.Cores)
            {
                var hex = cor[1];
                Opcao(cores, cor[0], delegate { config.Cor = hex; MudouEstilo(); }, () => string.Equals(config.Cor, hex, StringComparison.OrdinalIgnoreCase), w);
            }
            var chapeus = Opcao(menu, "Chapéu");
            foreach (var chapeu in Arte.Chapeus)
            {
                var id = chapeu.Id;
                Opcao(chapeus, chapeu.Nome, delegate { config.Chapeu = id; MudouEstilo(); }, () => config.Chapeu == id, w);
            }
            Opcao(menu, "Óculos escuros", delegate { config.Oculos = !config.Oculos; MudouEstilo(); }, () => config.Oculos, w);

            var posicao = Opcao(menu, "Posição neste monitor");
            foreach (var modo in new[] { new[] { "Automático (desvia do Iniciar e dos Widgets)", "auto" }, new[] { "Canto esquerdo", "esquerda" }, new[] { "Perto do relógio", "direita" } })
            {
                var valor = modo[1];
                Opcao(posicao, modo[0], delegate { EscolherPosicao(w, valor); }, () => config.PosicaoDe(w.Monitor).Modo == valor, w);
            }
            Opcao(posicao, "Onde eu arrastar (segure e arraste o widget)", null, () => config.PosicaoDe(w.Monitor).Modo == "manual", w, true);
            menu.Items.Add(new Separator());
            Opcao(menu, "Movimento", delegate { config.Movimento = !config.Movimento; MudouEstilo(); }, () => config.Movimento, w);
            Opcao(menu, "Visitante ao passar o mouse", delegate { config.Visitante = !config.Visitante; MudouEstilo(); }, () => config.Visitante, w);
            var chegada = Opcao(menu, "Visitante chega de");
            foreach (var transporte in Arte.Transportes)
            {
                var id = transporte.Id;
                Opcao(chegada, transporte.Nome, delegate { config.Transporte = id; config.Salvar(); }, () => config.Transporte == id, w);
            }
            menu.Items.Add(new Separator());
            Opcao(menu, "Esconder (fica nos ícones ocultos)", delegate { Esconder(true); });
            Opcao(menu, "Sair", Sair);

            menu.Opened += delegate
            {
                foreach (var m in w.Marcas) m.Key.IsChecked = m.Value();
                Ordem.Pausado = true;
                EsconderBalao(null);
                PararVisita(w);
                // O widget nunca fica ativo (para não roubar o foco), e aí o menu não percebe o clique fora.
                // Enquanto o menu está aberto, ele fica ativo; o vigia fecha o menu se outra janela vier para a frente.
                Win32.SetWindowLong(w.Hwnd, Win32.GWL_EXSTYLE, Win32.GetWindowLong(w.Hwnd, Win32.GWL_EXSTYLE) & ~Win32.WS_EX_NOACTIVATE);
                Win32.SetForegroundWindow(w.Hwnd);
                menuAberto = menu;
                frenteAoAbrir = Win32.GetForegroundWindow();
            };
            menu.Closed += delegate
            {
                Ordem.Pausado = false;
                menuAberto = null;
                Win32.SetWindowLong(w.Hwnd, Win32.GWL_EXSTYLE, Win32.GetWindowLong(w.Hwnd, Win32.GWL_EXSTYLE) | Win32.WS_EX_NOACTIVATE);
                if (w.Fundo.IsMouseOver) IniciarVisita(w);
                Ordem.Elevar();
            };
            w.Menu = menu;
            w.Fundo.ContextMenu = menu;
        }

        void AbrirMenu(Widget w)
        {
            w.Menu.PlacementTarget = w.Fundo;
            w.Menu.Placement = PlacementMode.Top;
            w.Menu.IsOpen = true;
        }

        void EscolherPosicao(Widget w, string modo)
        {
            if (w.Monitor == null) return;
            if (modo == "auto") config.Posicoes.Remove(w.Monitor);
            else config.Posicoes[w.Monitor] = new Posicao { Modo = modo, Lado = "esquerda", Desvio = Margem };
            w.Layout = null;
            config.Salvar();
            Animar("pular");
        }

        // --- Widgets, um por barra ---
        Widget NovoWidget(IntPtr barra)
        {
            var xaml = Arte.XamlWidget.Replace("§M§", Arte.XamlBoneco("M")).Replace("§V§", Arte.XamlBoneco("V"));
            var janela = (Window)XamlReader.Parse(xaml);
            var w = new Widget
            {
                Barra = barra,
                Janela = janela,
                Fundo = (Border)janela.FindName("Fundo"),
                AreaMascote = (Canvas)janela.FindName("AreaMascote"),
                Trilho = (Shapes.Ellipse)janela.FindName("Trilho"),
                Arco = (Shapes.Path)janela.FindName("Arco"),
                Texto = (TextBlock)janela.FindName("Texto"),
                Principal = new Boneco(janela, "M"),
                Visitante = new Boneco(janela, "V"),
                VisitaX = Fora,
            };
            // o palco começa 6 px acima do widget (espaço para chapéu, bicicleta e foguete)
            Canvas.SetTop(w.Visitante.Raiz, (Altura - 20) / 2 + Folga);
            Canvas.SetLeft(w.Visitante.Raiz, Fora);
            w.Visitante.Raiz.Visibility = Visibility.Hidden;
            CriarMenu(w);

            w.Fundo.MouseLeftButtonUp += delegate { AtualizarUso(); Animar("pular"); };
            w.Fundo.MouseEnter += delegate { IniciarVisita(w); };
            w.Fundo.MouseLeave += delegate { PararVisita(w); };
            w.AreaMascote.MouseLeftButtonUp += (s, e) => { e.Handled = true; AbrirMenu(w); };

            // Segurar e arrastar move o widget pela barra; um clique normal continua sendo clique
            w.Fundo.PreviewMouseLeftButtonDown += delegate { arrasto = new Arrasto { W = w, X0 = CursorX() }; };
            w.Fundo.PreviewMouseMove += (s, e) =>
            {
                var a = arrasto;
                if (a == null || a.W != w || e.LeftButton != MouseButtonState.Pressed) return;
                double dx = CursorX() - a.X0;
                if (!a.Movendo)
                {
                    if (Math.Abs(dx) < 5 || double.IsNaN(w.LimE)) return;
                    a.Movendo = true;
                    a.L0 = w.Janela.Left;
                    w.Arrastando = true;
                    PararVisita(w);
                    w.Fundo.CaptureMouse();
                }
                w.Janela.Left = Math.Max(w.LimE, Math.Min(w.LimD - w.Janela.ActualWidth, a.L0 + dx));
            };
            w.Fundo.PreviewMouseLeftButtonUp += (s, e) =>
            {
                var a = arrasto;
                arrasto = null;
                if (a == null || !a.Movendo) return;
                e.Handled = true;
                w.Fundo.ReleaseMouseCapture();
                FimDoArrasto(w);
            };
            w.Fundo.LostMouseCapture += delegate
            {
                var a = arrasto;
                if (a != null && a.Movendo) { arrasto = null; FimDoArrasto(w); }
            };

            // Fora do Alt+Tab e sem roubar o foco
            w.Hwnd = new WindowInteropHelper(janela).EnsureHandle();
            Win32.SetWindowLong(w.Hwnd, Win32.GWL_EXSTYLE, Win32.GetWindowLong(w.Hwnd, Win32.GWL_EXSTYLE) | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
            Ordem.Widgets.Add(w.Hwnd);
            MostrarEstado(w);
            EstiloDe(w);
            return w;
        }

        void RemoverWidget(long chave)
        {
            var w = widgets[chave];
            Ordem.Widgets.Remove(w.Hwnd);
            EsconderBalao(w);
            w.Janela.Close();
            widgets.Remove(chave);
        }

        double CursorX()
        {
            Win32.POINT p;
            Win32.GetCursorPos(out p);
            return p.X / escala;
        }

        // Guarda a distância até o lado mais perto, para sobreviver a mudanças de resolução
        void FimDoArrasto(Widget w)
        {
            w.Arrastando = false;
            if (w.Monitor == null || double.IsNaN(w.LimE)) return;
            double largura = w.Janela.ActualWidth;
            config.Posicoes[w.Monitor] = w.Janela.Left + largura / 2 < (w.LimE + w.LimD) / 2
                ? new Posicao { Modo = "manual", Lado = "esquerda", Desvio = Math.Round(w.Janela.Left - w.LimE) }
                : new Posicao { Modo = "manual", Lado = "direita", Desvio = Math.Round(w.LimD - w.Janela.Left - largura) };
            config.Salvar();
            Animar("pular");
        }

        // --- Posição na barra ---
        static List<IntPtr> Barras()
        {
            var lista = new List<IntPtr>();
            var principal = Win32.FindWindow("Shell_TrayWnd", null);
            if (principal != IntPtr.Zero) lista.Add(principal);
            var outra = IntPtr.Zero;
            while ((outra = Win32.FindWindowEx(IntPtr.Zero, outra, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) lista.Add(outra);
            return lista;
        }

        // A janela da frente cobre o monitor inteiro (vídeo, apresentação, jogo)?
        static IntPtr MonitorTelaCheia()
        {
            var frente = Win32.GetForegroundWindow();
            if (frente == IntPtr.Zero) return IntPtr.Zero;
            var classe = new StringBuilder(64);
            Win32.GetClassName(frente, classe, 64);
            if (ClassesDaShell.Contains(classe.ToString())) return IntPtr.Zero;
            var monitor = Win32.MonitorFromWindow(frente, 0);
            var info = new Win32.MONITORINFOEX();
            Win32.RECT r;
            if (monitor == IntPtr.Zero || !Win32.GetMonitorInfo(monitor, info) || !Win32.GetWindowRect(frente, out r)) return IntPtr.Zero;
            var m = info.rcMonitor;
            return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom ? monitor : IntPtr.Zero;
        }

        // Pergunta à barra onde estão os botões, numa thread separada para não travar a animação
        void PedirLayout(Widget w)
        {
            if (w.BuscandoLayout) return;
            w.BuscandoLayout = true;
            var barra = w.Barra;
            var esc = escala;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var layout = new Layout { Quando = DateTime.Now };
                try
                {
                    var botoes = AutomationElement.FromHandle(barra).FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    foreach (AutomationElement botao in botoes)
                    {
                        var c = botao.Current;
                        var r = c.BoundingRectangle;
                        if (r.IsEmpty || r.Width <= 0) continue;
                        double de = r.Left / esc, ate = r.Right / esc;
                        layout.Ocupados.Add(new[] { de, ate });
                        if (c.ClassName != null && c.ClassName.StartsWith("SystemTray.") && (!layout.Bandeja.HasValue || de < layout.Bandeja.Value)) layout.Bandeja = de;
                        if (c.AutomationId == "StartButton") layout.Inicio = de;
                    }
                }
                catch { }
                app.Dispatcher.BeginInvoke(new Action(delegate { w.Layout = layout; w.BuscandoLayout = false; }));
            });
        }

        static bool Livre(Layout layout, double de, double ate)
        {
            return layout.Ocupados.All(o => !(o[0] < ate && o[1] > de));
        }

        // Automático: canto esquerdo se estiver livre (ou logo depois do botão de Widgets);
        // com o Iniciar no canto esquerdo, encosta na bandeja; senão, o maior espaço livre
        static double XAuto(Layout layout, double esq, double dir, double largura)
        {
            var ocupados = layout.Ocupados.OrderBy(o => o[0]).ToList();
            var livres = new List<double[]>();
            double cursor = esq;
            foreach (var o in ocupados)
            {
                if (o[0] > cursor) livres.Add(new[] { cursor, o[0] });
                cursor = Math.Max(cursor, o[1]);
            }
            if (dir > cursor) livres.Add(new[] { cursor, dir });
            double precisa = largura + 2 * Margem;
            bool inicioNoCanto = layout.Inicio.HasValue && layout.Inicio.Value - esq < 80;
            if (!inicioNoCanto)
                foreach (var l in livres) if (l[1] - l[0] >= precisa) return l[0] + Margem;
            if (layout.Bandeja.HasValue)
            {
                double x = layout.Bandeja.Value - largura - Margem;
                if (Livre(layout, x, x + largura)) return x;
            }
            var maior = livres.OrderByDescending(l => l[1] - l[0]).FirstOrDefault();
            return maior != null ? maior[0] + Margem : esq + Margem;
        }

        // Coloca o widget na sua barra conforme a posição escolhida; devolve false se ele deve ficar escondido
        bool AtualizarPosicao(Widget w, IntPtr telaCheia)
        {
            Win32.RECT r;
            if (!Win32.GetWindowRect(w.Barra, out r)) return false;
            var monitor = Win32.MonitorFromWindow(w.Barra, 2);
            if (monitor == telaCheia) return false;
            var info = new Win32.MONITORINFOEX();
            if (!Win32.GetMonitorInfo(monitor, info)) return false;
            if (r.Top >= info.rcMonitor.Bottom - 4) return false;  // barra escondida (ocultar automaticamente)

            w.Monitor = info.szDevice;
            w.TelaE = info.rcMonitor.Left / escala;
            w.TelaD = info.rcMonitor.Right / escala;
            double esq = r.Left / escala, dir = r.Right / escala;
            w.LimE = esq;
            w.LimD = dir;
            double topo = r.Top / escala + ((r.Bottom - r.Top) / escala - Altura) / 2 - Folga;
            if (!(Math.Abs(w.Janela.Top - topo) <= 0.5)) w.Janela.Top = topo;  // !(<=) também cobre o NaN inicial
            if (w.Arrastando) return true;

            double largura = w.Janela.ActualWidth > 0 ? w.Janela.ActualWidth : 144;
            var pos = config.PosicaoDe(w.Monitor);
            if (pos.Modo == "auto" || pos.Modo == "direita")
            {
                // Refaz o mapa da barra a cada 30 s ou quando ela muda de tamanho
                var chaveBarra = r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom;
                if (w.Layout == null || w.RectBarra != chaveBarra || (DateTime.Now - w.Layout.Quando).TotalSeconds > 30)
                {
                    w.RectBarra = chaveBarra;
                    PedirLayout(w);
                }
                if (w.Layout == null) return false;  // espera o primeiro mapa para não aparecer e pular de lugar
            }
            double esquerda;
            if (pos.Modo == "esquerda") esquerda = esq + Margem;
            else if (pos.Modo == "direita") esquerda = (w.Layout.Bandeja.HasValue ? w.Layout.Bandeja.Value : dir - 300) - largura - Margem;
            else if (pos.Modo == "manual") esquerda = pos.Lado == "direita" ? dir - pos.Desvio - largura : esq + pos.Desvio;
            else esquerda = XAuto(w.Layout, esq, dir, largura);
            esquerda = Math.Max(esq, Math.Min(dir - largura, esquerda));
            if (!(Math.Abs(w.Janela.Left - esquerda) <= 0.5)) w.Janela.Left = esquerda;
            return true;
        }

        // A barra também é "sempre visível": reafirma a ordem, segue monitores que entram e saem
        void Vigiar()
        {
            // Clicou em outra janela com o menu aberto: fecha o menu
            if (menuAberto != null && menuAberto.IsOpen)
            {
                var frente = Win32.GetForegroundWindow();
                uint dono;
                Win32.GetWindowThreadProcessId(frente, out dono);
                if (frente != frenteAoAbrir && dono != pid) menuAberto.IsOpen = false;
            }
            var barras = Barras();
            var chaves = new HashSet<long>(barras.Select(b => b.ToInt64()));
            foreach (var chave in widgets.Keys.ToList()) if (!chaves.Contains(chave)) RemoverWidget(chave);
            var telaCheia = MonitorTelaCheia();
            foreach (var barra in barras)
            {
                Widget w;
                if (!widgets.TryGetValue(barra.ToInt64(), out w)) { w = NovoWidget(barra); widgets[barra.ToInt64()] = w; }
                if (!escondido && AtualizarPosicao(w, telaCheia))
                {
                    if (!w.Janela.IsVisible) w.Janela.Show();
                }
                else if (w.Janela.IsVisible)
                {
                    EsconderBalao(w);
                    w.Visita = "fora";
                    w.VisitaX = Fora;
                    w.Visitante.Raiz.Visibility = Visibility.Hidden;
                    w.Janela.Hide();
                }
            }
            Ordem.Elevar();
        }

        // --- Animação: quadros de 120 ms ---
        void Animar(string nome)
        {
            fila.Clear();
            foreach (var q in Arte.Sequencias[nome]) fila.Enqueue(q);
        }

        string Sortear()
        {
            int sorte = sorteio.Next(100);
            for (int i = 0; i < Arte.Movimentos.Length; i++)
            {
                sorte -= Arte.Pesos[i];
                if (sorte < 0) return Arte.Movimentos[i];
            }
            return "respirar";
        }

        void TicAnimacao()
        {
            // Respeita a opção do menu e "Efeitos de animação" desligado no Windows
            bool mexer = config.Movimento && SystemParameters.ClientAreaAnimation;
            if (mexer)
            {
                if (fila.Count == 0) Animar(Sortear());
                var q = fila.Dequeue();
                foreach (var w in widgets.Values)
                {
                    var quadro = q;
                    // com visita, o mascote olha para o visitante
                    if (w.Visita != "fora") { quadro = q.Copia(); quadro.Olhar = 2; }
                    w.Principal.Aplicar(quadro);
                }
            }
            foreach (var w in widgets.Values) PassoDaVisita(w, mexer);
        }

        // Visitante: chega pela direita (a pé, de skate, bicicleta, carro ou foguete) quando o mouse entra
        // e vai embora quando sai
        Transporte TransporteAtual() { return Arte.AcharTransporte(transporteDemo ?? config.Transporte); }

        void IniciarVisita(Widget w)
        {
            if (w.Arrastando || Ordem.Pausado || escondido) return;
            if (!config.Visitante || w.Visita == "parado") { MostrarBalao(w); return; }
            if (w.Visita != "entrando")
            {
                w.Visitante.Montar(TransporteAtual());
                w.Visitante.Virar(false);
                w.Visita = "entrando";
                w.Visitante.Raiz.Visibility = Visibility.Visible;
            }
        }

        // O quadro do visitante andando, conforme o transporte: pernas, balanço e para onde olha
        static Quadro QuadroAndando(Transporte t, bool par)
        {
            var q = new Quadro { Olhar = -2 };
            if (t.Pernas == "andar" || t.Pernas == "pedalar") q.Pe = par ? "A" : "B";
            else if (t.Pernas == "encolhidas") q.Pe = "T";
            if (t.Balanca && par) q.Y = -1;
            return q;
        }

        void PararVisita(Widget w)
        {
            EsconderBalao(w);
            if (w.Visita == "entrando" || w.Visita == "parado") w.Visita = "saindo";
        }

        void PassoDaVisita(Widget w, bool mexer)
        {
            if (w.Visita == "fora") return;
            w.Passo++;
            bool par = w.Passo % 2 == 0;
            var t = TransporteAtual();
            double passo = mexer ? t.Velocidade : 100;
            Quadro q;
            bool andando = true;
            if (w.Visita == "entrando")
            {
                w.VisitaX = Math.Max(t.Parada, w.VisitaX - passo);
                q = QuadroAndando(t, par);
                if (w.VisitaX <= t.Parada)
                {
                    // chegou: para, desliga chama e fumaça e mostra o balão
                    w.Visita = "parado";
                    w.Passo = 0;
                    andando = false;
                    q = new Quadro { Olhar = -2, Pe = t.Pernas == "encolhidas" ? "T" : "N" };
                    Canvas.SetLeft(w.Visitante.Raiz, w.VisitaX);
                    MostrarBalao(w);
                }
            }
            else if (w.Visita == "parado")
            {
                andando = false;
                q = new Quadro { Olhar = -2, Fechado = w.Passo % 30 >= 28, Pe = t.Pernas == "encolhidas" ? "T" : "N" };
            }
            else
            {
                // indo embora: vira para a direita (com o espelho, olhar -2 fica olhando para a direita)
                w.Visitante.Virar(true);
                w.VisitaX = Math.Min(Fora, w.VisitaX + passo);
                q = QuadroAndando(t, par);
                if (w.VisitaX >= Fora)
                {
                    w.Visita = "fora";
                    w.Visitante.Raiz.Visibility = Visibility.Hidden;
                    w.Visitante.Virar(false);
                    andando = false;
                    q = new Quadro();
                }
            }
            Canvas.SetLeft(w.Visitante.Raiz, w.VisitaX);
            w.Visitante.Aplicar(q);
            w.Visitante.Fase(andando, par);
        }
    }

    static class Programa
    {
        const string NomeMutex = @"Local\ClaudeWidget";
        const string EventoMostrar = @"Local\ClaudeWidget.Mostrar";
        const string EventoSair = @"Local\ClaudeWidget.Sair";

        [STAThread]
        static void Main(string[] args)
        {
            bool sair = args.Contains("--sair"), demo = args.Contains("--demo");
            bool criou;
            using (var mutex = new Mutex(true, NomeMutex, out criou))
            {
                if (!criou)
                {
                    // Já tem um aberto: pede para ele aparecer de novo (ou fechar, com --sair)
                    try { using (var ev = EventWaitHandle.OpenExisting(sair ? EventoSair : EventoMostrar)) ev.Set(); }
                    catch { }
                    return;
                }
                if (sair) return;

                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                Forms.Application.EnableVisualStyles();
                // Desenho pela CPU: as janelas são minúsculas, e pela placa de vídeo o driver reserva centenas de MB à toa
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                // Um erro inesperado não derruba o widget: fica anotado em %LOCALAPPDATA%\ClaudeWidget\erros.log
                app.DispatcherUnhandledException += (s, e) =>
                {
                    try
                    {
                        Directory.CreateDirectory(Config.Pasta);
                        File.AppendAllText(System.IO.Path.Combine(Config.Pasta, "erros.log"), DateTime.Now.ToString("s") + " " + e.Exception + Environment.NewLine);
                    }
                    catch { }
                    e.Handled = true;
                };
                var controle = new Controle(app, demo);

                var mostrar = new EventWaitHandle(false, EventResetMode.AutoReset, EventoMostrar);
                var fechar = new EventWaitHandle(false, EventResetMode.AutoReset, EventoSair);
                var escuta = new Thread(delegate ()
                {
                    var eventos = new WaitHandle[] { mostrar, fechar };
                    while (true)
                    {
                        if (WaitHandle.WaitAny(eventos) == 0) app.Dispatcher.BeginInvoke(new Action(controle.Mostrar));
                        else { app.Dispatcher.BeginInvoke(new Action(controle.Sair)); return; }
                    }
                });
                escuta.IsBackground = true;
                escuta.Start();

                app.Run();
                mutex.ReleaseMutex();
            }
        }
    }
}
