import Foundation

@main
struct SharingSessionChecks {
    static func main() throws {
        let wifi = NetworkPort(id: "wifi", name: "Wi-Fi", device: "en0", kind: .wifi,
                               hasLink: true, hasIPv4: true, isUSBAdapter: false)
        let adapter = NetworkPort(id: "usb", name: "USB Ethernet", device: "en7", kind: .ethernet,
                                  hasLink: true, hasIPv4: false, isUSBAdapter: true)
        let active = try SharingConfiguration.active(source: wifi, destination: adapter)
        let off = try SharingConfiguration(preferences: [:])
        let stale = try SharingConfiguration(preferences: ["Enabled": 1, "PrimaryService": wifi.id,
                                                           "SharingDevices": ["en8"]])
        let directory = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        let journal = directory.appending(path: "restore.json")
        defer { try? FileManager.default.removeItem(at: directory) }

        let controls = FakeControls(active)
        let session = SharingSession(controls: controls, journal: journal, waitLimit: 0)
        let result = try session.start(source: wifi, destination: adapter)
        precondition(result == .alreadySharing && !session.needsRestoration && controls.writes == 0)
        try session.restore()
        precondition(controls.config == active && controls.writes == 0)

        for baseline in [off, stale] {
            controls.config = baseline
            let result = try session.start(source: wifi, destination: adapter)
            precondition(result == .started && session.needsRestoration && controls.config == active)
            try SharingSession(controls: controls, journal: journal, waitLimit: 0).restore()
            precondition(controls.config == baseline && !session.needsRestoration)
        }

        controls.config = stale
        controls.suppressBridge = true
        do {
            _ = try session.start(source: wifi, destination: adapter)
            preconditionFailure("Start succeeded without a sharing bridge")
        } catch is SharingError {
            precondition(controls.config == stale && !session.needsRestoration)
        }
        controls.suppressBridge = false

        _ = try session.start(source: wifi, destination: adapter)
        controls.failNextWrite = true
        do {
            try session.restore()
            preconditionFailure("Restore unexpectedly succeeded")
        } catch FakeControls.Failure.write {
            precondition(session.needsRestoration && controls.config == active)
        }
        try session.restore()
        precondition(controls.config == stale && !session.needsRestoration)

        _ = try session.start(source: wifi, destination: adapter)
        controls.config = off
        try session.restore()
        precondition(controls.config == off && !session.needsRestoration)

        try Data(#"{"enabled":false,"source":"Wi-Fi","destinations":[]}"#.utf8).write(to: journal)
        controls.config = active
        try session.restore()
        precondition(controls.config == off && !session.needsRestoration)

        let unrelated = ["Nat64": true, "Enabled": true, "SharingDevices": ["en7"]] as [String: Any]
        precondition(NSDictionary(dictionary: off.applying(to: unrelated)).isEqual(to: ["Nat64": true]))

        let upstream = NetworkPort(id: "cellular", name: "iPhone USB", device: "en8", kind: .other,
                                   hasLink: true, hasIPv4: true, isUSBAdapter: false)
        let network = NetworkSnapshot(ports: [wifi, adapter, upstream], primaryID: upstream.id)
        precondition(network.destinations(excluding: upstream).map(\.id) == [adapter.id, wifi.id])
        precondition(network.destinations(excluding: wifi).map(\.id) == [adapter.id])

        controls.hasSavedHotspot = false
        let writesBeforeWiFi = controls.writes
        do {
            _ = try session.start(source: upstream, destination: wifi)
            preconditionFailure("Start used Wi-Fi without a saved hotspot password")
        } catch FakeControls.Failure.missingHotspot {
            precondition(controls.writes == writesBeforeWiFi && !session.needsRestoration)
        }
        controls.hasSavedHotspot = true
        _ = try session.start(source: upstream, destination: wifi)
        precondition(controls.config.destinations == ["en0"] && session.needsRestoration)
        try SharingSession(controls: controls, journal: journal, waitLimit: 0).restore()
        precondition(controls.config == off && !session.needsRestoration && controls.checkedWiFiOnRestore)

        let otherHotspot = try SharingConfiguration(preferences: ["Enabled": 1, "PrimaryService": "other",
                                                                  "SharingDevices": [wifi.device]])
        controls.config = otherHotspot
        do {
            _ = try session.start(source: upstream, destination: wifi)
            preconditionFailure("Start replaced a hotspot outside of LAN Share")
        } catch is SharingError {
            precondition(controls.config == otherHotspot && !session.needsRestoration)
        }

        controls.config = off
        _ = try session.start(source: upstream, destination: wifi)
        // A journal from the previous build still needs to restore its temporary credentials.
        let oldHotspot = HotspotRestoreRecord(device: wifi.device, id: UUID(), previous: nil, applied: Data())
        let activeWiFi = controls.config
        try JSONEncoder().encode(OldJournal(previous: off, applied: activeWiFi, hotspot: oldHotspot))
            .write(to: journal)
        controls.config = otherHotspot
        do {
            try session.restore()
            preconditionFailure("Restore changed a hotspot outside of LAN Share")
        } catch is SharingError {
            precondition(!controls.restoredOldHotspot && controls.config == otherHotspot && session.needsRestoration)
        }
        controls.config = off
        try session.restore()
        precondition(controls.restoredOldHotspot && !session.needsRestoration)

        controls.failNextWrite = true
        do {
            _ = try session.start(source: upstream, destination: wifi)
            preconditionFailure("Start succeeded after a failed preference write")
        } catch FakeControls.Failure.write {
            precondition(controls.config == off && !session.needsRestoration)
        }

        var traffic = SessionTraffic()
        traffic.update(InterfaceTraffic(device: "en7", index: 7, received: 1_000, sent: 5_000))
        traffic.update(InterfaceTraffic(device: "en7", index: 7, received: 1_800, sent: 5_400))
        precondition(traffic.bytes == 1_200)
        traffic.update(InterfaceTraffic(device: "en7", index: 7, received: 5, sent: 5_600))
        traffic.update(InterfaceTraffic(device: "ap1", index: 9, received: 6_000, sent: 20_000))
        traffic.update(InterfaceTraffic(device: "ap1", index: 9, received: 6_600, sent: 20_300))
        precondition(traffic.bytes == 2_300)
        print("Passed sharing, rollback, hotspot recovery, adapter selection, and data counter checks.")
    }
}

private struct OldJournal: Encodable {
    let previous: SharingConfiguration
    let applied: SharingConfiguration
    let hotspot: HotspotRestoreRecord
}

private final class FakeControls: SharingControls {
    enum Failure: Error { case write, missingHotspot }

    var config: SharingConfiguration
    var writes = 0
    var failNextWrite = false
    var suppressBridge = false
    var hasSavedHotspot = true
    var checkedWiFiOnRestore = false
    var restoredOldHotspot = false

    init(_ config: SharingConfiguration) { self.config = config }

    func read() throws -> SharingConfiguration { config }

    func apply(_ next: SharingConfiguration, replacing current: SharingConfiguration) throws {
        precondition(current == config)
        if failNextWrite {
            failNextWrite = false
            throw Failure.write
        }
        writes += 1
        config = next
    }

    func migrate(_ previous: LegacySharingConfiguration) throws -> SharingConfiguration {
        precondition(!previous.enabled && previous.source == "Wi-Fi" && previous.destinations.isEmpty)
        return try SharingConfiguration(preferences: [:])
    }

    func canShare(from source: NetworkPort, to destination: NetworkPort) -> Bool { true }

    func sharingInterface(to device: String, wifi: Bool) -> String? {
        if device == "en0", wifi, !config.enabled { checkedWiFiOnRestore = true }
        guard !suppressBridge, config.enabled, config.destinations.contains(device) else { return nil }
        return wifi ? "ap1" : device
    }

    func requireSavedHotspot(on device: String) throws {
        precondition(device == "en0")
        if !hasSavedHotspot { throw Failure.missingHotspot }
    }

    func restoreHotspot(_ record: HotspotRestoreRecord) throws {
        precondition(!config.destinations.contains(record.device))
        restoredOldHotspot = true
    }
}
