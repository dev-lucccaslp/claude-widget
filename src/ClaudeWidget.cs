// Claude Widget: o mascote do Claude Code na barra de tarefas do Windows, com um anel mostrando o % de uso do plano.
// https://github.com/dev-lucccaslp/claude-widget
//
// Compilado com o csc do .NET Framework 4.x que já vem no Windows (por isso C# 5): veja build.ps1.
// Lê o login do Claude Code em %USERPROFILE%\.claude\.credentials.json e consulta o uso em api.anthropic.com.
// Não renova nem grava o login. Fora isso, só olha a página de Releases no GitHub para avisar de versão nova
// (dá para desligar no ícone). As escolhas ficam em %LOCALAPPDATA%\ClaudeWidget\config.json e o histórico
// do gráfico em historico.json, na mesma pasta.
//
// Linha de comando: ClaudeWidget.exe [--sair] [--atualizar] [--demo]
//   sem nada     abre; se já estiver aberto, só mostra o widget de novo (ou assume o lugar, se for mais novo)
//   --sair       fecha a cópia aberta (o instalador usa antes de trocar o .exe)
//   --atualizar  procura versão nova no GitHub e, se tiver, baixa e troca na hora
//   --demo       comemora e passa o visitante por todos os transportes, para testar sem mouse

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
[assembly: AssemblyVersion("1.5.0.0")]
[assembly: AssemblyFileVersion("1.5.0.0")]

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

        [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        // Há quanto tempo ninguém mexe no mouse ou no teclado
        public static TimeSpan Ocioso()
        {
            var info = new LASTINPUTINFO { cbSize = 8 };
            if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
        }

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000;
    }

    // Ordem na tela: widgets por cima da barra e o balão por cima de tudo.
    // Com o menu aberto (Pausado) não mexe em nada, senão o widget cobriria o menu.
    static class Ordem
    {
        public static readonly List<IntPtr> Widgets = new List<IntPtr>();
        public static IntPtr Balao = IntPtr.Zero, Fala = IntPtr.Zero;
        public static bool Pausado;
        static readonly IntPtr SempreVisivel = new IntPtr(-1);

        public static void Elevar()
        {
            if (Pausado) return;
            foreach (var h in Widgets) Win32.SetWindowPos(h, SempreVisivel, 0, 0, 0, 0, 0x0213);
            if (Fala != IntPtr.Zero) Win32.SetWindowPos(Fala, SempreVisivel, 0, 0, 0, 0, 0x0213);
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
        public string Transporte { get; set; }  // como o visitante chega (Arte.Transportes)
        public bool ChapeuEstacao { get; set; }  // Natal em dezembro, abóbora no Halloween, palha na festa junina...
        public bool AvisarVersaoNova { get; set; }
        public string VersaoAvisada { get; set; }  // para avisar de cada versão nova uma vez só
        // Lembretes do mascote
        public bool Saudacao { get; set; }        // bom dia, boa tarde, boa noite ao ligar o PC
        public bool LembreteAgua { get; set; }
        public bool LembreteAlongar { get; set; }
        public bool LembreteVista { get; set; }
        public bool LembreteHorarios { get; set; }  // almoço, café, fim do dia, sextou
        public int IntervaloLembretes { get; set; }  // minutos
        public bool Codando { get; set; }  // mascote no notebook enquanto o Claude Code trabalha
        public Dictionary<string, Posicao> Posicoes { get; set; }

        public Config()
        {
            Cor = Arte.Laranja; Chapeu = "nenhum"; Movimento = true; Visitante = true; Mostrar = "maior"; Transporte = "pe";
            ChapeuEstacao = true; AvisarVersaoNova = true;
            Saudacao = true; LembreteAgua = true; LembreteAlongar = true; LembreteVista = true; LembreteHorarios = true; IntervaloLembretes = 60;
            Codando = true;
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
                if (c.IntervaloLembretes != 30 && c.IntervaloLembretes != 60 && c.IntervaloLembretes != 120) c.IntervaloLembretes = 60;
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
        public List<string[]> Modelos = new List<string[]>();  // limites semanais por modelo: nome, %, quando renova
        public string Creditos;          // créditos extras ou gasto além do plano, se a conta tiver
        public double CreditosPct = -1;  // < 0: sem barra
    }

    static class ApiUso
    {
        static readonly CultureInfo ptBR = new CultureInfo("pt-BR");
        static readonly CultureInfo inv = CultureInfo.InvariantCulture;

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
                LerModelos(u, uso);
                LerCreditos(u, uso);
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

        // Anda por objetos aninhados do JSON: Valor(u, "scope", "model", "display_name")
        static object Valor(Dictionary<string, object> d, params string[] caminho)
        {
            object atual = d;
            foreach (var chave in caminho)
            {
                var dic = atual as Dictionary<string, object>;
                if (dic == null || !dic.TryGetValue(chave, out atual)) return null;
            }
            return atual;
        }

        static int Inteiro(object v) { return (int)Math.Round(Convert.ToDouble(v, inv)); }

        // Limites semanais por modelo: a lista "limits" (weekly_scoped) e, nas contas que ainda têm, seven_day_opus/sonnet
        static void LerModelos(Dictionary<string, object> u, Uso uso)
        {
            var lista = Valor(u, "limits") as System.Collections.IEnumerable;
            if (lista != null)
                foreach (var item in lista)
                {
                    var l = item as Dictionary<string, object>;
                    if (l == null || (Valor(l, "kind") as string) != "weekly_scoped") continue;
                    var nome = Valor(l, "scope", "model", "display_name") as string;
                    var pct = Valor(l, "percent");
                    if (nome == null || pct == null) continue;
                    uso.Modelos.Add(new[] { nome, Inteiro(pct).ToString(), RenovaDe(l) });
                }
            foreach (var par in new[] { new[] { "seven_day_opus", "Opus" }, new[] { "seven_day_sonnet", "Sonnet" } })
            {
                var l = Limite(u, par[0]);
                var pct = l == null ? null : Valor(l, "utilization");
                var nome = par[1];
                if (pct == null || uso.Modelos.Any(m => m[0] == nome)) continue;
                uso.Modelos.Add(new[] { nome, Inteiro(pct).ToString(), RenovaDe(l) });
            }
        }

        static string RenovaDe(Dictionary<string, object> limite)
        {
            return Renova(new Dictionary<string, object> { { "x", limite } }, "x", "ddd HH:mm");
        }

        // Gasto além do plano (valor em centavos + expoente) ou, se não tiver, os créditos extras do mês (em %)
        static void LerCreditos(Dictionary<string, object> u, Uso uso)
        {
            var gasto = Limite(u, "spend");
            if (gasto != null && Valor(gasto, "enabled") is bool && (bool)Valor(gasto, "enabled") && Valor(gasto, "used", "amount_minor") != null)
            {
                uso.Creditos = "Gasto além do plano: " + Dinheiro(Valor(gasto, "used") as Dictionary<string, object>);
                var limite = Valor(gasto, "limit") as Dictionary<string, object>;
                if (limite != null && limite.ContainsKey("amount_minor")) uso.Creditos += " de " + Dinheiro(limite);
                var pct = Valor(gasto, "percent");
                if (limite != null && pct != null) uso.CreditosPct = Convert.ToDouble(pct, inv);
                return;
            }
            var extra = Limite(u, "extra_usage");
            if (extra != null && Valor(extra, "is_enabled") is bool && (bool)Valor(extra, "is_enabled"))
            {
                var pct = Valor(extra, "utilization");
                if (pct == null) { uso.Creditos = "Créditos extras ligados"; return; }
                uso.CreditosPct = Convert.ToDouble(pct, inv);
                uso.Creditos = "Créditos extras: " + Inteiro(pct) + "% do limite do mês";
            }
        }

        static string Dinheiro(Dictionary<string, object> valor)
        {
            double centavos = Convert.ToDouble(Valor(valor, "amount_minor"), inv);
            var exp = Valor(valor, "exponent");
            int casas = exp == null ? 2 : Convert.ToInt32(exp, inv);
            var moeda = (Valor(valor, "currency") as string) ?? "";
            var simbolo = moeda == "USD" ? "US$ " : moeda == "BRL" ? "R$ " : moeda == "EUR" ? "€ " : moeda + " ";
            return simbolo + (centavos / Math.Pow(10, casas)).ToString("N2", ptBR);
        }
    }

    // --- Histórico do uso semanal, para o gráfico dos últimos 7 dias (fica só no seu PC) ---
    public class Amostra
    {
        public long T { get; set; }       // DateTime.Ticks, hora local
        public int Semana { get; set; }   // % do limite semanal naquele momento
    }

    static class Historico
    {
        static List<Amostra> amostras;
        static string Arquivo { get { return System.IO.Path.Combine(Config.Pasta, "historico.json"); } }

        static void Carregar()
        {
            if (amostras != null) return;
            try { amostras = new JavaScriptSerializer().Deserialize<List<Amostra>>(File.ReadAllText(Arquivo)); } catch { }
            if (amostras == null) amostras = new List<Amostra>();
        }

        public static void Adicionar(int semana)
        {
            Carregar();
            var agora = DateTime.Now;
            var ultima = amostras.Count > 0 ? amostras[amostras.Count - 1] : null;
            // guarda quando muda, ou a cada meia hora, para o arquivo não crescer à toa
            if (ultima != null && ultima.Semana == semana && agora.Ticks - ultima.T < TimeSpan.FromMinutes(30).Ticks) return;
            amostras.Add(new Amostra { T = agora.Ticks, Semana = semana });
            long limite = agora.AddDays(-9).Ticks;
            amostras.RemoveAll(a => a.T < limite);
            try
            {
                Directory.CreateDirectory(Config.Pasta);
                File.WriteAllText(Arquivo, new JavaScriptSerializer().Serialize(amostras));
            }
            catch { }
        }

        // Quantos pontos do limite semanal foram gastos em cada um dos últimos dias (o último é hoje).
        // Só conta as subidas de uma leitura para a seguinte; quando o limite renova, o número cai e isso não conta.
        public static double[] PorDia(int dias, out bool temDados)
        {
            Carregar();
            var resultado = new double[dias];
            var hoje = DateTime.Today;
            temDados = amostras.Count >= 2;
            for (int i = 1; i < amostras.Count; i++)
            {
                int subida = amostras[i].Semana - amostras[i - 1].Semana;
                if (subida <= 0) continue;
                int indice = dias - 1 - (int)(hoje - new DateTime(amostras[i].T).Date).TotalDays;
                if (indice >= 0 && indice < dias) resultado[indice] += subida;
            }
            return resultado;
        }
    }

    // --- O que o mascote fala: saudação e lembretes ---
    static class Falas
    {
        public static string Periodo(DateTime d) { return d.Hour >= 5 && d.Hour < 12 ? "manha" : d.Hour >= 12 && d.Hour < 18 ? "tarde" : "noite"; }

        public static string Saudacao(DateTime agora, string nome, Uso uso)
        {
            var periodo = Periodo(agora);
            var texto = (periodo == "manha" ? "Bom dia" : periodo == "tarde" ? "Boa tarde" : "Boa noite") + (nome != null ? ", " + nome : "") + "! "
                + (periodo == "manha" ? "☀️" : periodo == "tarde" ? "🌤️" : "🌙");
            if (agora.DayOfWeek == DayOfWeek.Monday && periodo == "manha") texto += " Bora começar a semana!";
            if (agora.DayOfWeek == DayOfWeek.Friday) texto += " Sextou!";
            if (uso != null && uso.Erro == null && uso.Hora != "Carregando…")
            {
                texto += "\nSessão em " + uso.Sessao + "% e semana em " + uso.Semana + "%.";
                if (Math.Max(uso.Sessao, uso.Semana) >= 90) texto += " Vai com calma hoje 😅";
                else if (uso.Semana < 30) texto += " Bastante limite pela frente 🚀";
            }
            return texto;
        }

        public static readonly string[] Agua =
        {
            "Não esquece de tomar água! 💧", "Hora de um gole de água 💧", "Hidratação em dia? Bebe uma água aí 💧",
            "Água! Seu cérebro agradece 💧", "Já encheu a garrafinha hoje? 💧",
        };
        public static readonly string[] Alongar =
        {
            "Levanta e estica as costas um pouquinho 🙆", "Postura! Costas retas e ombros relaxados 😄",
            "Que tal uma caminhada rápida? 🚶", "Gira o pescoço devagar, ele merece 🙂", "Estica os braços pro alto, bem alto! 🙌",
        };
        public static readonly string[] Vista =
        {
            "Olha para longe por 20 segundos, seus olhos agradecem 👀", "Pisca bastante e descansa a vista um pouco 👀",
            "Tira os olhos da tela um minutinho 👀",
        };

        // Uma vez por dia, em certos horários
        public static string DoHorario(DateTime d)
        {
            int minutos = d.Hour * 60 + d.Minute;
            bool util = d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday;
            if (minutos >= 12 * 60 && minutos < 12 * 60 + 30) return "almoco|Hora do almoço! Vai comer alguma coisa 🍽️";
            if (minutos >= 15 * 60 + 30 && minutos < 16 * 60) return "cafe|Pausa pro café? ☕";
            if (util && d.DayOfWeek == DayOfWeek.Friday && minutos >= 16 * 60 && minutos < 17 * 60) return "sextou|Sextou! Bom fim de semana 🎉";
            if (util && minutos >= 18 * 60 && minutos < 18 * 60 + 30) return "encerrar|Bora encerrar o dia? Amanhã tem mais 🚀";
            return null;
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
            new Chapeu { Id = "abobora", Nome = "Abóbora", Partes = new[] {
                new[] { "#F97316", "M8,-8 H24 V0 H8 Z M6,-6 H8 V-2 H6 Z M24,-6 H26 V-2 H24 Z" }, new[] { "#C2410C", "M12,-8 h1 v8 h-1 Z M19,-8 h1 v8 h-1 Z" },
                new[] { "#15803D", "M15,-11 h2 v3 h-2 Z" }, new[] { "#1F1F1F", "M10,-6 h2 v2 h-2 Z M20,-6 h2 v2 h-2 Z M13,-3 h6 v1 h-6 Z" } } },
            new Chapeu { Id = "palha", Nome = "Palha (festa junina)", Partes = new[] {
                new[] { "#EAB308", "M0,-2 H32 V0 H0 Z" }, new[] { "#CA8A04", "M8,-8 H24 V-2 H8 Z" }, new[] { "#DC2626", "M8,-4 H24 V-3 H8 Z" },
                new[] { "#2563EB", "M18,-7 h3 v2 h-3 Z" } } },
        };

        // Chapéu automático da época do ano (null fora delas)
        public static string ChapeuDaEstacao(DateTime d)
        {
            if (d.Month == 12) return "natal";
            if ((d.Month == 10 && d.Day >= 20) || (d.Month == 11 && d.Day <= 2)) return "abobora";
            if (d.Month == 6 || (d.Month == 7 && d.Day <= 15)) return "palha";
            if (d.Month == 1 && d.Day <= 6) return "festa";
            return null;
        }

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
            new Transporte { Id = "patinete", Nome = "Patinete", Velocidade = 8, Parada = 12, Pernas = "paradas", Pecas = new[] {
                P("#4B5563", "M-2,20 H30 V22 H-2 Z", "atras"),
                P("#9CA3AF", "M-4,2 h2 v19 h-2 Z", "atras"),
                P("#1F2937", "M-7,2 h8 v2 h-8 Z", "atras"),
                P("#111827", "M-5,21 h4 v4 h-4 Z M25,21 h4 v4 h-4 Z", "atras"),
                P("#9CA3AF", "M36,6 h6 v1 h-6 Z M38,12 h6 v1 h-6 Z", "atras", "MA"),
                P("#9CA3AF", "M35,9 h6 v1 h-6 Z M37,15 h5 v1 h-5 Z", "atras", "MB") } },
            new Transporte { Id = "disco", Nome = "Disco voador", Velocidade = 9, Parada = 12, AssentoY = -4, Pernas = "encolhidas", Balanca = true, Pecas = new[] {
                P("#6693C5FD", "M2,-6 H30 V12 H2 Z", "atras"),
                P("#9CA3AF", "M-8,12 H40 V16 H-8 Z"),
                P("#6B7280", "M-4,16 H36 V19 H-4 Z"),
                P("#4B5563", "M4,19 H28 V21 H4 Z"),
                P("#FACC15", "M-4,13 h2 v2 h-2 Z M8,13 h2 v2 h-2 Z M22,13 h2 v2 h-2 Z M34,13 h2 v2 h-2 Z", "frente", "A"),
                P("#22C55E", "M2,13 h2 v2 h-2 Z M15,13 h2 v2 h-2 Z M28,13 h2 v2 h-2 Z", "frente", "B") } },
            new Transporte { Id = "tapete", Nome = "Tapete mágico", Velocidade = 7, Parada = 10, AssentoY = -2, Pernas = "encolhidas", Balanca = true, Pecas = new[] {
                P("#7C3AED", "M-6,17 H38 V21 H-6 Z"),
                P("#FACC15", "M-6,16 H38 V17 H-6 Z M-6,21 H38 V22 H-6 Z M-9,18 h3 v1 h-3 Z M-9,20 h3 v1 h-3 Z M38,18 h3 v1 h-3 Z M38,20 h3 v1 h-3 Z"),
                P("#F472B6", "M0,18 h2 v2 h-2 Z M12,18 h2 v2 h-2 Z M24,18 h2 v2 h-2 Z", "frente", "A"),
                P("#F472B6", "M6,18 h2 v2 h-2 Z M18,18 h2 v2 h-2 Z M30,18 h2 v2 h-2 Z", "frente", "B") } },
        };

        public static Transporte AcharTransporte(string id)
        {
            return Transportes.FirstOrDefault(t => t.Id == id) ?? Transportes[0];
        }

        // O notebook do mascote quando o Claude Code está trabalhando: a tampa (com o logo laranja) na frente do corpo,
        // o teclado embaixo e as teclas piscando (A/B) enquanto ele digita
        public static readonly Transporte Notebook = new Transporte { Id = "notebook", Pecas = new[] {
            P("#9CA3AF", "M6,9 H26 V19 H6 Z"),
            P("#6B7280", "M6,18 H26 V19 H6 Z M3,19 H29 V21 H3 Z"),
            P("#D77757", "M15,11 h2 v6 h-2 Z M13,13 h6 v2 h-6 Z"),
            P("#E5E7EB", "M7,19 h2 v1 h-2 Z M15,19 h2 v1 h-2 Z M23,19 h2 v1 h-2 Z", "frente", "A"),
            P("#E5E7EB", "M11,19 h2 v1 h-2 Z M19,19 h2 v1 h-2 Z", "frente", "B") } };

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
            // cansado perto do limite: respira rápido, de olhos meio fechados
            s["ofegar"] = new Roteiro().Q(y: -1).Q().Q(y: -1).Q().Q(y: -1).Q().Q(fechado: true).Q().Q(y: -1).Q();
            // quando o limite renova: três pulos altos e uma dancinha
            var festa = new Roteiro();
            for (int i = 0; i < 3; i++) festa.Q(y: -1).Q(y: -4, pe: "T", fechado: true, n: 2).Q(y: -1).Q();
            festa.AddRange(dancar);
            s["festa"] = festa;
            // no notebook: balança de leve enquanto digita
            s["digitar"] = new Roteiro().Q(n: 2).Q(y: -1).Q(n: 3).Q(y: -1).Q(n: 2).Q(fechado: true).Q(n: 2);
            return s;
        }

        // Peso de cada movimento no sorteio (soma 100); sempre emenda um no outro. Cansado, ele se mexe menos.
        public static readonly string[] Movimentos = { "andar", "respirar", "olhar", "pular", "dancar", "piscar" };
        public static readonly int[] Pesos = { 30, 25, 15, 10, 10, 10 };
        public static readonly string[] MovimentosCansado = { "ofegar", "respirar", "olhar", "piscar" };
        public static readonly int[] PesosCansado = { 40, 30, 10, 20 };

        // Cores do balão e do menu: escuro e claro, seguindo o tema do Windows
        public static readonly Dictionary<string, string[]> Tema = new Dictionary<string, string[]>
        {
            { "Fundo", new[] { "#2B2B2B", "#F9F9F9" } },
            { "Borda", new[] { "#454545", "#D9D9D9" } },
            { "Texto", new[] { "#F3F3F3", "#1A1A1A" } },
            { "Texto2", new[] { "#D6D6D6", "#3B3B3B" } },
            { "Texto3", new[] { "#9A9A9A", "#6B6B6B" } },
            { "Texto4", new[] { "#7A7A7A", "#8A8A8A" } },
            { "Trilho", new[] { "#40FFFFFF", "#1F000000" } },
            { "MenuFundo", new[] { "#2C2C2C", "#F9F9F9" } },
            { "MenuBorda", new[] { "#3F3F3F", "#DADADA" } },
            { "MenuDestaque", new[] { "#3D3D3D", "#EAEAEA" } },
            { "MenuApagado", new[] { "#9A9A9A", "#8A8A8A" } },
        };

        // Menu no estilo do Windows 11 (o padrão do WPF é o clássico cinza), com as cores do tema acima
        public const string XamlEstilos = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='ContextMenu'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='HasDropShadow' Value='False'/>
    <Setter Property='Foreground' Value='{DynamicResource Texto}'/>
    <Setter Property='FontFamily' Value='Segoe UI Variable Text, Segoe UI'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ContextMenu'>
          <Grid Margin='0,0,10,10'>
            <Border Background='{DynamicResource MenuFundo}' CornerRadius='8'>
              <Border.Effect><DropShadowEffect BlurRadius='12' ShadowDepth='3' Opacity='0.35' Color='Black'/></Border.Effect>
            </Border>
            <Border Background='{DynamicResource MenuFundo}' BorderBrush='{DynamicResource MenuBorda}' BorderThickness='1' CornerRadius='8' Padding='0,4'>
              <ItemsPresenter/>
            </Border>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='MenuItem'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Foreground' Value='{DynamicResource Texto}'/>
    <Setter Property='FontFamily' Value='Segoe UI Variable Text, Segoe UI'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='MenuItem'>
          <Border x:Name='Bd' Background='Transparent' CornerRadius='4' Margin='4,1' Padding='6,4,10,4' SnapsToDevicePixels='True'>
            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width='22'/>
                <ColumnDefinition Width='*'/>
                <ColumnDefinition Width='Auto'/>
              </Grid.ColumnDefinitions>
              <TextBlock x:Name='Marca' Text='&#xE73E;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='11'
                         Foreground='#D77757' VerticalAlignment='Center' Visibility='Hidden'/>
              <ContentPresenter Grid.Column='1' ContentSource='Header' RecognizesAccessKey='False' VerticalAlignment='Center'/>
              <TextBlock x:Name='Seta' Grid.Column='2' Text='&#xE76C;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='9'
                         Margin='18,0,0,0' VerticalAlignment='Center' Visibility='Collapsed'/>
              <Popup x:Name='PART_Popup' Placement='Right' HorizontalOffset='14' VerticalOffset='-8' AllowsTransparency='True'
                     Focusable='False' PopupAnimation='None'
                     IsOpen='{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}'>
                <Grid Margin='0,0,10,10'>
                  <Border Background='{DynamicResource MenuFundo}' CornerRadius='8'>
                    <Border.Effect><DropShadowEffect BlurRadius='12' ShadowDepth='3' Opacity='0.35' Color='Black'/></Border.Effect>
                  </Border>
                  <Border Background='{DynamicResource MenuFundo}' BorderBrush='{DynamicResource MenuBorda}' BorderThickness='1' CornerRadius='8' Padding='0,4'>
                    <ItemsPresenter/>
                  </Border>
                </Grid>
              </Popup>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Bd' Property='Background' Value='{DynamicResource MenuDestaque}'/></Trigger>
            <Trigger Property='IsChecked' Value='True'><Setter TargetName='Marca' Property='Visibility' Value='Visible'/></Trigger>
            <Trigger Property='HasItems' Value='True'><Setter TargetName='Seta' Property='Visibility' Value='Visible'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='{DynamicResource MenuApagado}'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='{x:Static MenuItem.SeparatorStyleKey}' TargetType='Separator'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Separator'><Border Height='1' Background='{DynamicResource MenuBorda}' Margin='10,4'/></ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";

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
    <Path x:Name='§Suor' Fill='#7DD3FC' Visibility='Collapsed' Data='M30,-1 h2 v1 h-2 Z M29,0 h4 v3 h-4 Z M30,3 h2 v1 h-2 Z'/>
    <Canvas x:Name='§Chapeu'/>
    <Canvas x:Name='§Xicara' Canvas.Left='31' Canvas.Top='6' Visibility='Collapsed'>
      <Path Fill='#F5F5F5' Data='M0,2 h5 v5 h-5 Z M5,3 h1 v3 h-1 Z'/>
      <Path Fill='#6B3F1F' Data='M0,2 h5 v1 h-5 Z'/>
      <Path x:Name='§VaporMao' Fill='#D1D5DB' Data='M1,-1 h1 v2 h-1 Z M3,-2 h1 v2 h-1 Z'/>
    </Canvas>
  </Canvas>
  <Canvas x:Name='§Frente'/>
  <Canvas x:Name='§XicaraMesa' Canvas.Left='31' Canvas.Top='13' Visibility='Collapsed'>
    <Path Fill='#F5F5F5' Data='M0,2 h5 v5 h-5 Z M5,3 h1 v3 h-1 Z'/>
    <Path Fill='#6B3F1F' Data='M0,2 h5 v1 h-5 Z'/>
    <Path x:Name='§VaporMesa' Fill='#D1D5DB' Data='M1,-1 h1 v2 h-1 Z M3,-2 h1 v2 h-1 Z'/>
  </Canvas>
</Canvas>").Replace("§", p);
        }

        public const string XamlWidget = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        WindowStyle='None' AllowsTransparency='True' Background='Transparent' Topmost='True' ShowInTaskbar='False'
        ShowActivated='False' Focusable='False' ResizeMode='NoResize' SizeToContent='WidthAndHeight' UseLayoutRounding='True'>
  <Grid>
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
  <Canvas x:Name='Festa' IsHitTestVisible='False'/>
  </Grid>
</Window>";

        // Balão com o uso: janela própria que fica acima dos widgets e deixa o mouse passar
        public const string XamlBalao = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        WindowStyle='None' AllowsTransparency='True' Background='Transparent' Topmost='True' ShowInTaskbar='False'
        ShowActivated='False' Focusable='False' IsHitTestVisible='False' ResizeMode='NoResize' SizeToContent='WidthAndHeight'
        UseLayoutRounding='True' FontFamily='Segoe UI Variable Text, Segoe UI, Segoe UI Emoji'>
  <Grid Margin='8'>
    <Grid.Effect><DropShadowEffect BlurRadius='10' ShadowDepth='2' Opacity='0.4' Color='Black'/></Grid.Effect>
    <StackPanel>
      <Border Background='{DynamicResource Fundo}' BorderBrush='{DynamicResource Borda}' BorderThickness='1' CornerRadius='8' Padding='12,10,12,10' Width='232'>
        <StackPanel>
          <TextBlock Text='Uso do Claude' Foreground='{DynamicResource Texto}' FontSize='13' FontWeight='SemiBold'/>
          <Grid Margin='0,10,0,0'>
            <TextBlock Text='Sessão (5h)' Foreground='{DynamicResource Texto2}' FontSize='12'/>
            <TextBlock x:Name='PctSessao' HorizontalAlignment='Right' Foreground='{DynamicResource Texto}' FontSize='12' FontWeight='SemiBold'/>
          </Grid>
          <Border Height='4' CornerRadius='2' Background='{DynamicResource Trilho}' Margin='0,5,0,0'>
            <Border x:Name='BarraSessao' HorizontalAlignment='Left' CornerRadius='2' Width='0'/>
          </Border>
          <TextBlock x:Name='RenovaSessao' Foreground='{DynamicResource Texto3}' FontSize='11' Margin='0,4,0,0'/>
          <Grid Margin='0,10,0,0'>
            <TextBlock Text='Semanal' Foreground='{DynamicResource Texto2}' FontSize='12'/>
            <TextBlock x:Name='PctSemana' HorizontalAlignment='Right' Foreground='{DynamicResource Texto}' FontSize='12' FontWeight='SemiBold'/>
          </Grid>
          <Border Height='4' CornerRadius='2' Background='{DynamicResource Trilho}' Margin='0,5,0,0'>
            <Border x:Name='BarraSemana' HorizontalAlignment='Left' CornerRadius='2' Width='0'/>
          </Border>
          <TextBlock x:Name='RenovaSemana' Foreground='{DynamicResource Texto3}' FontSize='11' Margin='0,4,0,0'/>
          <StackPanel x:Name='Extras'/>
          <TextBlock Text='Últimos 7 dias (% do limite semanal)' Foreground='{DynamicResource Texto2}' FontSize='12' Margin='0,12,0,2'/>
          <Canvas x:Name='Grafico' Width='206' Height='58' HorizontalAlignment='Left'/>
          <TextBlock x:Name='SemDados' Text='O gráfico vai enchendo conforme o widget acompanha o seu uso.' Foreground='{DynamicResource Texto3}'
                     FontSize='10.5' TextWrapping='Wrap' Visibility='Collapsed'/>
          <TextBlock x:Name='Rodape' Foreground='{DynamicResource Texto3}' FontSize='11' Margin='0,10,0,0' TextWrapping='Wrap'/>
          <TextBlock x:Name='NovaVersao' Foreground='#D77757' FontSize='11' FontWeight='SemiBold' Margin='0,4,0,0' TextWrapping='Wrap' Visibility='Collapsed'/>
          <TextBlock Text='Anel: atualizar · Bonequinho: personalizar' Foreground='{DynamicResource Texto4}' FontSize='10.5' Margin='0,2,0,0'/>
        </StackPanel>
      </Border>
      <Path x:Name='Bico' Data='M0,0 L8,8 L16,0' Fill='{DynamicResource Fundo}' Stroke='{DynamicResource Borda}' StrokeThickness='1' HorizontalAlignment='Left' Margin='24,-1,0,0'/>
    </StackPanel>
  </Grid>
</Window>";

        // Balãozinho de fala do mascote (saudação e lembretes); clique para fechar
        public const string XamlFala = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        WindowStyle='None' AllowsTransparency='True' Background='Transparent' Topmost='True' ShowInTaskbar='False'
        ShowActivated='False' Focusable='False' ResizeMode='NoResize' SizeToContent='WidthAndHeight' UseLayoutRounding='True'
        FontFamily='Segoe UI Variable Text, Segoe UI, Segoe UI Emoji' Cursor='Hand'>
  <Grid Margin='8'>
    <Grid.Effect><DropShadowEffect BlurRadius='10' ShadowDepth='2' Opacity='0.4' Color='Black'/></Grid.Effect>
    <StackPanel>
      <Border Background='{DynamicResource Fundo}' BorderBrush='{DynamicResource Borda}' BorderThickness='1' CornerRadius='10' Padding='12,8,12,9' MaxWidth='250'>
        <TextBlock x:Name='Mensagem' Foreground='{DynamicResource Texto}' FontSize='12.5' TextWrapping='Wrap' LineHeight='18'/>
      </Border>
      <Path x:Name='Bico' Data='M0,0 L8,8 L16,0' Fill='{DynamicResource Fundo}' Stroke='{DynamicResource Borda}' StrokeThickness='1' HorizontalAlignment='Left' Margin='16,-1,0,0'/>
    </StackPanel>
  </Grid>
</Window>";
    }

    // Um mascote (o principal ou o visitante): aplica quadros de animação e o estilo escolhido
    class Boneco
    {
        public Canvas Raiz, Chapeu, Atras, Frente, Xicara, XicaraMesa;
        public Shapes.Path VaporMao, VaporMesa;
        public TranslateTransform Pulo, Assento;
        public ScaleTransform Espelho;
        public Shapes.Path Corpo, Pernas, Oculos, Suor;
        public Shapes.Rectangle OlhoE, OlhoD;
        bool cansado;      // perto do limite: olhos meio fechados e uma gota de suor
        bool concentrado;  // no notebook: olhos baixos, olhando o teclado
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
            Suor = (Shapes.Path)escopo.FindName(p + "Suor");
            OlhoE = (Shapes.Rectangle)escopo.FindName(p + "OlhoE");
            OlhoD = (Shapes.Rectangle)escopo.FindName(p + "OlhoD");
            Chapeu = (Canvas)escopo.FindName(p + "Chapeu");
            Xicara = (Canvas)escopo.FindName(p + "Xicara");
            XicaraMesa = (Canvas)escopo.FindName(p + "XicaraMesa");
            VaporMao = (Shapes.Path)escopo.FindName(p + "VaporMao");
            VaporMesa = (Shapes.Path)escopo.FindName(p + "VaporMesa");
        }

        // Café: a xícara na mão (voltando da copa) e na mesa, ao lado do notebook, soltando vapor
        public void XicaraNaMao(bool ver, bool vapor)
        {
            Xicara.Visibility = ver ? Visibility.Visible : Visibility.Collapsed;
            VaporMao.Visibility = vapor ? Visibility.Visible : Visibility.Hidden;
        }

        public void XicaraNaMesa(bool ver) { XicaraMesa.Visibility = ver ? Visibility.Visible : Visibility.Collapsed; }
        public void Vapor(bool ver) { VaporMesa.Visibility = ver ? Visibility.Visible : Visibility.Hidden; }

        public void Aplicar(Quadro q)
        {
            var chave = q.Chave() + "|" + cansado + "|" + concentrado;
            if (chave == ultimo) return;
            ultimo = chave;
            Pulo.X = q.X;
            Pulo.Y = q.Y;
            Pernas.Data = Arte.Pernas[q.Pe];
            bool olhosBaixos = cansado || concentrado;
            double altura = q.Fechado ? 1 : olhosBaixos ? 2 : 4, topo = q.Fechado || olhosBaixos ? 6 : 4;
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

        public bool Concentrado
        {
            get { return concentrado; }
            set { if (concentrado == value) return; concentrado = value; ultimo = ""; }
        }

        public bool Cansado
        {
            get { return cansado; }
            set
            {
                if (cansado == value) return;
                cansado = value;
                Suor.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                ultimo = "";  // redesenha os olhos no próximo quadro
            }
        }
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
        public Canvas Festa;  // confete por cima de tudo, quando o limite renova
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

    // Partícula: um papel de confete (Forma) ou um símbolo de código (Texto, que some em Vida tiques)
    class Confete
    {
        public Shapes.Rectangle Forma;
        public TextBlock Texto;
        public Canvas Onde;
        public double X, Y, VX, VY;
        public int Vida;
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
        bool buscando, claro, escondido, temLeitura;
        int pct;
        string corAnel = Arte.Laranja, textoAnel = "…", chapeuAtual;
        double fonteAnel = 10;

        Window balao;
        FrameworkElement cartao;
        TextBlock pctSessao, pctSemana, renovaSessao, renovaSemana, rodape, semDados, textoNovaVersao;
        Border barraSessao, barraSemana;
        StackPanel extras;
        Canvas grafico;
        Shapes.Path bico;
        Widget donoBalao;

        // versão nova no GitHub
        const string Repositorio = "dev-lucccaslp/claude-widget";
        string novaVersao, urlNovoExe, urlNovidades = "https://github.com/" + Repositorio + "/releases/latest";
        bool baixando, balaoDeVersaoNova;
        Forms.ToolStripMenuItem itemNovaVersao;

        readonly List<Confete> confetes = new List<Confete>();
        DispatcherTimer queda;

        // Claude Code trabalhando: o mascote vai para o notebook e solta símbolos de código
        FileSystemWatcher vigiaClaude;
        long ultimaAtividade;  // DateTime.UtcNow.Ticks da última conversa gravada pelo Claude Code
        bool codando;
        int tique;
        DateTime codandoDemoAte = DateTime.MinValue;
        // pausa para o café: null, "saindo", "fora" ou "voltando"; cafeX é o quanto ele andou (o notebook fica parado)
        string cafe;
        double cafeX;
        int cafeTiques;
        DateTime proximoCafe = DateTime.MaxValue;
        readonly List<Confete> codigos = new List<Confete>();
        static readonly string[] Simbolos = { "</>", "{ }", "( )", "01", "=>", ";", "fn", "[ ]" };
        static readonly string[] CoresDoCodigo = { "#D77757", "#60A5FA", "#4ADE80", "#C084FC", "#FACC15" };
        static readonly FontFamily FonteDeCodigo = new FontFamily("Consolas");

        // fala do mascote: saudação e lembretes
        Window fala;
        TextBlock mensagem;
        Shapes.Path bicoFala;
        DispatcherTimer fimFala;
        DateTime ultimoLembrete = DateTime.Now, ultimaSaudacao = DateTime.MinValue, diaFalados = DateTime.Today;
        string periodoSaudado;
        int proximaCategoria;
        readonly HashSet<string> faladosHoje = new HashSet<string>();

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

            claro = TemaClaro();
            AplicarTema();
            // Trocou o tema do Windows: troca na hora
            SystemEvents.UserPreferenceChanged += delegate { app.Dispatcher.BeginInvoke(new Action(ConferirTema)); };

            CriarBalao();
            CriarFala();
            CriarBandeja();
            // Se o "Iniciar com o Windows" aponta para um .exe antigo, passa a abrir o atual
            if (IniciaComWindows()) IniciarComWindows(true);

            // Lembretes: confere a cada minuto. Desbloqueou o PC num outro período do dia: dá oi de novo.
            NovoTimer(60000, ConferirLembretes);
            SystemEvents.SessionSwitch += (s, e) =>
            {
                if (e.Reason == SessionSwitchReason.SessionUnlock) app.Dispatcher.BeginInvoke(new Action(delegate { Depois(2500, SaudarDeNovo); }));
            };

            animacao = NovoTimer(120, TicAnimacao);
            vigia = NovoTimer(300, Vigiar);
            relogio = NovoTimer(IntervaloMinutos * 60000, delegate { AtualizarUso(); ConferirChapeuDaEstacao(); });
            queda = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            queda.Tick += delegate { CairConfete(); };
            VigiarClaudeCode();

            // Versão nova no GitHub: olha um minuto depois de abrir e depois a cada 12 horas.
            // Também apaga o .exe antigo que ficou da última atualização.
            var versao = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            versao.Tick += delegate
            {
                versao.Interval = TimeSpan.FromHours(12);
                VerificarVersao();
                try { File.Delete(Programa.ExeInstalado + ".old"); } catch { }
            };
            versao.Start();

            // Quando a barra (ou qualquer janela) vem para a frente, reorganiza na hora, sem esperar o timer
            procFrente = delegate { Ordem.Elevar(); };
            Win32.SetWinEventHook(3, 3, IntPtr.Zero, procFrente, 0, 0, 0);

            AtualizarUso();
            Vigiar();
            if (!demo)
            {
                // Saudação: espera o primeiro número do uso chegar (até uns 12 s) para já dar o resumo
                int espera = 0;
                var oi = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                oi.Tick += delegate { if (temLeitura || ++espera >= 12) { oi.Stop(); Saudar(); } };
                oi.Start();
            }
            else
            {
                // Demonstração: oi, confete, um lembrete e depois todos os transportes
                Depois(800, delegate { Saudar(true); });
                Depois(1500, Comemorar);
                Depois(4000, MostrarLembreteAgora);
                Depois(5500, delegate { codandoDemoAte = DateTime.Now.AddSeconds(22); });
                Depois(6500, IniciarCafe);
                // Passa por todos os transportes, um depois do outro, no primeiro widget (o balão é um só)
                int proximo = 0;
                DateTime parouEm = DateTime.MinValue;
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(16) };
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

        // Faz uma vez, daqui a tantos milissegundos
        static void Depois(int ms, Action acao)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += delegate { t.Stop(); acao(); };
            t.Start();
        }

        // --- Fala do mascote: saudação e lembretes ---
        void CriarFala()
        {
            fala = (Window)XamlReader.Parse(Arte.XamlFala);
            mensagem = (TextBlock)fala.FindName("Mensagem");
            bicoFala = (Shapes.Path)fala.FindName("Bico");
            var h = new WindowInteropHelper(fala).EnsureHandle();
            Win32.SetWindowLong(h, Win32.GWL_EXSTYLE, Win32.GetWindowLong(h, Win32.GWL_EXSTYLE) | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
            Ordem.Fala = h;
            fala.MouseLeftButtonUp += delegate { EsconderFala(); };
            fimFala = new DispatcherTimer();
            fimFala.Tick += delegate { EsconderFala(); };
        }

        // Mostra a fala em cima do mascote do primeiro monitor. Com o widget escondido, vira um aviso do Windows.
        // Devolve false se não deu agora (balão de uso aberto, menu aberto, tela cheia): tenta de novo depois.
        bool Falar(string texto)
        {
            if (escondido)
            {
                balaoDeVersaoNova = false;
                bandeja.ShowBalloonTip(8000, "Claude Widget", texto, Forms.ToolTipIcon.None);
                return true;
            }
            var w = widgets.Values.FirstOrDefault(x => x.Janela.IsVisible);
            if (w == null || Ordem.Pausado || donoBalao != null) return false;
            mensagem.Text = texto;
            var cartaoFala = (FrameworkElement)fala.Content;
            cartaoFala.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var tamanho = cartaoFala.DesiredSize;
            double alvo = w.Janela.Left + 24;  // o meio do mascote
            double esquerda = alvo - 32;
            if (!double.IsNaN(w.TelaE)) esquerda = Math.Max(w.TelaE + 4, Math.Min(w.TelaD - tamanho.Width - 4, esquerda));
            bicoFala.Margin = new Thickness(Math.Max(10, Math.Min(tamanho.Width - 42, alvo - esquerda - 16)), -1, 0, 0);
            fala.Left = esquerda;
            fala.Top = w.Janela.Top + Folga + 4 - (tamanho.Height - 8);
            if (!fala.IsVisible) fala.Show();
            Ordem.Elevar();
            fimFala.Stop();
            fimFala.Interval = TimeSpan.FromSeconds(Math.Max(8, texto.Length / 7.0));
            fimFala.Start();
            Animar("pular");
            return true;
        }

        void EsconderFala()
        {
            fimFala.Stop();
            if (fala.IsVisible) fala.Hide();
        }

        // O primeiro nome da conta do Windows ("lucas.lessa" vira "Lucas"); se não parecer um nome, não usa
        static string PrimeiroNome()
        {
            try
            {
                var nome = Environment.UserName.Split('.', '_', '-', ' ')[0];
                if (nome.Length < 2 || nome.Any(char.IsDigit)) return null;
                return char.ToUpper(nome[0]) + nome.Substring(1).ToLower();
            }
            catch { return null; }
        }

        void Saudar() { Saudar(false); }

        void Saudar(bool forcar)
        {
            if (!config.Saudacao && !forcar) return;
            if (!Falar(Falas.Saudacao(DateTime.Now, PrimeiroNome(), temLeitura ? uso : null))) return;
            ultimaSaudacao = DateTime.Now;
            periodoSaudado = Falas.Periodo(DateTime.Now);
        }

        // Voltou ao PC: se mudou o período do dia (ou passou bastante tempo), dá oi de novo
        void SaudarDeNovo()
        {
            if (!config.Saudacao) return;
            if (Falas.Periodo(DateTime.Now) == periodoSaudado && (DateTime.Now - ultimaSaudacao).TotalHours < 4) return;
            Saudar();
        }

        string ProximoLembrete()
        {
            var categorias = new List<string[]>();
            if (config.LembreteAgua) categorias.Add(Falas.Agua);
            if (config.LembreteAlongar) categorias.Add(Falas.Alongar);
            if (config.LembreteVista) categorias.Add(Falas.Vista);
            if (categorias.Count == 0) return null;
            var lista = categorias[proximaCategoria++ % categorias.Count];
            return lista[sorteio.Next(lista.Length)];
        }

        void ConferirLembretes()
        {
            var agora = DateTime.Now;
            if (agora.Date != diaFalados) { faladosHoje.Clear(); diaFalados = agora.Date; }
            if (Win32.Ocioso().TotalMinutes >= 5) return;  // longe do PC: não fala sozinho
            if (config.LembreteHorarios)
            {
                var doHorario = Falas.DoHorario(agora);
                if (doHorario != null)
                {
                    var partes = doHorario.Split('|');
                    if (!faladosHoje.Contains(partes[0]) && Falar(partes[1])) { faladosHoje.Add(partes[0]); ultimoLembrete = agora; return; }
                }
            }
            if ((agora - ultimoLembrete).TotalMinutes < config.IntervaloLembretes) return;
            var texto = ProximoLembrete();
            if (texto != null && Falar(texto)) ultimoLembrete = agora;
        }

        // "Mostrar um agora": espera o menu fechar antes de falar
        void MostrarLembreteAgora()
        {
            Depois(400, delegate { Falar(ProximoLembrete() ?? Falas.Agua[0]); });
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
            EsconderFala();
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
            ConferirTema();
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
                // O limite renovou (caiu 10 pontos ou mais desde a última leitura): festa!
                bool renovou = temLeitura && (novo.Sessao + 10 <= uso.Sessao || novo.Semana + 10 <= uso.Semana);
                uso = novo;
                temLeitura = true;
                Historico.Adicionar(novo.Semana);
                relogio.Interval = TimeSpan.FromMinutes(IntervaloMinutos);
                if (renovou) Comemorar();
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
            foreach (var w in widgets.Values)
            {
                MostrarEstado(w);
                // Cansado perto do limite (do que o anel mostra)
                w.Principal.Cansado = uso.Erro == null && pct >= 90;
            }
            if (bandeja != null)
            {
                var dica = uso.Erro != null ? "Claude Widget · " + uso.Erro : "Claude · 5h " + uso.Sessao + "% · semana " + uso.Semana + "%";
                if (codando) dica = "Claude trabalhando · " + dica.Replace("Claude · ", "");
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

        // Balão e menu usam as cores de Arte.Tema pelo nome (DynamicResource); trocar aqui troca em tudo
        void AplicarTema()
        {
            foreach (var cor in Arte.Tema) app.Resources[cor.Key] = Arte.Pincel(claro ? cor.Value[1] : cor.Value[0]);
        }

        void ConferirTema()
        {
            bool agora = TemaClaro();
            if (agora == claro) return;
            claro = agora;
            AplicarTema();
            MostrarUso();
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
            extras = (StackPanel)balao.FindName("Extras");
            grafico = (Canvas)balao.FindName("Grafico");
            semDados = (TextBlock)balao.FindName("SemDados");
            textoNovaVersao = (TextBlock)balao.FindName("NovaVersao");
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
            rodape.Text = uso.Hora + (codando ? "\nClaude Code trabalhando agora 💻" : "");
            PreencherExtras();
            DesenharGrafico();
            textoNovaVersao.Text = novaVersao == null ? "" : "Versão " + novaVersao + " disponível: atualize pelo ícone nos ícones ocultos";
            textoNovaVersao.Visibility = novaVersao == null ? Visibility.Collapsed : Visibility.Visible;
        }

        // Limites semanais por modelo e créditos extras, quando a conta tiver
        void PreencherExtras()
        {
            extras.Children.Clear();
            foreach (var m in uso.Modelos) AdicionarLimite("Semanal · " + m[0], int.Parse(m[1]), m[2]);
            if (uso.Creditos != null)
            {
                var texto = new TextBlock { Text = uso.Creditos, FontSize = 12, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
                texto.SetResourceReference(TextBlock.ForegroundProperty, "Texto2");
                extras.Children.Add(texto);
                if (uso.CreditosPct >= 0) extras.Children.Add(Barra(uso.CreditosPct));
            }
            extras.Visibility = extras.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void AdicionarLimite(string titulo, double valor, string renova)
        {
            var linha = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            var nome = new TextBlock { Text = titulo, FontSize = 12 };
            nome.SetResourceReference(TextBlock.ForegroundProperty, "Texto2");
            var numero = new TextBlock { Text = valor + "%", FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
            numero.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
            linha.Children.Add(nome);
            linha.Children.Add(numero);
            extras.Children.Add(linha);
            extras.Children.Add(Barra(valor));
            if (string.IsNullOrEmpty(renova)) return;
            var quando = new TextBlock { Text = renova, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
            quando.SetResourceReference(TextBlock.ForegroundProperty, "Texto3");
            extras.Children.Add(quando);
        }

        static FrameworkElement Barra(double valor)
        {
            var fundo = new Border { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 5, 0, 0) };
            fundo.SetResourceReference(Border.BackgroundProperty, "Trilho");
            fundo.Child = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(2),
                Width = LarguraBarra * Math.Min(100, Math.Max(0, valor)) / 100.0,
                Background = Arte.Pincel(Arte.CorDoUso(valor)),
            };
            return fundo;
        }

        // Uma barra por dia: quanto do limite semanal foi gasto naquele dia (hoje é a última, mais forte)
        void DesenharGrafico()
        {
            grafico.Children.Clear();
            bool temDados;
            var dias = Historico.PorDia(7, out temDados);
            double maior = Math.Max(10, dias.Max());
            const double alturaBarras = 30, topo = 12;
            double passo = LarguraBarra / 7, largura = passo - 8;
            var ptBR = new CultureInfo("pt-BR");
            for (int i = 0; i < 7; i++)
            {
                bool hoje = i == 6;
                double altura = dias[i] <= 0 ? 2 : Math.Max(3, alturaBarras * dias[i] / maior);
                var barra = new Border { Width = largura, Height = altura, CornerRadius = new CornerRadius(2) };
                if (dias[i] <= 0) barra.SetResourceReference(Border.BackgroundProperty, "Trilho");
                else barra.Background = Arte.Pincel(hoje ? Arte.Laranja : "#99D77757");
                Canvas.SetLeft(barra, i * passo + 4);
                Canvas.SetTop(barra, topo + alturaBarras - altura);
                grafico.Children.Add(barra);
                if (dias[i] > 0)
                {
                    var valor = new TextBlock { Text = Math.Round(dias[i]) + "%", FontSize = 9, Width = passo, TextAlignment = TextAlignment.Center };
                    valor.SetResourceReference(TextBlock.ForegroundProperty, "Texto3");
                    Canvas.SetLeft(valor, i * passo);
                    Canvas.SetTop(valor, topo + alturaBarras - altura - 12);
                    grafico.Children.Add(valor);
                }
                var dia = DateTime.Today.AddDays(i - 6);
                var rotulo = new TextBlock
                {
                    Text = hoje ? "hoje" : ptBR.DateTimeFormat.GetAbbreviatedDayName(dia.DayOfWeek).TrimEnd('.'),
                    FontSize = 10, Width = passo, TextAlignment = TextAlignment.Center,
                    FontWeight = hoje ? FontWeights.SemiBold : FontWeights.Normal,
                };
                rotulo.SetResourceReference(TextBlock.ForegroundProperty, hoje ? "Texto" : "Texto3");
                Canvas.SetLeft(rotulo, i * passo);
                Canvas.SetTop(rotulo, topo + alturaBarras + 2);
                grafico.Children.Add(rotulo);
            }
            semDados.Visibility = temDados ? Visibility.Collapsed : Visibility.Visible;
        }

        // O bico aponta para o visitante (ou para o anel, se o visitante estiver desligado); o balão não sai da tela
        void MostrarBalao(Widget w)
        {
            EsconderFala();
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
            menu.Items.Add(new Forms.ToolStripMenuItem("Claude Widget " + VersaoCurta) { Enabled = false });
            itemNovaVersao = new Forms.ToolStripMenuItem("Atualizar") { Visible = false, Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold) };
            itemNovaVersao.Click += delegate { AtualizarVersao(); };
            menu.Items.Add(itemNovaVersao);
            menu.Items.Add(new Forms.ToolStripSeparator());
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
            var itemAvisar = new Forms.ToolStripMenuItem("Avisar quando sair versão nova");
            itemAvisar.Click += delegate
            {
                config.AvisarVersaoNova = !config.AvisarVersaoNova;
                config.Salvar();
                if (config.AvisarVersaoNova) VerificarVersao();
            };
            menu.Items.Add(itemAvisar);
            menu.Items.Add("Ver novidades", null, delegate { try { Process.Start(urlNovidades); } catch { } });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Sair", null, delegate { Sair(); });
            bandeja.BalloonTipClicked += delegate { if (balaoDeVersaoNova) AtualizarVersao(); };
            bandeja.BalloonTipClosed += delegate { balaoDeVersaoNova = false; };
            menu.Opening += delegate
            {
                itemMostrar.Text = escondido ? "Mostrar widget" : "Esconder widget";
                itemWindows.Checked = IniciaComWindows();
                itemAvisar.Checked = config.AvisarVersaoNova;
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

        // Com o instalador, abre a cópia instalada (que é atualizada sozinha); sem ele, este .exe
        static string CaminhoParaAbrir()
        {
            return File.Exists(Programa.ExeInstalado) ? Programa.ExeInstalado : Assembly.GetExecutingAssembly().Location;
        }

        static void IniciarComWindows(bool ligar)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(ChaveRun))
                {
                    if (ligar) k.SetValue("ClaudeWidget", "\"" + CaminhoParaAbrir() + "\"");
                    else k.DeleteValue("ClaudeWidget", false);
                }
            }
            catch { }
        }

        static string VersaoCurta { get { var v = Programa.Versao; return v.Major + "." + v.Minor + "." + v.Build; } }

        public void AvisarAtualizacao()
        {
            if (bandeja == null) return;
            balaoDeVersaoNova = false;
            bandeja.ShowBalloonTip(6000, "Claude Widget " + VersaoCurta,
                "Atualizado! Clique no mascote para ver as opções novas.", Forms.ToolTipIcon.Info);
        }

        // --- Versão nova no GitHub ---
        bool atualizarSozinho;  // --atualizar: se achar versão nova, já baixa e troca

        public void AtualizarAgora()
        {
            atualizarSozinho = true;
            if (urlNovoExe != null) AtualizarVersao();
            else VerificarVersao(true);
        }

        void VerificarVersao(bool forcar = false)
        {
            if (!config.AvisarVersaoNova && !forcar) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repositorio + "/releases/latest");
                    req.UserAgent = "claude-widget";
                    req.Accept = "application/vnd.github+json";
                    req.Timeout = 15000;
                    string json;
                    using (var resp = req.GetResponse())
                    using (var leitor = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        json = leitor.ReadToEnd();
                    var r = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                    var tag = (r["tag_name"] as string ?? "").TrimStart('v', 'V');
                    Version versao;
                    if (!Version.TryParse(tag, out versao)) return;
                    var atual = Programa.Versao;
                    if (versao <= new Version(atual.Major, atual.Minor, atual.Build)) return;
                    string url = null;
                    var arquivos = r["assets"] as System.Collections.IEnumerable;
                    if (arquivos != null)
                        foreach (var a in arquivos)
                        {
                            var d = a as Dictionary<string, object>;
                            if (d != null && (d["name"] as string) == "ClaudeWidget.exe") url = d["browser_download_url"] as string;
                        }
                    var pagina = r["html_url"] as string;
                    if (url == null) return;
                    app.Dispatcher.BeginInvoke(new Action(delegate { VersaoNovaDisponivel(tag, url, pagina); }));
                }
                catch { }
            });
        }

        void VersaoNovaDisponivel(string versao, string url, string pagina)
        {
            novaVersao = versao;
            urlNovoExe = url;
            if (pagina != null) urlNovidades = pagina;
            itemNovaVersao.Text = "Atualizar para a versão " + versao;
            itemNovaVersao.Visible = true;
            if (donoBalao != null) MostrarBalao(donoBalao);
            if (atualizarSozinho) { AtualizarVersao(); return; }
            if (config.VersaoAvisada == versao) return;  // avisa de cada versão uma vez só
            config.VersaoAvisada = versao;
            config.Salvar();
            balaoDeVersaoNova = true;
            bandeja.ShowBalloonTip(10000, "Claude Widget " + versao + " disponível",
                "Clique aqui para atualizar (ou depois, pelo ícone nos ícones ocultos).", Forms.ToolTipIcon.Info);
        }

        // Baixa o .exe novo da Release, confere que é ele mesmo, troca e abre; a versão nova fecha esta e assume o lugar
        void AtualizarVersao()
        {
            if (urlNovoExe == null || baixando) return;
            baixando = true;
            balaoDeVersaoNova = false;
            bandeja.ShowBalloonTip(4000, "Claude Widget", "Baixando a versão " + novaVersao + "…", Forms.ToolTipIcon.Info);
            var url = urlNovoExe;
            var versao = novaVersao;
            var baixado = Programa.ExeInstalado + ".baixando";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string erro = null;
                try
                {
                    Directory.CreateDirectory(Config.Pasta);
                    using (var cliente = new WebClient())
                    {
                        cliente.Headers[HttpRequestHeader.UserAgent] = "claude-widget";
                        cliente.DownloadFile(url, baixado);
                    }
                    var info = FileVersionInfo.GetVersionInfo(baixado);
                    if (info.ProductName != "Claude Widget" || info.FileVersion == null || !info.FileVersion.StartsWith(versao))
                        throw new Exception("o arquivo baixado não é a versão esperada");
                }
                catch (Exception ex) { erro = ex.Message; }
                app.Dispatcher.BeginInvoke(new Action(delegate { TrocarPelaNova(baixado, erro); }));
            });
        }

        void TrocarPelaNova(string baixado, string erro)
        {
            baixando = false;
            if (erro == null)
            {
                try
                {
                    var destino = Programa.ExeInstalado;
                    var este = Assembly.GetExecutingAssembly().Location;
                    var velho = destino + ".old";
                    if (File.Exists(velho)) File.Delete(velho);
                    // o .exe aberto não pode ser sobrescrito, mas pode ser renomeado
                    if (string.Equals(System.IO.Path.GetFullPath(destino), System.IO.Path.GetFullPath(este), StringComparison.OrdinalIgnoreCase)) File.Move(destino, velho);
                    else if (File.Exists(destino)) File.Delete(destino);
                    File.Move(baixado, destino);
                    Process.Start(destino);
                    return;
                }
                catch (Exception ex) { erro = ex.Message; }
            }
            try { File.Delete(baixado); } catch { }
            bandeja.ShowBalloonTip(8000, "Não deu para atualizar", erro + ". Vou abrir a página para baixar à mão.", Forms.ToolTipIcon.Warning);
            try { Process.Start(urlNovidades); } catch { }
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

        // Na época do Natal, Halloween, festa junina e Ano-Novo, o chapéu da estação ganha do escolhido
        string ChapeuEfetivo()
        {
            var estacao = config.ChapeuEstacao ? Arte.ChapeuDaEstacao(DateTime.Today) : null;
            return estacao ?? config.Chapeu;
        }

        void EstiloDe(Widget w)
        {
            chapeuAtual = ChapeuEfetivo();
            w.Principal.Estilo(config.Cor, chapeuAtual, config.Oculos);
            w.Visitante.Estilo(config.Cor, chapeuAtual, config.Oculos);
        }

        // Virou o dia e entrou (ou saiu) uma estação: troca o chapéu
        void ConferirChapeuDaEstacao()
        {
            if (ChapeuEfetivo() == chapeuAtual) return;
            foreach (var w in widgets.Values) EstiloDe(w);
            Animar("pular");
        }

        // --- Comemoração quando o limite renova: pulos, dancinha e confete ---
        static readonly string[] CoresDoConfete = { "#F59E0B", "#EF4444", "#3B82F6", "#22C55E", "#A855F7", "#EC4899", "#FACC15" };

        public void Comemorar()
        {
            if (!config.Movimento || !SystemParameters.ClientAreaAnimation) return;
            Animar("festa");
            foreach (var w in widgets.Values)
            {
                if (!w.Janela.IsVisible) continue;
                // estoura de cima do mascote: sobe, abre para os lados e cai
                for (int i = 0; i < 40; i++)
                {
                    var c = new Confete
                    {
                        Onde = w.Festa,
                        X = 14 + sorteio.NextDouble() * 24,
                        Y = 12 + sorteio.NextDouble() * 6,
                        VX = (sorteio.NextDouble() - 0.5) * 3.2,
                        VY = -1.2 - sorteio.NextDouble() * 1.6,
                        Forma = new Shapes.Rectangle
                        {
                            Width = sorteio.Next(2) == 0 ? 2 : 3,
                            Height = sorteio.Next(2) == 0 ? 3 : 2,
                            Fill = Arte.Pincel(CoresDoConfete[sorteio.Next(CoresDoConfete.Length)]),
                        },
                    };
                    w.Festa.Children.Add(c.Forma);
                    confetes.Add(c);
                }
            }
            if (confetes.Count > 0) { CairConfete(); queda.Start(); }
        }

        // --- Claude Code trabalhando ---
        // O Claude Code grava cada conversa em ~/.claude/projects/<projeto>/<sessão>.jsonl. Só olha QUANDO um desses
        // arquivos muda (não lê o conteúdo): mudou nos últimos 20 segundos, ele está trabalhando.
        void VigiarClaudeCode()
        {
            try
            {
                var pasta = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
                if (!Directory.Exists(pasta)) return;
                vigiaClaude = new FileSystemWatcher(pasta, "*.jsonl")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                };
                FileSystemEventHandler marcar = delegate { Interlocked.Exchange(ref ultimaAtividade, DateTime.UtcNow.Ticks); };
                vigiaClaude.Changed += marcar;
                vigiaClaude.Created += marcar;
                vigiaClaude.EnableRaisingEvents = true;
            }
            catch { }
        }

        bool ClaudeTrabalhando()
        {
            return DateTime.UtcNow.Ticks - Interlocked.Read(ref ultimaAtividade) < TimeSpan.FromSeconds(20).Ticks
                || DateTime.Now < codandoDemoAte;
        }

        // Liga ou desliga o notebook quando o Claude Code começa ou para de trabalhar
        void AtualizarCodando()
        {
            bool agora = config.Codando && ClaudeTrabalhando();
            if (agora == codando) return;
            codando = agora;
            fila.Clear();
            // o primeiro café sai depois de uns 15 a 25 minutos trabalhando
            if (codando) proximoCafe = DateTime.Now.AddMinutes(15 + sorteio.Next(11));
            else AbortarCafe();
            foreach (var w in widgets.Values) MontarNotebook(w);
            MostrarUso();
        }

        void MontarNotebook(Widget w)
        {
            w.Principal.Montar(codando ? Arte.Notebook : Arte.Transportes[0]);
            w.Principal.Fase(false, false);
            w.Principal.Concentrado = codando;
            if (!codando) w.Principal.XicaraNaMesa(false);
        }

        // --- Pausa para o café: avisa, levanta, sai andando, volta com a xícara e deixa na mesa ---
        void IniciarCafe()
        {
            if (cafe != null) return;
            if (!codando || Ordem.Pausado || widgets.Values.Any(w => w.Visita != "fora"))
            {
                proximoCafe = DateTime.Now.AddMinutes(2);  // agora não dá: tenta daqui a pouco
                return;
            }
            cafe = "saindo";
            cafeX = 0;
            cafeTiques = 0;
            foreach (var w in widgets.Values)
            {
                w.Principal.Fase(false, false);
                w.Principal.Concentrado = false;
                w.Principal.XicaraNaMesa(false);
            }
            Falar("Vou buscar um café ☕ Já volto!");
            proximoCafe = DateTime.Now.AddMinutes(15 + sorteio.Next(11));
        }

        void PassoDoCafe()
        {
            cafeTiques++;
            bool par = cafeTiques % 2 == 0;
            var q = new Quadro { Pe = par ? "A" : "B", Y = par ? -1 : 0 };
            if (cafe == "saindo")
            {
                cafeX -= 3;
                q.Olhar = -2;
                if (cafeX <= -46) { cafe = "fora"; cafeTiques = 0; }
            }
            else if (cafe == "fora")
            {
                q = new Quadro();
                if (cafeTiques >= 34) { cafe = "voltando"; cafeTiques = 0; }  // uns 4 segundos na copa
            }
            else
            {
                cafeX += 3;
                q.Olhar = 2;
                if (cafeX >= 0) { cafeX = 0; cafe = null; q = new Quadro(); }
            }
            foreach (var w in widgets.Values)
            {
                w.Principal.Assento.X = cafeX;
                w.Principal.Aplicar(q);
                w.Principal.XicaraNaMao(cafe == "voltando", par);
            }
            if (cafe == null) Voltou();
        }

        void Voltou()
        {
            foreach (var w in widgets.Values)
            {
                w.Principal.XicaraNaMao(false, false);
                w.Principal.XicaraNaMesa(true);
                w.Principal.Concentrado = codando;
            }
            Falar("Voltei! Café na mão, bora codar ☕");
        }

        void AbortarCafe()
        {
            cafe = null;
            cafeX = 0;
            foreach (var w in widgets.Values)
            {
                w.Principal.Assento.X = 0;
                w.Principal.XicaraNaMao(false, false);
            }
        }

        // Um símbolo de código sai do notebook, sobe e some
        void SoltarCodigo()
        {
            foreach (var w in widgets.Values)
            {
                if (!w.Janela.IsVisible) continue;
                var texto = new TextBlock
                {
                    Text = Simbolos[sorteio.Next(Simbolos.Length)],
                    FontSize = 7.5,
                    FontFamily = FonteDeCodigo,
                    FontWeight = FontWeights.Bold,
                    Foreground = Arte.Pincel(CoresDoCodigo[sorteio.Next(CoresDoCodigo.Length)]),
                };
                // sai pelos lados do notebook, para não passar na frente do rosto
                bool esquerda = sorteio.Next(2) == 0;
                var c = new Confete
                {
                    Texto = texto, Onde = w.Festa, Vida = 36, VY = -0.5,
                    X = esquerda ? -2 + sorteio.NextDouble() * 6 : 34 + sorteio.NextDouble() * 6,
                    Y = 24 + sorteio.NextDouble() * 4,
                    VX = esquerda ? -0.25 : 0.25,
                };
                w.Festa.Children.Add(texto);
                codigos.Add(c);
            }
            if (codigos.Count > 0 && !queda.IsEnabled) queda.Start();
        }

        void CairConfete()
        {
            for (int i = codigos.Count - 1; i >= 0; i--)
            {
                var c = codigos[i];
                c.X += c.VX;
                c.Y += c.VY;
                if (--c.Vida <= 0) { c.Onde.Children.Remove(c.Texto); codigos.RemoveAt(i); continue; }
                c.Texto.Opacity = Math.Min(1, c.Vida / 18.0);
                Canvas.SetLeft(c.Texto, c.X);
                Canvas.SetTop(c.Texto, c.Y);
            }
            for (int i = confetes.Count - 1; i >= 0; i--)
            {
                var c = confetes[i];
                c.X += c.VX;
                c.Y += c.VY;
                c.VX *= 0.985;
                c.VY = Math.Min(c.VY + 0.07, 1.4);  // gravidade, com queda devagar como papel
                if (c.Y > 48) { c.Onde.Children.Remove(c.Forma); confetes.RemoveAt(i); continue; }
                Canvas.SetLeft(c.Forma, c.X);
                Canvas.SetTop(c.Forma, c.Y);
            }
            if (confetes.Count == 0 && codigos.Count == 0) queda.Stop();
        }

        void CriarMenu(Widget w)
        {
            var menu = new ContextMenu();
            var atualizar = Opcao(menu, "", AtualizarVersao);
            atualizar.FontWeight = FontWeights.SemiBold;
            atualizar.Visibility = Visibility.Collapsed;
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
            chapeus.Items.Add(new Separator());
            Opcao(chapeus, "Chapéu da estação (Natal, Halloween, festa junina, Ano-Novo)",
                delegate { config.ChapeuEstacao = !config.ChapeuEstacao; MudouEstilo(); }, () => config.ChapeuEstacao, w);
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
            Opcao(menu, "Claude codando (quando o Claude Code estiver trabalhando)", delegate { config.Codando = !config.Codando; config.Salvar(); }, () => config.Codando, w);
            Opcao(menu, "Visitante ao passar o mouse", delegate { config.Visitante = !config.Visitante; MudouEstilo(); }, () => config.Visitante, w);
            var chegada = Opcao(menu, "Visitante chega de");
            foreach (var transporte in Arte.Transportes)
            {
                var id = transporte.Id;
                Opcao(chegada, transporte.Nome, delegate { config.Transporte = id; config.Salvar(); }, () => config.Transporte == id, w);
            }

            var lembretes = Opcao(menu, "Lembretes");
            Opcao(lembretes, "Saudação (bom dia, boa tarde, boa noite)", delegate { config.Saudacao = !config.Saudacao; config.Salvar(); }, () => config.Saudacao, w);
            Opcao(lembretes, "Beber água", delegate { config.LembreteAgua = !config.LembreteAgua; config.Salvar(); }, () => config.LembreteAgua, w);
            Opcao(lembretes, "Alongar e postura", delegate { config.LembreteAlongar = !config.LembreteAlongar; config.Salvar(); }, () => config.LembreteAlongar, w);
            Opcao(lembretes, "Descansar a vista", delegate { config.LembreteVista = !config.LembreteVista; config.Salvar(); }, () => config.LembreteVista, w);
            Opcao(lembretes, "Almoço, café e fim do dia", delegate { config.LembreteHorarios = !config.LembreteHorarios; config.Salvar(); }, () => config.LembreteHorarios, w);
            lembretes.Items.Add(new Separator());
            foreach (var frequencia in new[] { new[] { "A cada 30 minutos", "30" }, new[] { "A cada 1 hora", "60" }, new[] { "A cada 2 horas", "120" } })
            {
                int minutos = int.Parse(frequencia[1]);
                Opcao(lembretes, frequencia[0], delegate { config.IntervaloLembretes = minutos; config.Salvar(); }, () => config.IntervaloLembretes == minutos, w);
            }
            lembretes.Items.Add(new Separator());
            Opcao(lembretes, "Mostrar um agora", MostrarLembreteAgora);
            menu.Items.Add(new Separator());
            Opcao(menu, "Esconder (fica nos ícones ocultos)", delegate { Esconder(true); });
            Opcao(menu, "Sair", Sair);

            menu.Opened += delegate
            {
                foreach (var m in w.Marcas) m.Key.IsChecked = m.Value();
                atualizar.Header = novaVersao == null ? "" : "Atualizar para a versão " + novaVersao;
                atualizar.Visibility = novaVersao == null ? Visibility.Collapsed : Visibility.Visible;
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
                Festa = (Canvas)janela.FindName("Festa"),
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
            if (codando) MontarNotebook(w);
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
            bool cansado = widgets.Values.Any(w => w.Principal.Cansado);
            var movimentos = cansado ? Arte.MovimentosCansado : Arte.Movimentos;
            var pesos = cansado ? Arte.PesosCansado : Arte.Pesos;
            int sorte = sorteio.Next(100);
            for (int i = 0; i < movimentos.Length; i++)
            {
                sorte -= pesos[i];
                if (sorte < 0) return movimentos[i];
            }
            return "respirar";
        }

        void TicAnimacao()
        {
            // Respeita a opção do menu e "Efeitos de animação" desligado no Windows
            bool mexer = config.Movimento && SystemParameters.ClientAreaAnimation;
            AtualizarCodando();
            tique++;
            if (mexer && cafe != null) PassoDoCafe();
            else if (mexer)
            {
                // no notebook ele só digita; fora dele, sorteia o próximo movimento
                if (fila.Count == 0) Animar(codando ? "digitar" : Sortear());
                var q = fila.Dequeue();
                foreach (var w in widgets.Values)
                {
                    var quadro = q;
                    // com visita, o mascote olha para o visitante (no notebook, continua olhando o teclado)
                    if (w.Visita != "fora" && !codando) { quadro = q.Copia(); quadro.Olhar = 2; }
                    w.Principal.Aplicar(quadro);
                    if (codando) w.Principal.Fase(true, tique % 2 == 0);
                    w.Principal.Vapor(tique % 8 < 4);
                }
                if (codando && tique % 6 == 0) SoltarCodigo();
                if (codando && DateTime.Now >= proximoCafe) IniciarCafe();
            }
            foreach (var w in widgets.Values) PassoDaVisita(w, mexer);
        }

        // Visitante: chega pela direita (a pé, de skate, bicicleta, carro ou foguete) quando o mouse entra
        // e vai embora quando sai
        Transporte TransporteAtual() { return Arte.AcharTransporte(transporteDemo ?? config.Transporte); }

        void IniciarVisita(Widget w)
        {
            if (w.Arrastando || Ordem.Pausado || escondido) return;
            EsconderFala();
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
        const string EventoAtualizar = @"Local\ClaudeWidget.Atualizar";

        public static Version Versao { get { return Assembly.GetExecutingAssembly().GetName().Version; } }
        static string EsteExe { get { return Assembly.GetExecutingAssembly().Location; } }
        public static string ExeInstalado { get { return System.IO.Path.Combine(Config.Pasta, "ClaudeWidget.exe"); } }
        // A cópia aberta deixa aqui a versão dela, para uma versão mais nova saber que pode assumir o lugar
        static string ArquivoAberto { get { return System.IO.Path.Combine(Config.Pasta, "aberto.txt"); } }

        static void Sinalizar(string evento)
        {
            try { using (var ev = EventWaitHandle.OpenExisting(evento)) ev.Set(); }
            catch { }
        }

        // A cópia aberta é mais velha que esta? A 1.2.0 e as anteriores não deixam aberto.txt, então contam como mais velhas.
        static bool AbertaEhMaisVelha()
        {
            try { return new Version(File.ReadAllText(ArquivoAberto).Split('|')[0].Trim()) < Versao; }
            catch { return true; }
        }

        // A primeira versão (PowerShell) não escuta o pedido para sair e usa o mesmo nome de trava
        static void FecharVersaoPowerShell()
        {
            try
            {
                using (var busca = new System.Management.ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'powershell.exe'"))
                    foreach (System.Management.ManagementObject p in busca.Get())
                    {
                        var linha = p["CommandLine"] as string;
                        if (linha == null || !(linha.Contains("claude-widget.ps1") || linha.Contains("claude-barra.ps1"))) continue;
                        try { Process.GetProcessById(Convert.ToInt32(p["ProcessId"])).Kill(); } catch { }
                    }
            }
            catch { }
        }

        // Quem usou o instalador abre pelos atalhos, que apontam para a cópia instalada: se ela for mais velha, troca
        static void AtualizarCopiaInstalada()
        {
            try
            {
                var instalado = ExeInstalado;
                if (!File.Exists(instalado) || string.Equals(System.IO.Path.GetFullPath(instalado), System.IO.Path.GetFullPath(EsteExe), StringComparison.OrdinalIgnoreCase)) return;
                if (new Version(FileVersionInfo.GetVersionInfo(instalado).FileVersion) < Versao) File.Copy(EsteExe, instalado, true);
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool sair = args.Contains("--sair"), demo = args.Contains("--demo"), atualizar = args.Contains("--atualizar");
            bool criou, atualizou = false;
            using (var mutex = new Mutex(true, NomeMutex, out criou))
            {
                if (!criou)
                {
                    if (sair) { Sinalizar(EventoSair); return; }
                    // mesma versão (ou mais nova): pede para a aberta aparecer de novo, ou para ela se atualizar
                    if (!AbertaEhMaisVelha()) { Sinalizar(atualizar ? EventoAtualizar : EventoMostrar); return; }
                    // Esta versão é mais nova: pede para a aberta sair e assume o lugar dela
                    Sinalizar(EventoSair);
                    FecharVersaoPowerShell();
                    try { criou = mutex.WaitOne(10000); }
                    catch (AbandonedMutexException) { criou = true; }
                    if (!criou) { Sinalizar(EventoMostrar); return; }
                    atualizou = true;
                }
                if (sair) return;
                AtualizarCopiaInstalada();
                try
                {
                    Directory.CreateDirectory(Config.Pasta);
                    File.WriteAllText(ArquivoAberto, Versao + "|" + EsteExe);
                }
                catch { }

                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                Forms.Application.EnableVisualStyles();
                // Desenho pela CPU: as janelas são minúsculas, e pela placa de vídeo o driver reserva centenas de MB à toa
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

                // sobra da última atualização pelo ícone (o .exe antigo, renomeado)
                try { File.Delete(ExeInstalado + ".old"); } catch { }

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Arte.XamlEstilos));
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
                if (atualizou) controle.AvisarAtualizacao();
                if (atualizar) controle.AtualizarAgora();

                var mostrar = new EventWaitHandle(false, EventResetMode.AutoReset, EventoMostrar);
                var fechar = new EventWaitHandle(false, EventResetMode.AutoReset, EventoSair);
                var atualizarAgora = new EventWaitHandle(false, EventResetMode.AutoReset, EventoAtualizar);
                var escuta = new Thread(delegate ()
                {
                    var eventos = new WaitHandle[] { mostrar, fechar, atualizarAgora };
                    while (true)
                    {
                        int qual = WaitHandle.WaitAny(eventos);
                        if (qual == 0) app.Dispatcher.BeginInvoke(new Action(controle.Mostrar));
                        else if (qual == 2) app.Dispatcher.BeginInvoke(new Action(controle.AtualizarAgora));
                        else { app.Dispatcher.BeginInvoke(new Action(controle.Sair)); return; }
                    }
                });
                escuta.IsBackground = true;
                escuta.Start();

                app.Run();
                try { File.Delete(ArquivoAberto); } catch { }
                mutex.ReleaseMutex();
            }
        }
    }
}
