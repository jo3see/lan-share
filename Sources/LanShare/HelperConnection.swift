import Foundation

let sharingHelperID = "local.lanshare.macos.helper"

@objc(LANShareHelperProtocol) protocol SharingHelperProtocol {
    func start(_ sourceID: String, destination: String, reply: @escaping (Int, String?) -> Void)
    func stop(reply: @escaping (String?) -> Void)
    func recover(_ journal: Data, reply: @escaping (String?) -> Void)
    func status(reply: @escaping (Bool) -> Void)
}

final class HelperConnection: @unchecked Sendable {
    private var connection: NSXPCConnection?

    func start(source: NetworkPort, destination: NetworkPort) throws -> SharingSession.StartResult {
        try request(timeout: 24) { proxy, finish in
            proxy.start(source.id, destination: destination.device) { result, error in
                if let error { finish(.failure(SharingError.message(error))) }
                else { finish(.success(result == 1 ? .alreadySharing : .started)) }
            }
        }
    }

    func stop() throws {
        let _: Void = try request(timeout: 16) { proxy, finish in
            proxy.stop { error in
                if let error { finish(.failure(SharingError.message(error))) }
                else { finish(.success(())) }
            }
        }
    }

    func recover(_ journal: Data) throws {
        let _: Void = try request(timeout: 16) { proxy, finish in
            proxy.recover(journal) { error in
                if let error { finish(.failure(SharingError.message(error))) }
                else { finish(.success(())) }
            }
        }
    }

    func needsRestoration() -> Bool {
        (try? request(timeout: 2) { proxy, finish in proxy.status { finish(.success($0)) } }) ?? false
    }

    private func request<T>(timeout: TimeInterval,
                            _ send: (SharingHelperProtocol, @escaping (Result<T, Error>) -> Void) -> Void) throws -> T {
        if connection == nil {
            guard let url = Bundle.main.url(forResource: "SharingHelper", withExtension: "requirement"),
                  let requirement = try? String(contentsOf: url, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines),
                  !requirement.isEmpty else { throw unavailable }
            let newConnection = NSXPCConnection(machServiceName: sharingHelperID, options: .privileged)
            newConnection.remoteObjectInterface = NSXPCInterface(with: SharingHelperProtocol.self)
            newConnection.setCodeSigningRequirement(requirement)
            newConnection.resume()
            connection = newConnection
        }
        guard let connection else { throw unavailable }
        let reply = HelperReply<T>()
        guard let proxy = connection.remoteObjectProxyWithErrorHandler({ error in
            reply.finish(.failure(error))
        }) as? SharingHelperProtocol else { throw unavailable }
        send(proxy, reply.finish)
        let result = reply.wait(timeout: timeout)
        if let result {
            do { return try result.get() }
            catch let error as SharingError { throw error }
            catch { self.connection = nil; connection.invalidate(); throw unavailable }
        }
        self.connection = nil
        connection.invalidate()
        throw SharingError.message("The LAN Share helper took too long to respond. Try again.")
    }

    private var unavailable: SharingError {
        .message("The LAN Share helper is unavailable. Install LAN Share using its installer package.")
    }
}

private final class HelperReply<Value>: @unchecked Sendable {
    private let lock = NSLock()
    private let ready = DispatchSemaphore(value: 0)
    private var result: Result<Value, Error>?

    func finish(_ result: Result<Value, Error>) {
        lock.lock()
        defer { lock.unlock() }
        guard self.result == nil else { return }
        self.result = result
        ready.signal()
    }

    func wait(timeout: TimeInterval) -> Result<Value, Error>? {
        guard ready.wait(timeout: .now() + timeout) == .success else { return nil }
        lock.lock()
        defer { lock.unlock() }
        return result
    }
}
