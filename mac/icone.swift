// Desenha o ícone do app (o mascote num quadrado arredondado) nos tamanhos que o iconutil pede.
// Uso: icone <pasta>.iconset
import AppKit

let pasta = CommandLine.arguments[1]
try FileManager.default.createDirectory(atPath: pasta, withIntermediateDirectories: true)

func desenhar(_ px: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8, samplesPerPixel: 4,
                               hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let ctx = NSGraphicsContext.current!.cgContext
    let s = CGFloat(px) / 1024
    ctx.scaleBy(x: s, y: s)
    ctx.addPath(CGPath(roundedRect: CGRect(x: 100, y: 100, width: 824, height: 824), cornerWidth: 185, cornerHeight: 185, transform: nil))
    ctx.setFillColor(CGColor(srgbRed: 0.12, green: 0.12, blue: 0.118, alpha: 1))
    ctx.fillPath()
    // o mascote tem 32 x 20 pixels; no ícone cada pixel vira 20 (e o y do desenho cresce para baixo)
    ctx.translateBy(x: 192, y: 712)
    ctx.scaleBy(x: 20, y: -20)
    ctx.setFillColor(CGColor(srgbRed: 0xD7 / 255.0, green: 0x77 / 255.0, blue: 0x57 / 255.0, alpha: 1))
    ctx.fill([CGRect(x: 4, y: 0, width: 24, height: 16), CGRect(x: 0, y: 8, width: 32, height: 4),
              CGRect(x: 6, y: 16, width: 2, height: 4), CGRect(x: 10, y: 16, width: 2, height: 4),
              CGRect(x: 20, y: 16, width: 2, height: 4), CGRect(x: 24, y: 16, width: 2, height: 4)])
    ctx.setFillColor(CGColor(srgbRed: 0x14 / 255.0, green: 0x14 / 255.0, blue: 0x13 / 255.0, alpha: 1))
    ctx.fill([CGRect(x: 8, y: 4, width: 2, height: 4), CGRect(x: 22, y: 4, width: 2, height: 4)])
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

for tamanho in [16, 32, 128, 256, 512] {
    try desenhar(tamanho).write(to: URL(fileURLWithPath: "\(pasta)/icon_\(tamanho)x\(tamanho).png"))
    try desenhar(tamanho * 2).write(to: URL(fileURLWithPath: "\(pasta)/icon_\(tamanho)x\(tamanho)@2x.png"))
}
