import AppKit
import Combine
import Foundation

@MainActor
final class ShareModel: ObservableObject {
    enum Phase: Equatable {
        case ready, starting, sharing, existing, stopping, restore
    }

    @Published private(set) var phase: Phase = .ready
    @Published private(set) var network = NetworkSnapshot(ports: [], primaryID: nil)
    @Published private(set) var sourceID: String?
    @Published private(set) var destinationID: String?
    @Published private(set) var transferredBytes: UInt64 = 0
    @Published private(set) var error: String?
    @Published private(set) var sharingEnabled: Bool?
    @Published private(set) var isLoading = true

    private let controls = SharingPreferences()
    private let helper = HelperConnection()
    private let journal = URL.applicationSupportDirectory
        .appending(path: "LAN Share", directoryHint: .isDirectory)
        .appending(path: "restore.json")
    private let sharingQueue = DispatchQueue(label: "LanShare.Sharing", qos: .userInitiated)
    private var networkTask: Task<Void, Never>?
    private var traffic = SessionTraffic()
    private var trafficSession: UUID?
    private var manualSource: String?
    private var manualDestination: String?
    private var currentPair: (source: NetworkPort, destination: NetworkPort)?
    private var quitReply: ((Bool) -> Void)?

    init() {
        if FileManager.default.fileExists(atPath: journal.path) { phase = .restore }
        let helper = self.helper
        sharingQueue.async {
            let pending = helper.needsRestoration()
            DispatchQueue.main.async {
                if pending, self.phase == .ready { self.phase = .restore }
            }
        }
        networkTask = Task { [weak self] in
            while !Task.isCancelled {
                let snapshot = await Task.detached(priority: .utility) { NetworkSnapshot.read() }.value
                self?.refresh(snapshot)
                try? await Task.sleep(for: .seconds(2))
            }
        }
    }

    var source: NetworkPort? {
        network.ports.first { $0.id == sourceID } ?? currentPair?.source
    }

    var destination: NetworkPort? {
        network.ports.first { $0.id == destinationID } ?? currentPair?.destination
    }

    var sourceOptions: [NetworkPort] {
        var options = network.ports.filter(\.canProvideInternet).sorted { lhs, rhs in
            if lhs.id == network.primaryID { return rhs.id != network.primaryID }
            if rhs.id == network.primaryID { return false }
            return lhs.title.localizedStandardCompare(rhs.title) == .orderedAscending
        }
        if let currentPair, !options.contains(where: { $0.id == currentPair.source.id }) {
            options.insert(currentPair.source, at: 0)
        }
        return options
    }

    var destinationOptions: [NetworkPort] {
        var options = network.destinations(excluding: source)
        if let currentPair, !options.contains(where: { $0.id == currentPair.destination.id }) {
            options.insert(currentPair.destination, at: 0)
        }
        return options
    }

    var busy: Bool { phase == .starting || phase == .stopping }
    var canStart: Bool {
        phase == .ready && source?.canProvideInternet == true && destination != nil
    }
    var canChoosePorts: Bool { phase == .ready }
    var isSharing: Bool { phase == .sharing || phase == .existing }
    var destinationHasLink: Bool { network.ports.first { $0.id == destinationID }?.hasLink == true }

    func chooseSource(_ id: String) {
        guard canChoosePorts else { return }
        manualSource = id
        error = nil
        refreshSelections()
    }

    func chooseDestination(_ id: String) {
        guard canChoosePorts else { return }
        manualDestination = id
        error = nil
        refreshSelections()
    }

    func start() {
        guard canStart, let source, let destination else { return }
        error = nil
        phase = .starting
        currentPair = (source, destination)
        traffic = SessionTraffic()
        transferredBytes = 0
        trafficSession = UUID()
        let helper = self.helper
        let controls = self.controls
        sharingQueue.async {
            let result = Result { try helper.start(source: source, destination: destination) }
            let sample = controls.sharingInterface(to: destination.device, wifi: destination.kind == .wifi)
                .flatMap { InterfaceTraffic.read(device: $0) }
            let needsRestoration = helper.needsRestoration()
            DispatchQueue.main.async {
                if case .success = result {
                    self.updateTraffic(sample)
                }
                self.finishStart(result, needsRestoration: needsRestoration)
            }
        }
    }

    func stop() {
        guard !busy else { return }
        if phase == .existing {
            phase = .stopping
            let destination = currentPair?.destination
            let controls = self.controls
            sharingQueue.async {
                let lastSample = destination
                    .flatMap { controls.sharingInterface(to: $0.device, wifi: false) }
                    .flatMap { InterfaceTraffic.read(device: $0) }
                DispatchQueue.main.async {
                    self.updateTraffic(lastSample)
                    self.finishStop(.success(()))
                }
            }
            return
        }
        guard phase == .sharing || phase == .restore else { return }
        error = nil
        phase = .stopping
        let helper = self.helper
        let journal = self.journal
        let destination = currentPair?.destination
        let controls = self.controls
        sharingQueue.async {
            let lastSample = destination
                .flatMap { controls.sharingInterface(to: $0.device, wifi: $0.kind == .wifi) }
                .flatMap { InterfaceTraffic.read(device: $0) }
            let result = Result {
                if FileManager.default.fileExists(atPath: journal.path) {
                    try helper.recover(Data(contentsOf: journal))
                    try FileManager.default.removeItem(at: journal)
                } else {
                    try helper.stop()
                }
            }
            DispatchQueue.main.async {
                self.updateTraffic(lastSample)
                self.finishStop(result)
            }
        }
    }

    func shouldTerminate(_ reply: @escaping (Bool) -> Void) -> NSApplication.TerminateReply {
        guard phase == .starting || phase == .sharing || phase == .stopping || phase == .restore else {
            return .terminateNow
        }
        quitReply = reply
        if !busy {
            // Defer the reply until AppKit has received .terminateLater.
            Task { stop() }
        }
        return .terminateLater
    }

    private func finishStart(_ result: Result<SharingSession.StartResult, Error>, needsRestoration: Bool) {
        switch result {
        case .success(let state):
            phase = state == .started ? .sharing : .existing
            sharingEnabled = true
        case .failure(let failure):
            reset(to: needsRestoration ? .restore : .ready)
            error = failure.localizedDescription
        }
        guard quitReply != nil else { return }
        if phase == .sharing { stop() } else { finishQuit(phase != .restore) }
    }

    private func finishStop(_ result: Result<Void, Error>) {
        switch result {
        case .success:
            reset()
            finishQuit(true)
        case .failure(let failure):
            reset(to: .restore)
            error = failure.localizedDescription
            finishQuit(false)
        }
    }

    private func finishQuit(_ allowed: Bool) {
        let reply = quitReply
        quitReply = nil
        reply?(allowed)
    }

    private func reset(to newPhase: Phase = .ready) {
        phase = newPhase
        sharingEnabled = nil
        currentPair = nil
        trafficSession = nil
        error = nil
        refreshSelections()
    }

    private func refresh(_ snapshot: NetworkSnapshot) {
        network = snapshot
        isLoading = false
        refreshSelections()
        guard isSharing, let currentPair, let trafficSession else { return }
        let controls = self.controls
        sharingQueue.async {
            let destination = currentPair.destination
            let selected = (try? controls.read().matches(sourceID: currentPair.source.id,
                                                         destination: destination.device)) == true
            let sharingInterface = selected
                ? controls.sharingInterface(to: destination.device, wifi: destination.kind == .wifi) : nil
            let sample = sharingInterface.flatMap { InterfaceTraffic.read(device: $0) }
            DispatchQueue.main.async {
                guard self.isSharing, self.trafficSession == trafficSession else { return }
                self.sharingEnabled = sharingInterface != nil
                self.updateTraffic(sample)
            }
        }
    }

    private func updateTraffic(_ sample: InterfaceTraffic?) {
        traffic.update(sample)
        transferredBytes = traffic.bytes
    }

    private func refreshSelections() {
        guard canChoosePorts else { return }
        let nextSource = sourceOptions.first { $0.id == manualSource }?.id
            ?? sourceOptions.first?.id
        sourceID = nextSource
        destinationID = destinationOptions.first { $0.id == manualDestination }?.id
            ?? destinationOptions.first?.id
    }
}
