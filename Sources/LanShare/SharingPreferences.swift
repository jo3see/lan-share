import Darwin
import Foundation
import SystemConfiguration

// macOS's Internet Sharing preference plugin applies changes to this store.
final class SharingPreferences: SharingControls, Sendable {
    private var preferencesID: CFString { "com.apple.nat.plist" as CFString }
    private var key: CFString { "NAT" as CFString }
    private var client: CFString { "LAN Share" as CFString }

    func read() throws -> SharingConfiguration {
        guard let preferences = SCPreferencesCreate(nil, client, preferencesID) else {
            throw failure("read")
        }
        return try SharingConfiguration(preferences: try values(in: preferences))
    }

    func apply(_ next: SharingConfiguration, replacing current: SharingConfiguration) throws {
        guard geteuid() == 0 else {
            throw SharingError.message("Install LAN Share using its installer package.")
        }
        guard let preferences = SCPreferencesCreate(nil, client, preferencesID),
              SCPreferencesLock(preferences, false) else { throw failure("get write access to") }
        defer { _ = SCPreferencesUnlock(preferences) }

        let existing = try values(in: preferences)
        guard try SharingConfiguration(preferences: existing) == current else {
            throw SharingError.message("Internet Sharing changed while LAN Share was working. Try again.")
        }
        let updated = next.applying(to: existing)
        if !NSDictionary(dictionary: existing).isEqual(to: updated) {
            let saved: Bool
            if updated.isEmpty {
                saved = SCPreferencesRemoveValue(preferences, key)
            } else {
                saved = SCPreferencesSetValue(preferences, key, updated as CFDictionary)
            }
            guard saved, SCPreferencesCommitChanges(preferences) else { throw failure("save") }
        }
        guard SCPreferencesApplyChanges(preferences) else { throw failure("apply") }
    }

    func canShare(from source: NetworkPort, to destination: NetworkPort) -> Bool {
        guard let services = networkServices(),
              let service = services.first(where: { SCNetworkServiceGetServiceID($0) as String? == source.id }),
              SCNetworkServiceGetEnabled(service),
              let interface = SCNetworkServiceGetInterface(service),
              SCNetworkInterfaceGetBSDName(interface) as String? == source.device else { return false }
        return if_nametoindex(source.device) != 0 && if_nametoindex(destination.device) != 0
    }

    func restoreHotspot(_ record: HotspotRestoreRecord) throws {
        try HotspotPreferences().restore(record)
    }

    func requireSavedHotspot(on device: String) throws {
        try HotspotPreferences().requireSavedPassword(on: device)
    }

    func sharingInterface(to device: String, wifi: Bool) -> String? {
        let command = Process()
        let output = Pipe()
        command.executableURL = URL(fileURLWithPath: "/sbin/ifconfig")
        command.standardOutput = output
        command.standardError = FileHandle.nullDevice
        do { try command.run() } catch { return nil }
        let interfaces = String(decoding: output.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        command.waitUntilExit()
        guard command.terminationStatus == 0 else { return nil }

        var sharingBridge = false
        var accessPoints: [String] = []
        for line in interfaces.split(separator: "\n") {
            if line.first?.isWhitespace == false {
                let name = line.prefix { $0 != ":" }
                let bridgeNumber = name.hasPrefix("bridge") ? Int(name.dropFirst("bridge".count)) : nil
                sharingBridge = bridgeNumber.map { $0 >= 100 } ?? false
            } else if sharingBridge {
                let fields = line.split(whereSeparator: \.isWhitespace)
                guard fields.count >= 2, fields[0] == "member:" else { continue }
                let member = String(fields[1])
                if member == device { return member }
                if wifi, member.wholeMatch(of: #/ap[0-9]+/#) != nil { accessPoints.append(member) }
            }
        }
        return accessPoints.count == 1 ? accessPoints.first : nil
    }

    func migrate(_ previous: LegacySharingConfiguration) throws -> SharingConfiguration {
        // Earlier versions recorded display names. Resolve them before changing any preferences.
        let services = networkServices() ?? []
        let source = services.filter { SCNetworkServiceGetName($0) as String? == previous.source }
        guard source.count == 1, let service = source.first,
              let serviceID = SCNetworkServiceGetServiceID(service) as String?,
              let interface = SCNetworkServiceGetInterface(service),
              let device = SCNetworkInterfaceGetBSDName(interface) as String? else {
            throw SharingError.message("Reconnect the previous internet source to restore the saved settings.")
        }
        let hardware = SCNetworkInterfaceCopyAll() as? [SCNetworkInterface] ?? []
        let destinations = try previous.destinations.map { destination in
            let serviceDevices = services.compactMap { service -> String? in
                guard SCNetworkServiceGetName(service) as String? == destination.title,
                      let interface = SCNetworkServiceGetInterface(service) else { return nil }
                return SCNetworkInterfaceGetBSDName(interface) as String?
            }
            let hardwareDevices = hardware.compactMap { interface -> String? in
                guard let device = SCNetworkInterfaceGetBSDName(interface) as String?,
                      SCNetworkInterfaceGetLocalizedDisplayName(interface) as String? == destination.title
                        || destination.title == "Ethernet Adapter (\(device))" else { return nil }
                return device
            }
            let devices = Set(serviceDevices + hardwareDevices)
            guard destination.count == 1, devices.count == 1, let device = devices.first else {
                throw SharingError.message("Reconnect \(destination.title) to restore the saved settings.")
            }
            return device
        }
        return try SharingConfiguration(preferences: [
            "Enabled": previous.enabled ? 1 : 0,
            "PrimaryService": serviceID,
            "PrimaryInterface": ["Device": device, "Enabled": 0,
                                 "HardwareKey": "", "PrimaryUserReadable": previous.source] as [String: Any],
            "SharingDevices": destinations,
        ])
    }

    private func networkServices() -> [SCNetworkService]? {
        guard let preferences = SCPreferencesCreate(nil, client, nil),
              let location = SCNetworkSetCopyCurrent(preferences) else { return nil }
        return SCNetworkSetCopyServices(location) as? [SCNetworkService]
    }

    private func values(in preferences: SCPreferences) throws -> [String: Any] {
        guard let value = SCPreferencesGetValue(preferences, key) else {
            if SCError() == kSCStatusNoKey { return [:] }
            throw failure("read")
        }
        guard let dictionary = value as? [String: Any] else { throw failure("read") }
        return dictionary
    }

    private func failure(_ operation: String) -> SharingError {
        let code = SCError()
        let reason = String(cString: SCErrorString(code))
        return .message("macOS could not \(operation) Internet Sharing settings (\(reason)).")
    }
}
