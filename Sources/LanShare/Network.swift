import Darwin
import Foundation
import IOKit
import SystemConfiguration

struct NetworkPort: Identifiable, Equatable, Sendable {
    let id: String
    let name: String
    let device: String
    let kind: Kind
    let hasLink: Bool
    let hasIPv4: Bool
    let isUSBAdapter: Bool
    var hasIPv6 = false

    enum Kind: Sendable {
        case wifi, ethernet, localNetwork, other
    }

    var canProvideInternet: Bool { id != device && (hasIPv4 || hasIPv6) }
    var canReceiveSharing: Bool { kind != .other }
    var title: String {
        if kind == .wifi || name.contains("(\(device))") { return name }
        return "\(name) · \(device)"
    }

}

struct NetworkSnapshot: Sendable {
    let ports: [NetworkPort]
    let primaryID: String?

    static func read() -> Self {
        let store = SCDynamicStoreCreate(nil, "LanShare" as CFString, nil, nil)
        func state(_ key: String) -> [String: Any] {
            store.flatMap { SCDynamicStoreCopyValue($0, key as CFString) as? [String: Any] } ?? [:]
        }

        let ipv4 = state("State:/Network/Global/IPv4")
        let ipv6 = state("State:/Network/Global/IPv6")
        let addresses = interfaceAddresses()
        let interfaces = SCNetworkInterfaceCopyAll() as? [SCNetworkInterface] ?? []
        let preferences = SCPreferencesCreate(nil, "LanShare" as CFString, nil)
        let currentSet = preferences.flatMap { SCNetworkSetCopyCurrent($0) }
        let services = currentSet.flatMap { SCNetworkSetCopyServices($0) as? [SCNetworkService] } ?? []

        func port(for interface: SCNetworkInterface, id: String, name: String?) -> NetworkPort? {
            guard let device = SCNetworkInterfaceGetBSDName(interface) as String?,
                  if_nametoindex(device) != 0 else { return nil }
            let type = SCNetworkInterfaceGetInterfaceType(interface) as String? ?? ""
            let hardwareName = SCNetworkInterfaceGetLocalizedDisplayName(interface) as String? ?? ""
            let kind: NetworkPort.Kind
            if type == kSCNetworkInterfaceTypeIEEE80211 as String {
                kind = .wifi
            } else if type == kSCNetworkInterfaceTypeEthernet as String {
                // These system ports expose Ethernet semantics without a wired Ethernet jack.
                if registryHasAncestor("IOUSBDeviceInterface", for: device) { return nil }
                if registryHasAncestor("AppleUSBEthernetHostAQM", for: device)
                    || registryHasAncestor("AppleUSBEthernetHost", for: device) {
                    // An iPhone's USB tether supplies a route to the Mac.
                    kind = .other
                } else if registryHasAncestor("AppleThunderboltIPPort", for: device) {
                    // A Thunderbolt service is usable, but its hidden member ports aren't.
                    guard id != device else { return nil }
                    kind = .localNetwork
                } else {
                    kind = .ethernet
                }
            } else if ["Bond", "Bridge", "VLAN", "FireWire", "Bluetooth"].contains(type) {
                kind = .localNetwork
            } else {
                kind = .other
            }
            return NetworkPort(
                id: id, name: name ?? (hardwareName.isEmpty ? device : hardwareName),
                device: device, kind: kind,
                hasLink: state("State:/Network/Interface/\(device)/Link")["Active"] as? Bool
                    ?? addresses.running.contains(device),
                hasIPv4: addresses.ipv4.contains(device),
                isUSBAdapter: kind == .ethernet && registryHasAncestor("IOUSBHostDevice", for: device),
                hasIPv6: addresses.ipv6.contains(device)
            )
        }

        var ports = services.compactMap { service -> NetworkPort? in
            guard SCNetworkServiceGetEnabled(service),
                  let interface = SCNetworkServiceGetInterface(service),
                  let id = SCNetworkServiceGetServiceID(service) as String? else { return nil }
            return port(for: interface, id: id, name: SCNetworkServiceGetName(service) as String?)
        }
        let configuredDevices = Set(services.compactMap { service in
            SCNetworkServiceGetInterface(service).flatMap { SCNetworkInterfaceGetBSDName($0) as String? }
        })
        for interface in interfaces {
            guard let device = SCNetworkInterfaceGetBSDName(interface) as String?,
                  !configuredDevices.contains(device),
                  let candidate = port(for: interface, id: device, name: nil),
                  candidate.canReceiveSharing else { continue }
            ports.append(candidate)
        }

        let primary = [ipv4, ipv6].lazy.compactMap { global in
            ports.first { $0.id == global["PrimaryService"] as? String }
                ?? ports.first { $0.device == global["PrimaryInterface"] as? String }
        }.first ?? ports.first { $0.kind == .wifi && $0.canProvideInternet }
            ?? ports.first { $0.canProvideInternet }

        return Self(ports: ports, primaryID: primary?.id)
    }

    func destinations(excluding source: NetworkPort?) -> [NetworkPort] {
        var seen = Set<String>()
        return ports.filter { $0.canReceiveSharing && $0.device != source?.device && seen.insert($0.device).inserted }
            .sorted { lhs, rhs in
                if lhs.kind == .ethernet, lhs.hasLink, rhs.kind != .ethernet { return true }
                if rhs.kind == .ethernet, rhs.hasLink, lhs.kind != .ethernet { return false }
                if lhs.hasLink != rhs.hasLink { return lhs.hasLink }
                if lhs.isUSBAdapter != rhs.isUSBAdapter { return lhs.isUSBAdapter }
                return lhs.title.localizedStandardCompare(rhs.title) == .orderedAscending
            }
    }

    private static func registryHasAncestor(_ className: String, for device: String) -> Bool {
        let matching = IOBSDNameMatching(kIOMainPortDefault, 0, device)
        var entry = IOServiceGetMatchingService(kIOMainPortDefault, matching)
        while entry != 0 {
            let matches = IOObjectConformsTo(entry, className) != 0
            var parent: io_registry_entry_t = 0
            if !matches { IORegistryEntryGetParentEntry(entry, kIOServicePlane, &parent) }
            IOObjectRelease(entry)
            if matches { return true }
            entry = parent
        }
        return false
    }

    private static func interfaceAddresses() -> (running: Set<String>, ipv4: Set<String>, ipv6: Set<String>) {
        var first: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&first) == 0, let first else { return ([], [], []) }
        defer { freeifaddrs(first) }
        var running = Set<String>()
        var ipv4 = Set<String>()
        var ipv6 = Set<String>()
        for pointer in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let entry = pointer.pointee
            let device = String(cString: entry.ifa_name)
            if entry.ifa_flags & UInt32(IFF_RUNNING) != 0 { running.insert(device) }
            guard let address = entry.ifa_addr else { continue }
            if address.pointee.sa_family == AF_INET6 {
                let isRoutable = address.withMemoryRebound(to: sockaddr_in6.self, capacity: 1) {
                    // Global unicast and unique-local prefixes can carry routed traffic.
                    let firstByte = $0.pointee.sin6_addr.__u6_addr.__u6_addr8.0
                    return firstByte & 0xe0 == 0x20 || firstByte & 0xfe == 0xfc
                }
                if isRoutable { ipv6.insert(device) }
            }
            guard address.pointee.sa_family == AF_INET else { continue }
            let bytes = address.withMemoryRebound(to: sockaddr_in.self, capacity: 1) {
                UInt32(bigEndian: $0.pointee.sin_addr.s_addr)
            }
            if bytes >> 24 != 0, bytes >> 24 != 127, bytes >> 24 < 224, bytes >> 16 != 0xA9FE { ipv4.insert(device) }
        }
        return (running, ipv4, ipv6)
    }
}

struct InterfaceTraffic: Sendable {
    let device: String
    let index: UInt32
    let received: UInt64
    let sent: UInt64

    static func read(device: String) -> Self? {
        let index = if_nametoindex(device)
        guard index != 0 else { return nil }
        var query = [CTL_NET, PF_ROUTE, 0, 0, NET_RT_IFLIST2, Int32(index)]
        var size = 0
        let headerSize = MemoryLayout<if_msghdr2>.size
        guard sysctl(&query, UInt32(query.count), nil, &size, nil, 0) == 0,
              size >= headerSize else { return nil }
        var buffer = [UInt8](repeating: 0, count: size)
        return buffer.withUnsafeMutableBytes { bytes in
            guard sysctl(&query, UInt32(query.count), bytes.baseAddress, &size, nil, 0) == 0,
                  size >= headerSize else { return nil }
            let header = bytes.loadUnaligned(as: if_msghdr2.self)
            guard header.ifm_version == RTM_VERSION, header.ifm_type == RTM_IFINFO2,
                  header.ifm_msglen >= headerSize, header.ifm_index == index else { return nil }
            return Self(device: device, index: index, received: header.ifm_data.ifi_ibytes, sent: header.ifm_data.ifi_obytes)
        }
    }
}

struct SessionTraffic {
    private var last: InterfaceTraffic?
    private(set) var bytes: UInt64 = 0

    mutating func update(_ sample: InterfaceTraffic?) {
        guard let sample else {
            last = nil
            return
        }
        if let last, last.device == sample.device, last.index == sample.index {
            if sample.received >= last.received { bytes += sample.received - last.received }
            if sample.sent >= last.sent { bytes += sample.sent - last.sent }
        }
        last = sample
    }
}
