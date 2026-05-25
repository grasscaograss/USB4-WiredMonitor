import Foundation
import Network

struct ClientDisplayInfo {
    let width: Int
    let height: Int
    let refreshRate: Int
    let dpi: Int
}

class FrameServer {
    private static let queueKey = DispatchSpecificKey<Void>()
    private static let maxControlPayloadLength = 1024 * 1024

    private enum PacketKind {
        case frame
        case realtime
    }

    private var listener: NWListener?
    private var connections: [NWConnection] = []
    private var lastFramePacket: Data?
    private var sendingConnectionIds: Set<ObjectIdentifier> = []
    private var pendingFramePackets: [ObjectIdentifier: Data] = [:]
    private var pendingRealtimePackets: [ObjectIdentifier: Data] = [:]
    private var receiveBuffers: [ObjectIdentifier: Data] = [:]
    private var lastClientDisplayInfo: ClientDisplayInfo?
    private var firstClientNotificationSent = false
    private var droppedPendingFrames: UInt64 = 0
    private var lastDropReportTime = Date()
    private let port: UInt16
    private let queue = DispatchQueue(label: "com.wiredmonitor.server", qos: .userInteractive)

    var clientCount: Int {
        syncOnQueue {
            connections.count
        }
    }
    var isFrameBackpressured: Bool {
        syncOnQueue {
            !sendingConnectionIds.isEmpty || !pendingFramePackets.isEmpty
        }
    }
    var onFirstClientConnected: ((ClientDisplayInfo?) -> Void)?

    init(port: UInt16) {
        self.port = port
        queue.setSpecific(key: Self.queueKey, value: ())
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
                    self?.receiveControlPackets(from: conn)
                    if let packet = self?.lastFramePacket {
                        self?.sendPacket(packet, to: conn, kind: .frame)
                    }
                    if wasEmpty {
                        self?.notifyFirstClientAfterHelloTimeout()
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
        pendingFramePackets.removeAll()
        pendingRealtimePackets.removeAll()
        receiveBuffers.removeAll()
        print("[服务端] 已停止")
    }

    func sendFrame(data: Data, packetType: PacketType, cacheForNewClients: Bool = true) {
        var packet = Data(capacity: 10 + data.count)
        let header = PacketHeader(type: packetType, payloadLength: UInt32(data.count))
        packet.append(header.encode())
        packet.append(data)

        syncOnQueue {
            if cacheForNewClients {
                self.lastFramePacket = packet
            }
            guard !self.connections.isEmpty else { return }

            for conn in self.connections {
                let id = ObjectIdentifier(conn)
                if self.sendingConnectionIds.contains(id) {
                    if self.pendingFramePackets[id] != nil {
                        self.droppedPendingFrames += 1
                    }
                    self.pendingFramePackets[id] = packet
                    self.reportDroppedFramesIfNeeded()
                } else {
                    self.sendPacket(packet, to: conn, kind: .frame)
                }
            }
        }
    }

    func sendRealtime(data: Data, packetType: PacketType) {
        var packet = Data(capacity: PacketHeader.size + data.count)
        let header = PacketHeader(type: packetType, payloadLength: UInt32(data.count))
        packet.append(header.encode())
        packet.append(data)

        queue.async { [weak self] in
            guard let self, !self.connections.isEmpty else { return }

            for conn in self.connections {
                let id = ObjectIdentifier(conn)
                if self.sendingConnectionIds.contains(id) {
                    self.pendingRealtimePackets[id] = packet
                } else {
                    self.sendPacket(packet, to: conn, kind: .realtime)
                }
            }
        }
    }

    private func sendPacket(_ packet: Data, to conn: NWConnection, kind: PacketKind) {
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

                if let pending = self.nextPendingPacket(after: kind, for: id),
                   self.connections.contains(where: { $0 === conn }) {
                    self.sendPacket(pending.packet, to: conn, kind: pending.kind)
                } else {
                    self.sendingConnectionIds.remove(id)
                }
            }
        })
    }

    private func nextPendingPacket(after kind: PacketKind, for id: ObjectIdentifier) -> (packet: Data, kind: PacketKind)? {
        switch kind {
        case .frame:
            if let packet = pendingRealtimePackets.removeValue(forKey: id) {
                return (packet, .realtime)
            }
            if let packet = pendingFramePackets.removeValue(forKey: id) {
                return (packet, .frame)
            }
        case .realtime:
            if let packet = pendingFramePackets.removeValue(forKey: id) {
                return (packet, .frame)
            }
            if let packet = pendingRealtimePackets.removeValue(forKey: id) {
                return (packet, .realtime)
            }
        }

        return nil
    }

    private func removeConnection(_ conn: NWConnection) {
        let id = ObjectIdentifier(conn)
        connections.removeAll { $0 === conn }
        sendingConnectionIds.remove(id)
        pendingFramePackets.removeValue(forKey: id)
        pendingRealtimePackets.removeValue(forKey: id)
        receiveBuffers.removeValue(forKey: id)
    }

    private func reportDroppedFramesIfNeeded() {
        let now = Date()
        guard droppedPendingFrames > 0, now.timeIntervalSince(lastDropReportTime) >= 1.0 else { return }

        print("[服务端] 发送队列丢弃旧帧: \(droppedPendingFrames)")
        droppedPendingFrames = 0
        lastDropReportTime = now
    }

    private func receiveControlPackets(from conn: NWConnection) {
        conn.receive(minimumIncompleteLength: 1, maximumLength: 4096) { [weak self, weak conn] data, _, isComplete, error in
            guard let self, let conn else { return }

            self.queue.async {
                let id = ObjectIdentifier(conn)

                if let data, !data.isEmpty {
                    var buffer = self.receiveBuffers[id] ?? Data()
                    buffer.append(data)
                    self.receiveBuffers[id] = buffer
                    self.processControlBuffer(for: conn)
                }

                if isComplete || error != nil {
                    if let error {
                        print("[服务端] 控制接收失败: \(error)")
                    }
                    self.removeConnection(conn)
                    return
                }

                if self.connections.contains(where: { $0 === conn }) {
                    self.receiveControlPackets(from: conn)
                }
            }
        }
    }

    private func processControlBuffer(for conn: NWConnection) {
        let id = ObjectIdentifier(conn)
        var buffer = receiveBuffers[id] ?? Data()

        while buffer.count >= PacketHeader.size {
            let headerData = buffer.prefix(PacketHeader.size)
            guard let header = PacketHeader.decode(Data(headerData)) else {
                print("[服务端] 收到无效控制包头，丢弃 \(buffer.count) 字节")
                buffer.removeAll()
                break
            }

            if header.payloadLength > UInt32(FrameServer.maxControlPayloadLength) {
                print("[服务端] 控制包过大: \(header.payloadLength)")
                buffer.removeAll()
                break
            }

            let packetSize = PacketHeader.size + Int(header.payloadLength)
            guard buffer.count >= packetSize else {
                break
            }

            let payload = buffer.subdata(in: PacketHeader.size..<packetSize)
            buffer.removeSubrange(0..<packetSize)
            handleControlPacket(header: header, payload: payload, from: conn)
        }

        receiveBuffers[id] = buffer
    }

    private func handleControlPacket(header: PacketHeader, payload: Data, from conn: NWConnection) {
        switch header.type {
        case .hello:
            guard payload.count >= 12 else {
                print("[服务端] HELLO payload 过短: \(payload.count)")
                return
            }

            let info = ClientDisplayInfo(
                width: Int(readUInt32(payload, offset: 0)),
                height: Int(readUInt32(payload, offset: 4)),
                refreshRate: Int(readUInt32(payload, offset: 8)),
                dpi: payload.count >= 16 ? Int(readUInt32(payload, offset: 12)) : 0)
            lastClientDisplayInfo = info
            print("[服务端] 收到客户端显示信息: \(info.width)x\(info.height) @ \(info.refreshRate)Hz, dpi=\(info.dpi)")
            notifyFirstClientIfNeeded(info: info)

        default:
            break
        }
    }

    private func notifyFirstClientAfterHelloTimeout() {
        queue.asyncAfter(deadline: .now() + .milliseconds(300)) { [weak self] in
            guard let self, !self.connections.isEmpty else { return }
            self.notifyFirstClientIfNeeded(info: self.lastClientDisplayInfo)
        }
    }

    private func notifyFirstClientIfNeeded(info: ClientDisplayInfo?) {
        guard !firstClientNotificationSent else { return }
        firstClientNotificationSent = true
        onFirstClientConnected?(info)
    }

    private func syncOnQueue<T>(_ work: () -> T) -> T {
        if DispatchQueue.getSpecific(key: Self.queueKey) != nil {
            return work()
        }

        return queue.sync(execute: work)
    }
}
