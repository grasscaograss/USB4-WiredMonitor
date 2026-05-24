import Foundation
import CoreGraphics

// MARK: - Protocol Constants

enum PacketType: UInt16 {
    case hello = 0x0001
    case helloAck = 0x0002
    case displayInfo = 0x0010
    case frameRequest = 0x0020
    case frameH264 = 0x0030
    case frameRaw = 0x0031
    case inputEvent = 0x0040
    case stats = 0x0050
}

let ProtocolMagic: UInt16 = 0x574D  // "WM"
let ProtocolVersion: UInt16 = 0x0001
let ControlPort: UInt16 = 9801
let VideoPort: UInt16 = 9802

func videoDimensionAlignment() -> Int {
    if let value = ProcessInfo.processInfo.environment["WIRED_MONITOR_ALIGN"],
       let parsed = Int(value),
       parsed >= 2,
       parsed <= 128 {
        return parsed
    }

    return 16
}

func alignVideoDimension(_ value: Int) -> Int {
    let alignment = videoDimensionAlignment()
    return max(alignment, (value / alignment) * alignment)
}

// MARK: - Packet Header (10 bytes)

struct PacketHeader {
    let type: PacketType
    let payloadLength: UInt32

    func encode() -> Data {
        var data = Data(capacity: 10)
        var m = ProtocolMagic.littleEndian
        var v = ProtocolVersion.littleEndian
        var t = type.rawValue.littleEndian
        var p = payloadLength.littleEndian

        data.append(Data(bytes: &m, count: 2))
        data.append(Data(bytes: &v, count: 2))
        data.append(Data(bytes: &t, count: 2))
        data.append(Data(bytes: &p, count: 4))
        return data
    }
}
