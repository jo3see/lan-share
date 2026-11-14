import Foundation

struct SharingConfiguration: Codable, Equatable, Sendable {
    // Store the original values, including missing keys and their property list types.
    private let data: Data
    private static let keys = ["Enabled", "PrimaryInterface", "PrimaryService", "SharingDevices"]

    init(preferences: [String: Any]) throws {
        let values = preferences.filter { Self.keys.contains($0.key) }
        data = try PropertyListSerialization.data(fromPropertyList: values, format: .binary, options: 0)
    }

    var values: [String: Any] {
        // The journal is validated when decoded, and new snapshots come from a property list.
        (try? Self.decode(data)) ?? [:]
    }

    var enabled: Bool { (values["Enabled"] as? NSNumber)?.boolValue == true }
    var sourceID: String? { values["PrimaryService"] as? String }
    var destinations: [String] { values["SharingDevices"] as? [String] ?? [] }

    func matches(sourceID: String, destination: String) -> Bool {
        enabled && self.sourceID == sourceID && destinations == [destination]
    }

    func applying(to preferences: [String: Any]) -> [String: Any] {
        var result = preferences
        let saved = values
        for key in Self.keys { result[key] = saved[key] }
        return result
    }

    static func active(source: NetworkPort, destination: NetworkPort) throws -> Self {
        try Self(preferences: [
            "Enabled": 1,
            "PrimaryService": source.id,
            "PrimaryInterface": ["Device": source.device, "Enabled": 0,
                                 "HardwareKey": "", "PrimaryUserReadable": source.name] as [String: Any],
            "SharingDevices": [destination.device],
        ])
    }

    static func == (lhs: Self, rhs: Self) -> Bool {
        NSDictionary(dictionary: lhs.values).isEqual(to: rhs.values)
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        let data = try container.decode(Data.self)
        _ = try Self.decode(data)
        self.data = data
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(data)
    }

    private static func decode(_ data: Data) throws -> [String: Any] {
        guard let values = try PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
              values.keys.allSatisfy(Self.keys.contains) else {
            throw SharingError.message("The saved Internet Sharing settings could not be read.")
        }
        return values
    }

    var hasValidRecoveryValues: Bool {
        let values = self.values
        let validDevice = { (name: String) in
            name.wholeMatch(of: #/[a-z][a-z0-9]{0,11}[0-9]{1,3}/#) != nil
        }
        if let enabled = values["Enabled"], (enabled as? NSNumber).map({ $0 == 0 || $0 == 1 }) != true { return false }
        if let source = values["PrimaryService"], (source as? String).flatMap(UUID.init(uuidString:)) == nil { return false }
        if let devices = values["SharingDevices"],
           (devices as? [String]).map({ $0.allSatisfy(validDevice) }) != true { return false }
        if let primary = values["PrimaryInterface"] {
            guard let dictionary = primary as? [String: Any],
                  dictionary.keys.allSatisfy(["Device", "Enabled", "HardwareKey", "PrimaryUserReadable"].contains),
                  let device = dictionary["Device"] as? String, validDevice(device),
                  dictionary.values.allSatisfy({ $0 is String || $0 is NSNumber }) else { return false }
        }
        return true
    }
}

enum SharingError: LocalizedError {
    case message(String)
    case recoveryFailed(original: String, recovery: String)

    var errorDescription: String? {
        switch self {
        case .message(let message): return message
        case .recoveryFailed(let original, let recovery):
            return "\(original) Restoring Internet Sharing also failed. \(recovery)"
        }
    }
}

struct LegacySharingConfiguration: Decodable {
    struct Destination: Decodable {
        let title: String
        let count: Int
    }

    let enabled: Bool
    let source: String
    let destinations: [Destination]
}

protocol SharingControls: AnyObject {
    func read() throws -> SharingConfiguration
    func apply(_ next: SharingConfiguration, replacing current: SharingConfiguration) throws
    func migrate(_ previous: LegacySharingConfiguration) throws -> SharingConfiguration
    func canShare(from source: NetworkPort, to destination: NetworkPort) -> Bool
    func sharingInterface(to device: String, wifi: Bool) -> String?
    func requireSavedHotspot(on device: String) throws
    func restoreHotspot(_ record: HotspotRestoreRecord) throws
}

// The caller serializes access to the controls and journal on the sharing queue.
final class SharingSession: @unchecked Sendable {
    enum StartResult: Sendable {
        case started, alreadySharing
    }

    private struct RestoreRecord: Codable {
        let previous: SharingConfiguration
        let applied: SharingConfiguration
        var wifiDevice: String? = nil
        // Older builds also recorded temporary changes to hotspot credentials.
        var hotspot: HotspotRestoreRecord? = nil
    }

    private let controls: SharingControls
    private let journal: URL
    private let waitLimit: TimeInterval

    init(controls: SharingControls, journal: URL, waitLimit: TimeInterval = 8) {
        self.controls = controls
        self.journal = journal
        self.waitLimit = waitLimit
    }

    var needsRestoration: Bool { FileManager.default.fileExists(atPath: journal.path) }

    func start(source: NetworkPort, destination: NetworkPort) throws -> StartResult {
        guard !needsRestoration else {
            throw SharingError.message("Restore the previous Internet Sharing settings before starting again.")
        }
        guard source.device != destination.device, destination.canReceiveSharing,
              controls.canShare(from: source, to: destination) else {
            throw SharingError.message("Choose an active internet connection and a separate sharing adapter.")
        }
        let isWiFi = destination.kind == .wifi
        let previous = try controls.read()
        if isWiFi, (previous.enabled && previous.destinations.contains(destination.device))
            || controls.sharingInterface(to: destination.device, wifi: true) != nil {
            throw SharingError.message("This Mac already has an Internet Sharing hotspot. Stop it before starting a new hotspot.")
        }
        if previous.matches(sourceID: source.id, destination: destination.device),
           controls.sharingInterface(to: destination.device, wifi: isWiFi) != nil {
            return .alreadySharing
        }
        if isWiFi { try controls.requireSavedHotspot(on: destination.device) }

        let applied = try SharingConfiguration.active(source: source, destination: destination)
        try FileManager.default.createDirectory(at: journal.deletingLastPathComponent(),
                                                withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        try JSONEncoder().encode(RestoreRecord(previous: previous, applied: applied,
                                               wifiDevice: isWiFi ? destination.device : nil))
            .write(to: journal, options: .atomic)
        do {
            try controls.apply(applied, replacing: previous)
            guard waitUntil({ controls.sharingInterface(to: destination.device, wifi: isWiFi) != nil }),
                  try controls.read() == applied else {
                let details = isWiFi ? " Check the saved Wi-Fi hotspot in System Settings." : ""
                throw SharingError.message("macOS did not start Internet Sharing on \(destination.name).\(details)")
            }
            return .started
        } catch {
            let original = error
            do {
                try restore()
            } catch {
                throw SharingError.recoveryFailed(original: original.localizedDescription,
                                                  recovery: error.localizedDescription)
            }
            throw original
        }
    }

    func restore() throws {
        guard needsRestoration else { return }
        let data = try Data(contentsOf: journal)
        let current = try controls.read()
        let record: RestoreRecord
        if let legacy = try? JSONDecoder().decode(LegacySharingConfiguration.self, from: data) {
            record = RestoreRecord(previous: try controls.migrate(legacy), applied: current)
            try JSONEncoder().encode(record).write(to: journal, options: .atomic)
        } else {
            record = try JSONDecoder().decode(RestoreRecord.self, from: data)
        }

        let previousDevices = record.previous.enabled ? record.previous.destinations : []
        let stoppedDevices = Set(record.applied.destinations).subtracting(previousDevices)
        let wifiDevice = record.wifiDevice ?? record.hotspot?.device
        let stopped = {
            stoppedDevices.allSatisfy { device in
                self.controls.sharingInterface(to: device, wifi: device == wifiDevice) == nil
            }
        }
        if current == record.applied || (current == record.previous && !stopped()) {
            try controls.apply(record.previous, replacing: current)
        } else if current != record.previous {
            // An external change has superseded this session.
            if let hotspot = record.hotspot {
                guard !(current.enabled && current.destinations.contains(hotspot.device)), waitUntil(stopped) else {
                    throw SharingError.message("Stop the current macOS hotspot before restoring the previous Wi-Fi settings.")
                }
                try controls.restoreHotspot(hotspot)
            }
            try FileManager.default.removeItem(at: journal)
            return
        }

        guard waitUntil(stopped), try controls.read() == record.previous else {
            throw SharingError.message("macOS did not restore the previous Internet Sharing settings.")
        }
        if let hotspot = record.hotspot { try controls.restoreHotspot(hotspot) }
        try FileManager.default.removeItem(at: journal)
    }

    func recoverPreviousBuild(_ data: Data) throws {
        guard !needsRestoration, data.count <= 65_536 else {
            throw SharingError.message("The previous LAN Share session could not be recovered.")
        }
        let current = try controls.read()
        let record: RestoreRecord
        if let legacy = try? JSONDecoder().decode(LegacySharingConfiguration.self, from: data) {
            record = RestoreRecord(previous: try controls.migrate(legacy), applied: current)
        } else {
            record = try JSONDecoder().decode(RestoreRecord.self, from: data)
        }
        guard record.hotspot == nil, record.wifiDevice == nil,
              record.previous.hasValidRecoveryValues, record.applied.hasValidRecoveryValues else {
            throw SharingError.message("The previous LAN Share settings could not be verified.")
        }
        try FileManager.default.createDirectory(at: journal.deletingLastPathComponent(),
                                                withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        try JSONEncoder().encode(record).write(to: journal, options: .atomic)
        try restore()
    }

    private func waitUntil(_ ready: () -> Bool) -> Bool {
        let deadline = ProcessInfo.processInfo.systemUptime + waitLimit
        repeat {
            if ready() { return true }
            if ProcessInfo.processInfo.systemUptime >= deadline { return false }
            Thread.sleep(forTimeInterval: 0.2)
        } while true
    }
}
