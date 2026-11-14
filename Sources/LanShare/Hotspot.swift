import Foundation

struct HotspotRestoreRecord: Codable, Sendable {
    let device: String
    let id: UUID
    let previous: Data?
    let applied: Data

    var label: String { "LAN Share \(id.uuidString)" }
    var service: String { Self.passwordService(for: device) }
    var backupService: String { "\(service).LANShare.\(id.uuidString)" }

    static func passwordService(for device: String) -> String { "com.apple.network.wlan.swap.ssid.\(device)" }
}
