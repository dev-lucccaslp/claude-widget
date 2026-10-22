// Claude Widget para macOS: o mascote do Claude Code na barra de menus, com o % de uso do plano do Claude.
// https://github.com/dev-lucccaslp/claude-widget
//
// Compilado com swiftc pelo mac/build.sh (o GitHub compila numa máquina macOS a cada versão).
// Lê o login do Claude Code nas Chaves do macOS (item "Claude Code-credentials") ou em ~/.claude/.credentials.json
// e consulta o uso em api.anthropic.com. Não renova nem grava o login. Uma vez a cada 12 horas olha a página de
// Releases no GitHub para avisar de versão nova (dá para desligar no menu). As escolhas ficam nos ajustes do app
// (UserDefaults) e o histórico do gráfico também.

import AppKit
import Combine
import Security
import ServiceManagement
import SwiftUI

let repositorio = "dev-lucccaslp/claude-widget"
let laranja = "#D77757"

// MARK: - Escolhas

final class Escolhas {
    private let d = UserDefaults.standard
    private func bool(_ chave: String, _ padrao: Bool) -> Bool { d.object(forKey: chave) as? Bool ?? padrao }

    var mostrar: String { get { d.string(forKey: "mostrar") ?? "maior" } set { d.set(newValue, forKey: "mostrar") } }
    var cor: String { get { d.string(forKey: "cor") ?? laranja } set { d.set(newValue, forKey: "cor") } }
    var chapeu: String { get { d.string(forKey: "chapeu") ?? "nenhum" } set { d.set(newValue, forKey: "chapeu") } }
    var chapeuEstacao: Bool { get { bool("chapeuEstacao", true) } set { d.set(newValue, forKey: "chapeuEstacao") } }
    var oculos: Bool { get { bool("oculos", false) } set { d.set(newValue, forKey: "oculos") } }
    var movimento: Bool { get { bool("movimento", true) } set { d.set(newValue, forKey: "movimento") } }
    var codando: Bool { get { bool("codando", true) } set { d.set(newValue, forKey: "codando") } }
    var saudacao: Bool { get { bool("saudacao", true) } set { d.set(newValue, forKey: "saudacao") } }
    var lembretes: Bool { get { bool("lembretes", true) } set { d.set(newValue, forKey: "lembretes") } }
    var avisarVersao: Bool { get { bool("avisarVersao", true) } set { d.set(newValue, forKey: "avisarVersao") } }
    var versaoAvisada: String? { get { d.string(forKey: "versaoAvisada") } set { d.set(newValue, forKey: "versaoAvisada") } }

    // Histórico do % semanal para o gráfico dos últimos 7 dias: pares [segundos desde 1970, %]
    var historico: [[Double]] { get { d.array(forKey: "historico") as? [[Double]] ?? [] } set { d.set(newValue, forKey: "historico") } }
}

let escolhas = Escolhas()

// MARK: - Uso do plano

struct Limite: Identifiable {
    let id = UUID()
    let nome: String
    let pct: Int
    let renova: String
}

final class Uso: ObservableObject {
    @Published var sessao = 0
    @Published var semana = 0
    @Published var renovaSessao = ""
    @Published var renovaSemana = ""
    @Published var modelos: [Limite] = []
    @Published var hora = "Carregando…"
    @Published var erro: String?
    @Published var codando = false
    @Published var novaVersao: String?
    @Published var dias: [Double] = Array(repeating: 0, count: 7)
    var temLeitura = false
}

struct Leitura {
    var sessao = 0, semana = 0
    var renovaSessao = "", renovaSemana = ""
    var modelos: [Limite] = []
    var erro: String?
}

enum ApiUso {
    // O Claude Code guarda o login nas Chaves do macOS; em outros casos, num arquivo
    static func token() -> String? {
        let busca: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: "Claude Code-credentials",
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        if SecItemCopyMatching(busca as CFDictionary, &item) == errSecSuccess, let dados = item as? Data, let t = extrair(dados) {
            return t
        }
        let arquivo = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude/.credentials.json")
        if let dados = try? Data(contentsOf: arquivo), let t = extrair(dados) { return t }
        return nil
    }

    static func extrair(_ dados: Data) -> String? {
        guard let json = try? JSONSerialization.jsonObject(with: dados) as? [String: Any],
              let oauth = json["claudeAiOauth"] as? [String: Any] else { return nil }
        return oauth["accessToken"] as? String
    }

    static func buscar(_ pronto: @escaping (Leitura) -> Void) {
        guard let token = token() else {
            pronto(Leitura(erro: "Entre no Claude Code para ver o uso"))
            return
        }
        var pedido = URLRequest(url: URL(string: "https://api.anthropic.com/api/oauth/usage")!)
        pedido.setValue("Bearer " + token, forHTTPHeaderField: "Authorization")
        pedido.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")
        pedido.setValue("claude-widget", forHTTPHeaderField: "User-Agent")
        pedido.timeoutInterval = 15
        URLSession.shared.dataTask(with: pedido) { dados, resposta, _ in
            var leitura = Leitura()
            let codigo = (resposta as? HTTPURLResponse)?.statusCode ?? 0
            if codigo == 401 {
                leitura.erro = "Login expirado: abra o Claude Code"
            } else if let dados = dados, codigo == 200, let u = try? JSONSerialization.jsonObject(with: dados) as? [String: Any] {
                leitura.sessao = pct(u["five_hour"])
                leitura.semana = pct(u["seven_day"])
                leitura.renovaSessao = renova(u["five_hour"], "'às' HH:mm")
                leitura.renovaSemana = renova(u["seven_day"], "EEE HH:mm")
                if let lista = u["limits"] as? [[String: Any]] {
                    for l in lista where (l["kind"] as? String) == "weekly_scoped" {
                        guard let escopo = l["scope"] as? [String: Any], let modelo = escopo["model"] as? [String: Any],
                              let nome = modelo["display_name"] as? String, let p = l["percent"] as? Double else { continue }
                        leitura.modelos.append(Limite(nome: nome, pct: Int(p.rounded()), renova: renova(l, "EEE HH:mm")))
                    }
                }
            } else {
                leitura.erro = "Sem resposta (" + horaAgora() + ")"
            }
            DispatchQueue.main.async { pronto(leitura) }
        }.resume()
    }

    static func pct(_ limite: Any?) -> Int {
        guard let l = limite as? [String: Any], let v = l["utilization"] as? Double else { return 0 }
        return Int(v.rounded())
    }

    static func renova(_ limite: Any?, _ formato: String) -> String {
        guard let l = limite as? [String: Any], let texto = l["resets_at"] as? String else { return "" }
        let leitor = ISO8601DateFormatter()
        leitor.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        var quando = leitor.date(from: texto)
        if quando == nil {
            leitor.formatOptions = [.withInternetDateTime]
            quando = leitor.date(from: texto)
        }
        guard let data = quando else { return "" }
        let f = DateFormatter()
        f.locale = Locale(identifier: "pt_BR")
        f.dateFormat = formato
        return "renova " + f.string(from: data).replacingOccurrences(of: ".", with: "")
    }
}

func horaAgora() -> String {
    let f = DateFormatter()
    f.dateFormat = "HH:mm"
    return f.string(from: Date())
}

func corDoUso(_ pct: Int) -> String { pct >= 90 ? "#E5484D" : pct >= 75 ? "#E8B339" : laranja }

func cor(_ hex: String) -> NSColor {
    var s = hex.trimmingCharacters(in: CharacterSet(charactersIn: "#"))
    var a: CGFloat = 1
    if s.count == 8 {
        a = CGFloat(Int(s.prefix(2), radix: 16) ?? 255) / 255
        s = String(s.dropFirst(2))
    }
    let v = Int(s, radix: 16) ?? 0
    return NSColor(srgbRed: CGFloat((v >> 16) & 0xFF) / 255, green: CGFloat((v >> 8) & 0xFF) / 255, blue: CGFloat(v & 0xFF) / 255, alpha: a)
}

// MARK: - Desenho: o mesmo pixel art do Windows (coordenadas do corpo de 0,0 a 32,20)

// Lê os desenhos no formato "M8,-6 H22 V-4 h2 v4 Z" (só linhas retas, como no Windows)
func caminho(_ desenho: String) -> CGPath {
    var pedacos: [String] = []
    var atual = ""
    for ch in desenho {
        if ch.isLetter {
            if !atual.isEmpty { pedacos.append(atual); atual = "" }
            pedacos.append(String(ch))
        } else if ch == "," || ch == " " {
            if !atual.isEmpty { pedacos.append(atual); atual = "" }
        } else if ch == "-" {
            if !atual.isEmpty { pedacos.append(atual) }
            atual = "-"
        } else {
            atual.append(ch)
        }
    }
    if !atual.isEmpty { pedacos.append(atual) }

    let p = CGMutablePath()
    var x: CGFloat = 0, y: CGFloat = 0, inicio = CGPoint.zero
    var comando = "M"
    var i = 0
    func numero() -> CGFloat {
        let v = CGFloat(Double(pedacos[i]) ?? 0)
        i += 1
        return v
    }
    while i < pedacos.count {
        let t = pedacos[i]
        if let c = t.first, c.isLetter {
            comando = t
            i += 1
            if comando == "Z" || comando == "z" { p.closeSubpath(); x = inicio.x; y = inicio.y }
            continue
        }
        switch comando {
        case "M": x = numero(); y = numero(); p.move(to: CGPoint(x: x, y: y)); inicio = CGPoint(x: x, y: y); comando = "L"
        case "L": x = numero(); y = numero(); p.addLine(to: CGPoint(x: x, y: y))
        case "H": x = numero(); p.addLine(to: CGPoint(x: x, y: y))
        case "V": y = numero(); p.addLine(to: CGPoint(x: x, y: y))
        case "h": x += numero(); p.addLine(to: CGPoint(x: x, y: y))
        case "v": y += numero(); p.addLine(to: CGPoint(x: x, y: y))
        default: i += 1
        }
    }
    return p
}

struct Chapeu {
    let id: String
    let nome: String
    let partes: [(String, CGPath)]
    init(id: String, nome: String, partes: [(String, String)]) {
        self.id = id
        self.nome = nome
        self.partes = partes.map { ($0.0, caminho($0.1)) }
    }
}

let chapeus: [Chapeu] = [
    Chapeu(id: "nenhum", nome: "Nenhum", partes: []),
    Chapeu(id: "bone", nome: "Boné", partes: [("#2F6FEB", "M8,-6 H22 V-4 H24 V0 H6 V-4 H8 Z"), ("#1F4FB8", "M22,-2 H32 V0 H22 Z"), ("#FFFFFF", "M12,-4 h2 v2 h-2 Z")]),
    Chapeu(id: "cartola", nome: "Cartola", partes: [("#4A4A4A", "M8,-10 H24 V-2 H8 Z"), ("#E5484D", "M8,-4 H24 V-2 H8 Z"), ("#5C5C5C", "M4,-2 H28 V0 H4 Z")]),
    Chapeu(id: "festa", nome: "Festa", partes: [("#EC4899", "M10,-2 H22 V0 H10 Z M13,-6 H19 V-4 H13 Z"), ("#FACC15", "M12,-4 H20 V-2 H12 Z M15,-8 H17 V-6 H15 Z"), ("#FFFFFF", "M14,-10 H18 V-8 H14 Z")]),
    Chapeu(id: "coroa", nome: "Coroa", partes: [("#F5C542", "M6,-4 H26 V0 H6 Z M6,-8 H9 V-4 H6 Z M14,-9 H18 V-4 H14 Z M23,-8 H26 V-4 H23 Z"), ("#E5484D", "M15,-3 H17 V-1 H15 Z"), ("#4F8EF7", "M9,-3 H11 V-1 H9 Z M21,-3 H23 V-1 H21 Z")]),
    Chapeu(id: "bruxa", nome: "Bruxa", partes: [("#6D28D9", "M2,-2 H30 V0 H2 Z M10,-6 H22 V-4 H10 Z M12,-8 H20 V-6 H12 Z M16,-10 H22 V-8 H16 Z"), ("#22C55E", "M8,-4 H24 V-2 H8 Z")]),
    Chapeu(id: "natal", nome: "Natal", partes: [("#DC2626", "M6,-6 H24 V-2 H6 Z M12,-8 H26 V-6 H12 Z"), ("#F5F5F5", "M4,-2 H28 V0 H4 Z M26,-10 H30 V-6 H26 Z")]),
    Chapeu(id: "abobora", nome: "Abóbora", partes: [("#F97316", "M8,-8 H24 V0 H8 Z M6,-6 H8 V-2 H6 Z M24,-6 H26 V-2 H24 Z"), ("#C2410C", "M12,-8 h1 v8 h-1 Z M19,-8 h1 v8 h-1 Z"), ("#15803D", "M15,-11 h2 v3 h-2 Z"), ("#1F1F1F", "M10,-6 h2 v2 h-2 Z M20,-6 h2 v2 h-2 Z M13,-3 h6 v1 h-6 Z")]),
    Chapeu(id: "palha", nome: "Palha (festa junina)", partes: [("#EAB308", "M0,-2 H32 V0 H0 Z"), ("#CA8A04", "M8,-8 H24 V-2 H8 Z"), ("#DC2626", "M8,-4 H24 V-3 H8 Z"), ("#2563EB", "M18,-7 h3 v2 h-3 Z")]),
]

let cores: [(String, String)] = [("Laranja Claude", laranja), ("Azul", "#4F8EF7"), ("Verde", "#3FB950"), ("Roxo", "#A371F7"), ("Rosa", "#F778BA"), ("Dourado", "#E3B341"), ("Cinza", "#9DA5B4")]

func chapeuDaEstacao(_ d: Date) -> String? {
    let c = Calendar.current.dateComponents([.month, .day], from: d)
    let mes = c.month ?? 0, dia = c.day ?? 0
    if mes == 12 { return "natal" }
    if (mes == 10 && dia >= 20) || (mes == 11 && dia <= 2) { return "abobora" }
    if mes == 6 || (mes == 7 && dia <= 15) { return "palha" }
    if mes == 1 && dia <= 6 { return "festa" }
    return nil
}

let corpo = caminho("M4,0 H28 V8 H32 V12 H28 V16 H4 V12 H0 V8 H4 Z")
func pernas(_ alturas: [CGFloat]) -> CGPath {
    let p = CGMutablePath()
    for (i, x) in [6, 10, 20, 24].enumerated() { p.addRect(CGRect(x: CGFloat(x), y: 16, width: 2, height: alturas[i])) }
    return p
}
let pernasPorPasso: [String: CGPath] = ["N": pernas([4, 4, 4, 4]), "A": pernas([2, 4, 2, 4]), "B": pernas([4, 2, 4, 2]), "T": pernas([2, 2, 2, 2])]
let oculosDesenho = caminho("M6,3 h6 v5 h-6 Z M20,3 h6 v5 h-6 Z M12,4 h8 v2 h-8 Z")
let suorDesenho = caminho("M30,-1 h2 v1 h-2 Z M29,0 h4 v3 h-4 Z M30,3 h2 v1 h-2 Z")
// notebook (tampa com o logo, teclado) e a xícara de café
let notebook: [(String, CGPath)] = [("#9CA3AF", caminho("M6,9 H26 V19 H6 Z")), ("#6B7280", caminho("M6,18 H26 V19 H6 Z M3,19 H29 V21 H3 Z")), (laranja, caminho("M15,11 h2 v6 h-2 Z M13,13 h6 v2 h-6 Z"))]
let teclasA = caminho("M7,19 h2 v1 h-2 Z M15,19 h2 v1 h-2 Z M23,19 h2 v1 h-2 Z")
let teclasB = caminho("M11,19 h2 v1 h-2 Z M19,19 h2 v1 h-2 Z")
let xicara: [(String, CGPath)] = [("#F5F5F5", caminho("M0,2 h5 v5 h-5 Z M5,3 h1 v3 h-1 Z")), ("#6B3F1F", caminho("M0,2 h5 v1 h-5 Z"))]
let vapor = caminho("M1,-1 h1 v2 h-1 Z M3,-2 h1 v2 h-1 Z")

struct Quadro {
    var x = 0, y = 0, olhar = 0
    var fechado = false
    var pe = "N"
}

final class Roteiro {
    var quadros: [Quadro] = []
    @discardableResult
    func q(x: Int = 0, y: Int = 0, olhar: Int = 0, fechado: Bool = false, pe: String = "N", n: Int = 1) -> Roteiro {
        for _ in 0..<n { quadros.append(Quadro(x: x, y: y, olhar: olhar, fechado: fechado, pe: pe)) }
        return self
    }
}

let sequencias: [String: [Quadro]] = {
    var s: [String: [Quadro]] = [:]
    s["piscar"] = Roteiro().q(fechado: true).q(n: 2).quadros
    s["respirar"] = Roteiro().q(n: 3).q(y: -1, n: 3).q(n: 3).q(y: -1, n: 3).q(fechado: true).q(n: 2).quadros
    s["olhar"] = Roteiro().q(olhar: -2, n: 6).q(olhar: -2, fechado: true).q(olhar: -2, n: 3).q(n: 2).q(olhar: 2, n: 8).q().quadros
    let andar = Roteiro()
    for lado in [-1, 1] {
        andar.q(x: lado, olhar: lado * 2, pe: "A", n: 2).q(x: 2 * lado, y: -1, olhar: lado * 2, pe: "B", n: 2)
            .q(x: 3 * lado, olhar: lado * 2, pe: "A", n: 2).q(x: 2 * lado, y: -1, olhar: -lado * 2, pe: "B", n: 2)
            .q(x: lado, olhar: -lado * 2, pe: "A", n: 2).q(y: -1, pe: "B", n: 2)
    }
    s["andar"] = andar.q().quadros
    let pular = Roteiro()
    for _ in 0..<2 { pular.q(y: -1).q(y: -3, fechado: true, pe: "T", n: 2).q(y: -1).q() }
    s["pular"] = pular.q().quadros
    s["ofegar"] = Roteiro().q(y: -1).q().q(y: -1).q().q(y: -1).q().q(fechado: true).q().q(y: -1).q().quadros
    s["digitar"] = Roteiro().q(n: 2).q(y: -1).q(n: 3).q(y: -1).q(n: 2).q(fechado: true).q(n: 2).quadros
    return s
}()

// MARK: - Fala: saudação e lembretes

enum Falas {
    static func periodo(_ d: Date) -> String {
        let h = Calendar.current.component(.hour, from: d)
        return h >= 5 && h < 12 ? "manha" : h >= 12 && h < 18 ? "tarde" : "noite"
    }

    static func primeiroNome() -> String? {
        let nome = NSFullUserName().split(separator: " ").first.map(String.init) ?? NSUserName()
        return nome.count >= 2 && !nome.contains(where: { $0.isNumber }) ? nome : nil
    }

    static func saudacao(_ uso: Uso) -> String {
        let p = periodo(Date())
        var texto = (p == "manha" ? "Bom dia" : p == "tarde" ? "Boa tarde" : "Boa noite") + (primeiroNome().map { ", " + $0 } ?? "") + "! "
            + (p == "manha" ? "☀️" : p == "tarde" ? "🌤️" : "🌙")
        if Calendar.current.component(.weekday, from: Date()) == 6 { texto += " Sextou!" }
        if uso.temLeitura && uso.erro == nil {
            texto += "\nSessão em \(uso.sessao)% e semana em \(uso.semana)%."
            if max(uso.sessao, uso.semana) >= 90 { texto += " Vai com calma hoje 😅" }
        }
        return texto
    }

    static let lembretes = [
        "Não esquece de tomar água! 💧", "Hora de um gole de água 💧", "Levanta e estica as costas um pouquinho 🙆",
        "Postura! Costas retas e ombros relaxados 😄", "Olha para longe por 20 segundos, seus olhos agradecem 👀",
        "Que tal uma caminhada rápida? 🚶",
    ]
}

// MARK: - Painel (o "balão" do Windows): abre clicando no mascote

struct BarraDeUso: View {
    let titulo: String
    let pct: Int
    let renova: String
    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(titulo).font(.system(size: 12))
                Spacer()
                Text("\(pct)%").font(.system(size: 12, weight: .semibold))
            }
            GeometryReader { g in
                ZStack(alignment: .leading) {
                    RoundedRectangle(cornerRadius: 2).fill(Color.secondary.opacity(0.25))
                    RoundedRectangle(cornerRadius: 2).fill(Color(cor(corDoUso(pct)))).frame(width: g.size.width * CGFloat(min(100, max(0, pct))) / 100)
                }
            }
            .frame(height: 4)
            if !renova.isEmpty { Text(renova).font(.system(size: 11)).foregroundColor(.secondary) }
        }
    }
}

struct Painel: View {
    @ObservedObject var uso: Uso
    let atualizar: () -> Void
    let baixar: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("Uso do Claude").font(.system(size: 13, weight: .semibold))
                Spacer()
                if uso.codando { Text("💻 Claude trabalhando").font(.system(size: 11)).foregroundColor(.secondary) }
            }
            BarraDeUso(titulo: "Sessão (5h)", pct: uso.sessao, renova: uso.renovaSessao)
            BarraDeUso(titulo: "Semanal", pct: uso.semana, renova: uso.renovaSemana)
            ForEach(uso.modelos) { m in BarraDeUso(titulo: "Semanal · \(m.nome)", pct: m.pct, renova: m.renova) }
            Text("Últimos 7 dias (% do limite semanal)").font(.system(size: 12))
            Grafico(dias: uso.dias)
            Text(uso.erro ?? uso.hora).font(.system(size: 11)).foregroundColor(.secondary)
            if let v = uso.novaVersao {
                Button("Baixar a versão \(v)", action: baixar).foregroundColor(Color(cor(laranja)))
            }
            HStack {
                Button("Atualizar", action: atualizar)
                Button("Abrir claude.ai") { NSWorkspace.shared.open(URL(string: "https://claude.ai/settings/usage")!) }
            }
            Text("Botão direito no mascote: cor, chapéu e mais").font(.system(size: 10.5)).foregroundColor(.secondary)
        }
        .padding(14)
        .frame(width: 260)
    }
}

struct Grafico: View {
    let dias: [Double]
    var body: some View {
        let maior = max(10, dias.max() ?? 0)
        let nomes: [String] = (0..<7).map { i in
            if i == 6 { return "hoje" }
            let d = Calendar.current.date(byAdding: .day, value: i - 6, to: Date()) ?? Date()
            let f = DateFormatter()
            f.locale = Locale(identifier: "pt_BR")
            f.dateFormat = "EEE"
            return f.string(from: d).replacingOccurrences(of: ".", with: "")
        }
        return HStack(alignment: .bottom, spacing: 6) {
            ForEach(0..<7, id: \.self) { i in
                VStack(spacing: 2) {
                    Text(dias[i] > 0 ? "\(Int(dias[i].rounded()))%" : " ").font(.system(size: 9)).foregroundColor(.secondary)
                    RoundedRectangle(cornerRadius: 2)
                        .fill(dias[i] > 0 ? Color(cor(i == 6 ? laranja : "#99D77757")) : Color.secondary.opacity(0.25))
                        .frame(height: dias[i] > 0 ? max(3, 30 * CGFloat(dias[i] / maior)) : 2)
                    Text(nomes[i]).font(.system(size: 10, weight: i == 6 ? .semibold : .regular)).foregroundColor(i == 6 ? .primary : .secondary)
                }
                .frame(maxWidth: .infinity)
            }
        }
        .frame(height: 58)
    }
}

// MARK: - O app

final class Acao: NSObject {
    let fazer: () -> Void
    init(_ fazer: @escaping () -> Void) { self.fazer = fazer }
    @objc func executar() { fazer() }
}

final class App: NSObject, NSApplicationDelegate {
    let uso = Uso()
    var item: NSStatusItem!
    let painel = NSPopover()
    var hospedeiro: NSHostingController<Painel>!
    var fala: NSPanel?
    var acoes: [Acao] = []
    var fila: [Quadro] = []
    var quadro = Quadro()
    var tique = 0
    var buscando = false
    var urlNovaVersao = URL(string: "https://github.com/\(repositorio)/releases/latest")!
    var ultimaAtividade = Date.distantPast
    var ultimoLembrete = Date()
    var proximoCafe = Date.distantFuture
    var cafe: String?  // nil, "saindo", "fora" ou "voltando"
    var cafeX: CGFloat = 0
    var cafeTiques = 0
    var xicaraNaMesa = false
    let demo = CommandLine.arguments.contains("--demo")
    var codandoDemoAte = Date.distantPast

    var pct: Int { escolhas.mostrar == "sessao" ? uso.sessao : escolhas.mostrar == "semanal" ? uso.semana : max(uso.sessao, uso.semana) }
    var cansado: Bool { uso.erro == nil && uso.temLeitura && pct >= 90 }

    func applicationDidFinishLaunching(_ notification: Notification) {
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.target = self
        item.button?.action = #selector(clicou(_:))
        item.button?.sendAction(on: [.leftMouseUp, .rightMouseUp])
        painel.behavior = .transient
        hospedeiro = NSHostingController(rootView: Painel(uso: uso, atualizar: { [weak self] in self?.atualizarUso() }, baixar: { [weak self] in self?.baixarVersao() }))
        painel.contentViewController = hospedeiro
        desenhar()

        Timer.scheduledTimer(withTimeInterval: 0.12, repeats: true) { [weak self] _ in self?.animar() }
        Timer.scheduledTimer(withTimeInterval: 300, repeats: true) { [weak self] _ in self?.atualizarUso() }
        Timer.scheduledTimer(withTimeInterval: 4, repeats: true) { [weak self] _ in self?.olharClaudeCode() }
        Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in self?.conferirLembretes() }
        Timer.scheduledTimer(withTimeInterval: 12 * 3600, repeats: true) { [weak self] _ in self?.verificarVersao() }
        DispatchQueue.main.asyncAfter(deadline: .now() + 60) { [weak self] in self?.verificarVersao() }
        if demo { comecarDemo() } else { atualizarUso(saudar: true) }
    }

    // MARK: Uso

    func atualizarUso() { atualizarUso(saudar: false) }

    func atualizarUso(saudar: Bool) {
        if buscando || demo { return }
        buscando = true
        ApiUso.buscar { [weak self] leitura in
            guard let self = self else { return }
            self.buscando = false
            if let erro = leitura.erro {
                self.uso.erro = erro
                self.uso.hora = erro
            } else {
                let renovou = self.uso.temLeitura && (leitura.sessao + 10 <= self.uso.sessao || leitura.semana + 10 <= self.uso.semana)
                self.uso.erro = nil
                self.uso.sessao = leitura.sessao
                self.uso.semana = leitura.semana
                self.uso.renovaSessao = leitura.renovaSessao
                self.uso.renovaSemana = leitura.renovaSemana
                self.uso.modelos = leitura.modelos
                self.uso.hora = "Atualizado às " + horaAgora()
                self.uso.temLeitura = true
                self.guardarHistorico()
                if renovou { self.animarSequencia("pular"); self.falar("Limite renovado! Pode voltar com tudo 🎉") }
            }
            if saudar && escolhas.saudacao { self.falar(Falas.saudacao(self.uso)) }
            self.desenhar()
        }
    }

    func guardarHistorico() {
        var h = escolhas.historico
        let agora = Date().timeIntervalSince1970
        if let ultima = h.last, Int(ultima[1]) == uso.semana, agora - ultima[0] < 1800 { calcularDias(h); return }
        h.append([agora, Double(uso.semana)])
        h.removeAll { $0[0] < agora - 9 * 86400 }
        escolhas.historico = h
        calcularDias(h)
    }

    // Quanto do limite semanal subiu em cada um dos últimos 7 dias (as quedas são o limite renovando)
    func calcularDias(_ h: [[Double]]) {
        var dias = Array(repeating: 0.0, count: 7)
        let hoje = Calendar.current.startOfDay(for: Date())
        if h.count >= 2 {
            for i in 1..<h.count {
                let subida = h[i][1] - h[i - 1][1]
                if subida <= 0 { continue }
                let dia = Calendar.current.startOfDay(for: Date(timeIntervalSince1970: h[i][0]))
                let atras = Calendar.current.dateComponents([.day], from: dia, to: hoje).day ?? 99
                if atras >= 0 && atras < 7 { dias[6 - atras] += subida }
            }
        }
        uso.dias = dias
    }

    // MARK: Claude Code trabalhando

    // O Claude Code grava cada conversa em ~/.claude/projects/<projeto>/<sessão>.jsonl. Só olha QUANDO esses
    // arquivos mudam (não lê o conteúdo): mudou nos últimos 20 segundos, ele está trabalhando.
    func olharClaudeCode() {
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let fm = FileManager.default
            let casa = fm.homeDirectoryForCurrentUser
            var maisNova = Date.distantPast
            for base in [casa.appendingPathComponent(".claude/projects"), casa.appendingPathComponent(".config/claude/projects")] {
                guard let pastas = try? fm.contentsOfDirectory(at: base, includingPropertiesForKeys: nil) else { continue }
                for pasta in pastas {
                    guard let arquivos = try? fm.contentsOfDirectory(at: pasta, includingPropertiesForKeys: [.contentModificationDateKey]) else { continue }
                    for a in arquivos where a.pathExtension == "jsonl" {
                        if let d = (try? a.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate, d > maisNova { maisNova = d }
                    }
                }
            }
            DispatchQueue.main.async {
                guard let self = self else { return }
                self.ultimaAtividade = maisNova
                self.mudarCodando(escolhas.codando && (Date().timeIntervalSince(maisNova) < 20 || Date() < self.codandoDemoAte))
            }
        }
    }

    func mudarCodando(_ agora: Bool) {
        if agora == uso.codando { return }
        uso.codando = agora
        fila.removeAll()
        if agora { proximoCafe = Date().addingTimeInterval(Double(15 + Int.random(in: 0...10)) * 60) }
        else { cafe = nil; cafeX = 0; xicaraNaMesa = false }
    }

    // --demo: números de mentira e tudo o que o mascote sabe fazer, em sequência (bom para gravar um vídeo)
    func comecarDemo() {
        uso.sessao = 42
        uso.semana = 67
        uso.renovaSessao = "renova às 18:00"
        uso.renovaSemana = "renova sex 09:00"
        uso.modelos = [Limite(nome: "Opus", pct: 35, renova: "renova sex 09:00")]
        uso.dias = [8, 12, 0, 15, 9, 14, 9]
        uso.hora = "Demonstração"
        uso.temLeitura = true
        desenhar()
        falar(Falas.saudacao(uso))
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in
            self?.codandoDemoAte = Date().addingTimeInterval(40)
            self?.mudarCodando(true)
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in self?.iniciarCafe() }
        DispatchQueue.main.asyncAfter(deadline: .now() + 15) { [weak self] in self?.abrirPainel() }
    }

    // MARK: Animação

    func animarSequencia(_ nome: String) { fila = sequencias[nome] ?? [] }

    func sortear() -> String {
        if cansado { return ["ofegar", "respirar", "olhar", "piscar"].randomElement()! }
        let opcoes = ["andar", "andar", "andar", "respirar", "respirar", "olhar", "pular", "piscar"]
        return opcoes.randomElement()!
    }

    func animar() {
        tique += 1
        if !escolhas.movimento { if tique % 25 == 0 { desenhar() }; return }
        if cafe != nil { passoDoCafe(); desenhar(); return }
        if fila.isEmpty { animarSequencia(uso.codando ? "digitar" : sortear()) }
        if fila.isEmpty { return }
        quadro = fila.removeFirst()
        if uso.codando && Date() >= proximoCafe { iniciarCafe() }
        desenhar()
    }

    // Pausa para o café: avisa, levanta, sai andando, volta com a xícara e deixa ao lado do notebook
    func iniciarCafe() {
        cafe = "saindo"
        cafeX = 0
        cafeTiques = 0
        xicaraNaMesa = false
        proximoCafe = Date().addingTimeInterval(Double(15 + Int.random(in: 0...10)) * 60)
        falar("Vou buscar um café ☕ Já volto!")
    }

    func passoDoCafe() {
        cafeTiques += 1
        let par = cafeTiques % 2 == 0
        quadro = Quadro(x: 0, y: par ? -1 : 0, olhar: 0, fechado: false, pe: par ? "A" : "B")
        switch cafe ?? "" {
        case "saindo":
            cafeX -= 3
            quadro.olhar = -2
            if cafeX <= -40 { cafe = "fora"; cafeTiques = 0 }
        case "fora":
            quadro = Quadro()
            if cafeTiques >= 34 { cafe = "voltando"; cafeTiques = 0 }
        default:
            cafeX += 3
            quadro.olhar = 2
            if cafeX >= 0 {
                cafeX = 0
                cafe = nil
                quadro = Quadro()
                xicaraNaMesa = true
                falar("Voltei! Café na mão, bora codar ☕")
            }
        }
    }

    // MARK: Desenho na barra de menus

    func desenhar() {
        let rotulo = uso.erro != nil ? "?" : "\(pct)%"
        let fonte = NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .semibold)
        let larguraTexto = ceil((rotulo as NSString).size(withAttributes: [.font: fonte]).width)
        let tamanho = NSSize(width: 44 + larguraTexto, height: 22)
        let q = quadro, tq = tique
        let imagem = NSImage(size: tamanho, flipped: true) { [weak self] _ in
            guard let self = self, let ctx = NSGraphicsContext.current?.cgContext else { return false }
            self.desenharMascote(ctx, quadro: q, tique: tq)
            self.desenharAnel(ctx, centro: CGPoint(x: 29, y: 11))
            (rotulo as NSString).draw(at: NSPoint(x: 39, y: 3), withAttributes: [.font: fonte, .foregroundColor: NSColor.labelColor])
            return true
        }
        imagem.isTemplate = false
        item.button?.image = imagem
        item.button?.toolTip = uso.erro ?? "Claude · 5h \(uso.sessao)% · semana \(uso.semana)%" + (uso.codando ? " · trabalhando" : "")
    }

    func preencher(_ ctx: CGContext, _ caminho: CGPath, _ hex: String) {
        ctx.addPath(caminho)
        ctx.setFillColor(cor(hex).cgColor)
        ctx.fillPath()
    }

    // O mascote em meia escala (cada pixel do desenho vira meio ponto, um pixel de verdade na tela Retina),
    // com chapéu, óculos, suor, notebook e café
    func desenharMascote(_ ctx: CGContext, quadro q: Quadro, tique: Int) {
        let escala: CGFloat = 0.5
        let corpoCor = escolhas.cor
        let chapeuId = (escolhas.chapeuEstacao ? chapeuDaEstacao(Date()) : nil) ?? escolhas.chapeu
        // a mesa (notebook e xícara) fica parada; o corpo anda, pula e vai buscar café
        ctx.saveGState()
        ctx.translateBy(x: 2, y: 9)
        ctx.scaleBy(x: escala, y: escala)
        ctx.saveGState()
        ctx.translateBy(x: CGFloat(q.x) + cafeX, y: CGFloat(q.y))
        preencher(ctx, corpo, corpoCor)
        preencher(ctx, pernasPorPasso[q.pe] ?? pernasPorPasso["N"]!, corpoCor)
        let baixos = cansado || (uso.codando && cafe == nil)
        let altura: CGFloat = q.fechado ? 1 : baixos ? 2 : 4
        let topo: CGFloat = q.fechado || baixos ? 6 : 4
        let olhar = CGFloat(q.olhar)
        preencher(ctx, CGPath(rect: CGRect(x: 8 + olhar, y: topo, width: 2, height: altura), transform: nil), "#141413")
        preencher(ctx, CGPath(rect: CGRect(x: 22 + olhar, y: topo, width: 2, height: altura), transform: nil), "#141413")
        if escolhas.oculos { preencher(ctx, oculosDesenho, "#0F0F0F") }
        if cansado { preencher(ctx, suorDesenho, "#7DD3FC") }
        if let chapeu = chapeus.first(where: { $0.id == chapeuId }) { for parte in chapeu.partes { preencher(ctx, parte.1, parte.0) } }
        if cafe == "voltando" {
            ctx.saveGState()
            ctx.translateBy(x: 31, y: 6)
            for parte in xicara { preencher(ctx, parte.1, parte.0) }
            if tique % 4 < 2 { preencher(ctx, vapor, "#D1D5DB") }
            ctx.restoreGState()
        }
        ctx.restoreGState()
        if uso.codando {
            for parte in notebook { preencher(ctx, parte.1, parte.0) }
            if cafe == nil { preencher(ctx, tique % 2 == 0 ? teclasA : teclasB, "#E5E7EB") }
            if xicaraNaMesa {
                ctx.saveGState()
                ctx.translateBy(x: 31, y: 13)
                for parte in xicara { preencher(ctx, parte.1, parte.0) }
                if tique % 8 < 4 { preencher(ctx, vapor, "#D1D5DB") }
                ctx.restoreGState()
            }
        }
        ctx.restoreGState()
    }

    func desenharAnel(_ ctx: CGContext, centro: CGPoint) {
        let raio: CGFloat = 6.5
        ctx.setLineWidth(2)
        ctx.setStrokeColor(NSColor.labelColor.withAlphaComponent(0.25).cgColor)
        ctx.addArc(center: centro, radius: raio, startAngle: 0, endAngle: .pi * 2, clockwise: false)
        ctx.strokePath()
        guard uso.erro == nil, pct > 0 else { return }
        ctx.setLineCap(.round)
        ctx.setStrokeColor(cor(corDoUso(pct)).cgColor)
        let inicio = -CGFloat.pi / 2
        let fim = inicio + .pi * 2 * CGFloat(min(100, pct)) / 100
        ctx.addArc(center: centro, radius: raio, startAngle: inicio, endAngle: fim, clockwise: false)
        ctx.strokePath()
    }

    // MARK: Cliques e menu

    @objc func clicou(_ sender: Any?) {
        guard let botao = item.button else { return }
        if NSApp.currentEvent?.type == .rightMouseUp {
            item.menu = montarMenu()
            botao.performClick(nil)
            item.menu = nil
            return
        }
        if painel.isShown { painel.performClose(nil); return }
        abrirPainel()
    }

    func abrirPainel() {
        guard let botao = item.button else { return }
        fecharFala()
        painel.contentSize = hospedeiro.view.fittingSize
        painel.show(relativeTo: botao.bounds, of: botao, preferredEdge: .minY)
        NSApp.activate(ignoringOtherApps: true)
    }

    func opcao(_ menu: NSMenu, _ titulo: String, marcada: Bool = false, _ fazer: @escaping () -> Void) {
        let a = Acao(fazer)
        acoes.append(a)
        let i = NSMenuItem(title: titulo, action: #selector(Acao.executar), keyEquivalent: "")
        i.target = a
        i.state = marcada ? .on : .off
        menu.addItem(i)
    }

    func submenu(_ menu: NSMenu, _ titulo: String) -> NSMenu {
        let sub = NSMenu()
        let i = NSMenuItem(title: titulo, action: nil, keyEquivalent: "")
        i.submenu = sub
        menu.addItem(i)
        return sub
    }

    func montarMenu() -> NSMenu {
        acoes.removeAll()
        let versao = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? ""
        let m = NSMenu()
        let titulo = NSMenuItem(title: "Claude Widget " + versao, action: nil, keyEquivalent: "")
        titulo.isEnabled = false
        m.addItem(titulo)
        if let v = uso.novaVersao { opcao(m, "Baixar a versão \(v)") { [weak self] in self?.baixarVersao() } }
        m.addItem(.separator())
        opcao(m, "Atualizar agora") { [weak self] in self?.atualizarUso() }
        opcao(m, "Abrir uso no claude.ai") { NSWorkspace.shared.open(URL(string: "https://claude.ai/settings/usage")!) }
        m.addItem(.separator())
        let mostrar = submenu(m, "Mostrar %")
        for (nome, valor) in [("O maior dos dois", "maior"), ("Sessão (5h)", "sessao"), ("Semanal", "semanal")] {
            opcao(mostrar, nome, marcada: escolhas.mostrar == valor) { [weak self] in escolhas.mostrar = valor; self?.desenhar() }
        }
        let corMenu = submenu(m, "Cor")
        for (nome, hex) in cores { opcao(corMenu, nome, marcada: escolhas.cor == hex) { [weak self] in escolhas.cor = hex; self?.animarSequencia("pular") } }
        let chapeuMenu = submenu(m, "Chapéu")
        for c in chapeus { opcao(chapeuMenu, c.nome, marcada: escolhas.chapeu == c.id) { [weak self] in escolhas.chapeu = c.id; self?.animarSequencia("pular") } }
        chapeuMenu.addItem(.separator())
        opcao(chapeuMenu, "Chapéu da estação (Natal, Halloween, festa junina, Ano-Novo)", marcada: escolhas.chapeuEstacao) { escolhas.chapeuEstacao.toggle() }
        opcao(m, "Óculos escuros", marcada: escolhas.oculos) { escolhas.oculos.toggle() }
        m.addItem(.separator())
        opcao(m, "Movimento", marcada: escolhas.movimento) { escolhas.movimento.toggle() }
        opcao(m, "Claude codando (quando o Claude Code estiver trabalhando)", marcada: escolhas.codando) { escolhas.codando.toggle() }
        opcao(m, "Saudação ao abrir (bom dia, boa tarde, boa noite)", marcada: escolhas.saudacao) { escolhas.saudacao.toggle() }
        opcao(m, "Lembretes (água, alongar, descansar a vista)", marcada: escolhas.lembretes) { escolhas.lembretes.toggle() }
        opcao(m, "Mostrar um lembrete agora") { [weak self] in self?.falar(Falas.lembretes.randomElement()!) }
        m.addItem(.separator())
        if #available(macOS 13.0, *) {
            let ligado = SMAppService.mainApp.status == .enabled
            opcao(m, "Abrir ao iniciar o Mac", marcada: ligado) {
                if ligado { try? SMAppService.mainApp.unregister() } else { try? SMAppService.mainApp.register() }
            }
        }
        opcao(m, "Avisar quando sair versão nova", marcada: escolhas.avisarVersao) { [weak self] in
            escolhas.avisarVersao.toggle()
            if escolhas.avisarVersao { self?.verificarVersao() }
        }
        opcao(m, "Ver novidades") { [weak self] in if let u = self?.urlNovaVersao { NSWorkspace.shared.open(u) } }
        m.addItem(.separator())
        opcao(m, "Sair") { NSApp.terminate(nil) }
        return m
    }

    // MARK: Fala (saudação, lembretes, café)

    func falar(_ texto: String, tentativa: Int = 0) {
        guard let botao = item.button, let janela = botao.window else { return }
        if painel.isShown { return }
        // logo que o app abre, o ícone ainda não está no lugar dele na barra de menus: espera um pouco
        let lugar = janela.convertToScreen(botao.convert(botao.bounds, to: nil))
        if janela.screen.map({ lugar.maxY < $0.frame.maxY - 40 }) ?? true {
            if tentativa < 20 {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in self?.falar(texto, tentativa: tentativa + 1) }
            }
            return
        }
        fecharFala()
        let rotulo = NSTextField(wrappingLabelWithString: texto)
        rotulo.font = .systemFont(ofSize: 12.5)
        rotulo.preferredMaxLayoutWidth = 240
        rotulo.isSelectable = false
        let tamanhoTexto = rotulo.fittingSize
        let tamanho = NSSize(width: ceil(tamanhoTexto.width) + 24, height: ceil(tamanhoTexto.height) + 18)
        rotulo.frame = NSRect(x: 12, y: 9, width: ceil(tamanhoTexto.width), height: ceil(tamanhoTexto.height))
        let vista = NSVisualEffectView(frame: NSRect(origin: .zero, size: tamanho))
        vista.material = .popover
        vista.state = .active
        vista.blendingMode = .behindWindow
        vista.maskImage = NSImage(size: tamanho, flipped: false) { r in
            NSBezierPath(roundedRect: r, xRadius: 10, yRadius: 10).fill()
            return true
        }
        vista.addSubview(rotulo)
        vista.addGestureRecognizer(NSClickGestureRecognizer(target: self, action: #selector(fecharFala)))
        let quadroBotao = janela.convertToScreen(botao.convert(botao.bounds, to: nil))
        var x = quadroBotao.minX
        if let tela = janela.screen { x = min(x, tela.visibleFrame.maxX - tamanho.width - 4) }
        let p = NSPanel(contentRect: NSRect(x: x, y: quadroBotao.minY - tamanho.height - 2, width: tamanho.width, height: tamanho.height),
                        styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = true
        p.level = .statusBar
        p.collectionBehavior = [.canJoinAllSpaces, .stationary]
        p.contentView = vista
        p.orderFrontRegardless()
        fala = p
        animarSequencia("pular")
        let segundos = max(8, Double(texto.count) / 7)
        DispatchQueue.main.asyncAfter(deadline: .now() + segundos) { [weak self, weak p] in
            if let p = p, self?.fala === p { self?.fecharFala() }
        }
    }

    @objc func fecharFala() {
        fala?.orderOut(nil)
        fala = nil
    }

    // Há quanto tempo ninguém mexe no Mac (para não falar sozinho com você longe)
    func ocioso() -> Double {
        guard let qualquer = CGEventType(rawValue: ~0) else { return 0 }
        return CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: qualquer)
    }

    func conferirLembretes() {
        guard escolhas.lembretes, ocioso() < 300, Date().timeIntervalSince(ultimoLembrete) >= 3600 else { return }
        ultimoLembrete = Date()
        falar(Falas.lembretes.randomElement()!)
    }

    // MARK: Versão nova

    func verificarVersao() {
        guard escolhas.avisarVersao else { return }
        var pedido = URLRequest(url: URL(string: "https://api.github.com/repos/\(repositorio)/releases/latest")!)
        pedido.setValue("claude-widget", forHTTPHeaderField: "User-Agent")
        pedido.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        URLSession.shared.dataTask(with: pedido) { [weak self] dados, _, _ in
            guard let dados = dados, let r = try? JSONSerialization.jsonObject(with: dados) as? [String: Any],
                  let tag = r["tag_name"] as? String else { return }
            let nova = tag.trimmingCharacters(in: CharacterSet(charactersIn: "vV"))
            let atual = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0"
            guard nova.compare(atual, options: .numeric) == .orderedDescending else { return }
            DispatchQueue.main.async {
                guard let self = self else { return }
                if let pagina = r["html_url"] as? String, let u = URL(string: pagina) { self.urlNovaVersao = u }
                self.uso.novaVersao = nova
                if escolhas.versaoAvisada != nova {
                    escolhas.versaoAvisada = nova
                    self.falar("Saiu a versão \(nova) do Claude Widget! Clique no mascote para baixar.")
                }
            }
        }.resume()
    }

    func baixarVersao() { NSWorkspace.shared.open(urlNovaVersao) }
}

// MARK: - Início

let aplicativo = NSApplication.shared
let delegado = App()
aplicativo.delegate = delegado
aplicativo.setActivationPolicy(.accessory)
aplicativo.run()
