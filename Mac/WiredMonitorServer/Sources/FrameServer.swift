import Foundation
import Network

class FrameServer {
    private var listener: NWListener?
    private var connections: [NWConnection] = []
    private var lastFramePacket: Data?
    private var sendingConnectionIds: Set<ObjectIdentifier> = []
    private var pendingPackets: [ObjectIdentifier: Data] = [:]
    private var droppedPendingFrames: UInt64 = 0
    private var lastDropReportTime = Date()
    private let port: UInt16
    private let queue = DispatchQueue(label: "com.wiredmonitor.server", qos: .userInteractive)

    var clientCount: Int { connections.count }
    var onFirstClientConnected: (() -> Void)?

    init(port: UInt16) {
        self.port = port
    }

    func start() -> Bool {
        let tcpOptions = NWProtocolTCP.Options()
        tcpOptions.noDelay = true
        let params = NWParameters(tls: nil, tcp: tcpOptions)

        do {
            listener = try NWListener(using: params, on: NWEndpoint.Port(rawValue: port)!)
        } catch {
            print("[服务端] 创建监听器失败: \(error)")
            return false
        }

        listener?.stateUpdateHandler = { state in
            if case .ready = state {
                print("[服务端] TCP 监听端口 \(self.port)")
            }
        }

        listener?.newConnectionHandler = { [weak self] conn in
            conn.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    print("[服务端] 客户端已连接: \(conn.endpoint)")
                    let wasEmpty = self?.connections.isEmpty ?? true
                    self?.connections.append(conn)
                    if let packet = self?.lastFramePacket {
                        self?.sendPacket(packet, to: conn)
                    }
                    if wasEmpty {
                        self?.onFirstClientConnected?()
                    }
                case .failed, .cancelled:
                    self?.removeConnection(conn)
                default:
                    break
                }
            }
            conn.start(queue: self?.queue ?? .main)
        }

        listener?.start(queue: queue)
        return true
    }

    func stop() {
        listener?.cancel()
        connections.forEach { $0.cancel() }
        connections.removeAll()
        sendingConnectionIds.removeAll()
        pendingPackets.removeAll()
        print("[服务端] 已停止")
    }

    func sendFrame(data: Data, packetType: PacketType, cacheForNewClients: Bool = true) {
        var packet = Data(capacity: 10 + data.count)
        let header = PacketHeader(type: packetType, payloadLength: UInt32(data.count))
        packet.append(header.encode())
        packet.append(data)

        queue.async { [weak self] in
            guard let self else { return }
            if cacheForNewClients {
                self.lastFramePacket = packet
            }
            guard !self.connections.isEmpty else { return }

            for conn in self.connections {
                let id = ObjectIdentifier(conn)
                if self.sendingConnectionIds.contains(id) {
                    if self.pendingPackets[id] != nil {
                        self.droppedPendingFrames += 1
                    }
                    self.pendingPackets[id] = packet
                    self.reportDroppedFramesIfNeeded()
                } else {
                    self.sendPacket(packet, to: conn)
                }
            }
        }
    }

    private func sendPacket(_ packet: Data, to conn: NWConnection) {
        let id = ObjectIdentifier(conn)
        sendingConnectionIds.insert(id)

        conn.send(content: packet, completion: .contentProcessed { [weak self] error in
            guard let self else { return }

            self.queue.async {
                if let error = error {
                    print("[服务端] 发送失败: \(error)")
                    self.removeConnection(conn)
                    return
                }

                if let pending = self.pendingPackets.removeValue(forKey: id),
                   self.connections.contains(where: { $0 === conn }) {
                    self.sendPacket(pending, to: conn)
                } else {
                    self.sendingConnectionIds.remove(id)
                }
            }
        })
    }

    private func removeConnection(_ conn: NWConnection) {
        let id = ObjectIdentifier(conn)
        connections.removeAll { $0 === conn }
        sendingConnectionIds.remove(id)
        pendingPackets.removeValue(forKey: id)
    }

    private func reportDroppedFramesIfNeeded() {
        let now = Date()
        guard droppedPendingFrames > 0, now.timeIntervalSince(lastDropReportTime) >= 1.0 else { return }

        print("[服务端] 发送队列丢弃旧帧: \(droppedPendingFrames)")
        droppedPendingFrames = 0
        lastDropReportTime = now
    }
}
