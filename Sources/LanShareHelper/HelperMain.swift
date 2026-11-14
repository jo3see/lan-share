import Darwin
import Foundation
import Security
import SystemConfiguration

@main
struct SharingHelper {
    static func main() {
        guard geteuid() == 0 else { exit(1) }
        let requirementURL = URL(fileURLWithPath: "/Library/PrivilegedHelperTools/\(sharingHelperID).client-requirement")
        guard let requirement = try? String(contentsOf: requirementURL, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines),
              !requirement.isEmpty else { exit(1) }
        var parsed: SecRequirement?
        guard SecRequirementCreateWithString(requirement as CFString, [], &parsed) == errSecSuccess else { exit(1) }
        let server = SharingServer(requirement: requirement)
        let listener = NSXPCListener(machServiceName: sharingHelperID)
        listener.delegate = server
        listener.resume()
        withExtendedLifetime(server) { dispatchMain() }
    }
}

private final class SharingServer: NSObject, NSXPCListenerDelegate, @unchecked Sendable {
    let queue = DispatchQueue(label: sharingHelperID)
    let session = SharingSession(controls: SharingPreferences(), journal: URL(fileURLWithPath: "/var/db/local.lanshare.macos/restore.json"))
    var owner: UUID?
    private let requirement: String

    init(requirement: String) { self.requirement = requirement }

    func listener(_ listener: NSXPCListener, shouldAcceptNewConnection connection: NSXPCConnection) -> Bool {
        let client = SharingClient(server: self, uid: connection.effectiveUserIdentifier)
        connection.setCodeSigningRequirement(requirement)
        connection.exportedInterface = NSXPCInterface(with: SharingHelperProtocol.self)
        connection.exportedObject = client
        connection.invalidationHandler = { client.disconnect() }
        connection.resume()
        return true
    }
}

private final class SharingClient: NSObject, SharingHelperProtocol, @unchecked Sendable {
    private let server: SharingServer
    private let uid: uid_t
    private let id = UUID()

    init(server: SharingServer, uid: uid_t) {
        self.server = server
        self.uid = uid
    }

    func start(_ sourceID: String, destination: String, reply: @escaping (Int, String?) -> Void) {
        server.queue.async {
            do {
                try self.checkConsoleUser()
                guard self.server.owner == nil else {
                    throw SharingError.message("Another LAN Share window is already sharing.")
                }
                try self.server.session.restore()
                let network = NetworkSnapshot.read()
                guard let source = network.ports.first(where: { $0.id == sourceID && $0.canProvideInternet }),
                      let port = network.destinations(excluding: source).first(where: { $0.device == destination }) else {
                    throw SharingError.message("Choose an active internet connection and a separate sharing adapter.")
                }
                let result = try self.server.session.start(source: source, destination: port)
                if result == .started { self.server.owner = self.id }
                reply(result == .alreadySharing ? 1 : 0, nil)
            } catch { reply(0, error.localizedDescription) }
        }
    }

    func stop(reply: @escaping (String?) -> Void) {
        server.queue.async {
            do {
                if self.server.owner != self.id { try self.checkConsoleUser() }
                try self.restore()
                reply(nil)
            } catch { reply(error.localizedDescription) }
        }
    }

    func recover(_ journal: Data, reply: @escaping (String?) -> Void) {
        server.queue.async {
            do {
                try self.checkConsoleUser()
                guard self.server.owner == nil else {
                    throw SharingError.message("Stop the other LAN Share session before restoring settings.")
                }
                try self.server.session.restore()
                try self.server.session.recoverPreviousBuild(journal)
                reply(nil)
            } catch { reply(error.localizedDescription) }
        }
    }

    func status(reply: @escaping (Bool) -> Void) {
        server.queue.async { reply(self.server.owner == nil && self.server.session.needsRestoration) }
    }

    func disconnect() {
        let server = self.server
        let owner = id
        server.queue.async {
            guard server.owner == owner else { return }
            server.owner = nil
            try? server.session.restore()
        }
    }

    private func restore() throws {
        guard server.owner == nil || server.owner == id else {
            throw SharingError.message("Stop sharing from the LAN Share window that started it.")
        }
        try server.session.restore()
        server.owner = nil
    }

    private func checkConsoleUser() throws {
        var consoleUID: uid_t = 0
        guard SCDynamicStoreCopyConsoleUser(nil, &consoleUID, nil) != nil,
              uid != 0, uid == consoleUID else {
            throw SharingError.message("Open LAN Share from the Mac’s active desktop session.")
        }
    }
}
