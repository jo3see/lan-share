import AppKit
import Combine
import SwiftUI

@main
@MainActor
struct LanShareApp {
    static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.regular)
        withExtendedLifetime(delegate) { app.run() }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    private let model = ShareModel()
    private var window: NSWindow?
    private var windowSizing: AnyCancellable?

    func applicationDidFinishLaunching(_ notification: Notification) {
        let menu = NSMenu()
        let appItem = NSMenuItem()
        menu.addItem(appItem)
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "Quit LAN Share", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        NSApp.mainMenu = menu

        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 438, height: 375),
                              styleMask: [.titled, .closable, .miniaturizable, .fullSizeContentView],
                              backing: .buffered, defer: false)
        window.title = "LAN Share"
        window.titleVisibility = .hidden
        window.titlebarAppearsTransparent = true
        window.contentView = NSHostingView(rootView: ShareView(model: model))
        window.isReleasedWhenClosed = false
        window.isMovableByWindowBackground = true
        window.delegate = self
        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        self.window = window
        windowSizing = model.objectWillChange
            .receive(on: DispatchQueue.main)
            .sink { [weak self] in self?.fitWindow() }
        fitWindow()
    }

    private func fitWindow() {
        guard let window else { return }
        let height: CGFloat = model.error == nil ? 375 : 410
        let delta = height - (window.contentView?.frame.height ?? height)
        guard abs(delta) > 0.5 else { return }
        var frame = window.frame
        frame.origin.y -= delta
        frame.size.height += delta
        window.setFrame(frame, display: true)
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        model.shouldTerminate { [weak self] allowed in
            sender.reply(toApplicationShouldTerminate: allowed)
            if !allowed {
                self?.window?.makeKeyAndOrderFront(nil)
                sender.activate(ignoringOtherApps: true)
            }
        }
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        NSApp.terminate(nil)
        return false
    }
}

struct ShareView: View {
    @ObservedObject var model: ShareModel
    @Environment(\.colorScheme) private var colorScheme
    private let accent = Color(red: 0.26, green: 0.84, blue: 0.73)

    private var ink: Color { colorScheme == .dark ? accent : Color(red: 0.02, green: 0.40, blue: 0.34) }
    private var panel: Color { Color.primary.opacity(colorScheme == .dark ? 0.06 : 0.035) }
    private var outline: Color { Color.primary.opacity(colorScheme == .dark ? 0.10 : 0.08) }

    var body: some View {
        VStack(alignment: .leading, spacing: 15) {
            header
            VStack(spacing: 0) {
                portRow("Internet", icon: model.source?.kind == .wifi ? "wifi" : "globe",
                        selected: model.source, options: model.sourceOptions,
                        placeholder: model.isLoading ? "Detecting…" : "No connection",
                        alwaysShowsMenu: false, select: model.chooseSource)
                Divider().padding(.leading, 46)
                portRow("Share to", icon: model.destination?.kind == .wifi ? "wifi" : "cable.connector",
                        selected: model.destination,
                        options: model.destinationOptions, placeholder: "Connect an adapter",
                        select: model.chooseDestination)
            }
            .background(panel, in: RoundedRectangle(cornerRadius: 13))
            .overlay(RoundedRectangle(cornerRadius: 13).strokeBorder(outline))

            HStack(spacing: 10) {
                VStack(alignment: .leading, spacing: 4) {
                    Text(linkTitle).font(.system(size: 11, weight: .medium)).foregroundStyle(.secondary)
                    HStack(spacing: 6) {
                        Circle().fill(linkActive ? ink : Color.secondary.opacity(0.5)).frame(width: 6, height: 6)
                        Text(linkStatus)
                            .font(.system(size: 13, weight: .semibold))
                    }
                }
                Spacer()
                VStack(alignment: .trailing, spacing: 4) {
                    Text("Data transferred")
                        .font(.system(size: 11, weight: .medium)).foregroundStyle(.secondary)
                    Text(model.transferredBytes == 0 ? "0 B" :
                            ByteCountFormatter.string(fromByteCount: Int64(clamping: model.transferredBytes),
                                                      countStyle: .decimal))
                        .font(.system(size: 13, weight: .semibold, design: .monospaced))
                        .foregroundStyle(model.isSharing ? ink : .secondary)
                }
            }
            .padding(.horizontal, 12).padding(.vertical, 9)
            .background(panel, in: RoundedRectangle(cornerRadius: 11))

            actionButton
            footer
        }
        .padding(.top, 37).padding(.horizontal, 23).padding(.bottom, 14)
        .frame(minWidth: 438, maxWidth: 438, minHeight: 352, alignment: .topLeading)
        .background {
            ZStack(alignment: .topLeading) {
                Color(nsColor: .windowBackgroundColor)
                RadialGradient(colors: [accent.opacity(colorScheme == .dark ? 0.14 : 0.19), .clear],
                               center: .topLeading, startRadius: 15, endRadius: 270)
            }
            .ignoresSafeArea()
        }
    }

    private var header: some View {
        HStack(spacing: 12) {
            Image(systemName: "point.3.connected.trianglepath.dotted")
                .font(.system(size: 23, weight: .medium))
                .foregroundStyle(ink)
                .frame(width: 46, height: 46)
                .background(ink.opacity(0.12), in: RoundedRectangle(cornerRadius: 13))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text("LAN Share").font(.system(size: 24, weight: .semibold))
                    .tracking(-0.7).accessibilityAddTraits(.isHeader)
                Text("Internet sharing").font(.system(size: 12)).foregroundStyle(.secondary)
            }
            Spacer()
            HStack(spacing: 6) {
                Circle().fill(statusColor).frame(width: 6, height: 6)
                Text(statusText).font(.system(size: 11, weight: .semibold))
            }
            .foregroundStyle(statusColor)
            .padding(.horizontal, 9).padding(.vertical, 6)
            .background(statusColor.opacity(0.10), in: Capsule())
        }
    }

    private var linkTitle: String {
        switch model.destination?.kind {
        case .wifi: return "Wi-Fi hotspot"
        case .ethernet: return "Ethernet link"
        default: return "Sharing adapter"
        }
    }

    private var linkActive: Bool {
        model.destination?.kind == .wifi ? model.sharingEnabled == true : model.destinationHasLink
    }

    private var linkStatus: String {
        if model.destination?.kind == .wifi { return linkActive ? "Broadcasting" : "Off" }
        return linkActive ? "Connected" : "Disconnected"
    }

    private func portRow(_ label: String, icon: String, selected: NetworkPort?, options: [NetworkPort],
                         placeholder: String, alwaysShowsMenu: Bool = true,
                         select: @escaping (String) -> Void) -> some View {
        HStack(spacing: 10) {
            Image(systemName: icon).font(.system(size: 14, weight: .medium))
                .foregroundStyle(ink).frame(width: 22).accessibilityHidden(true)
            Text(label).font(.system(size: 13, weight: .medium))
            Spacer(minLength: 8)
            if alwaysShowsMenu || options.count > 1 {
                Menu {
                    ForEach(options) { port in
                        Button { select(port.id) } label: {
                            if port.id == selected?.id { Label(port.title, systemImage: "checkmark") }
                            else { Text(port.title) }
                        }
                    }
                } label: {
                    HStack(spacing: 7) {
                        Text(selected?.title ?? placeholder)
                            .font(.system(size: 12, weight: .medium)).lineLimit(1).truncationMode(.middle)
                        Image(systemName: "chevron.up.chevron.down").font(.system(size: 9, weight: .semibold))
                    }
                    .foregroundStyle(selected == nil ? Color.secondary : Color.primary)
                    .padding(.horizontal, 9).padding(.vertical, 6)
                    .background(Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 6))
                }
                .menuStyle(.borderlessButton).menuIndicator(.hidden)
                .fixedSize(horizontal: true, vertical: false).frame(maxWidth: 260, alignment: .trailing)
                .disabled(!model.canChoosePorts || options.isEmpty)
                .accessibilityLabel(label)
                .accessibilityValue(selected?.title ?? placeholder)
                .help(selected?.title ?? placeholder)
            } else {
                Text(selected?.name ?? placeholder)
                    .font(.system(size: 12, weight: .medium))
                    .foregroundStyle(selected == nil ? Color.secondary : Color.primary)
                    .lineLimit(1).truncationMode(.middle)
                    .frame(maxWidth: 260, alignment: .trailing)
                    .help(selected?.name ?? placeholder)
            }
        }
        .padding(.horizontal, 12).frame(height: 43)
    }

    private var actionButton: some View {
        Button {
            if model.phase == .ready { model.start() } else { model.stop() }
        } label: {
            HStack(spacing: 8) {
                if model.busy {
                    ProgressView().controlSize(.small).tint(.black)
                } else {
                    Image(systemName: actionIcon).font(.system(size: 12, weight: .semibold))
                }
                Text(actionTitle).font(.system(size: 13, weight: .semibold))
            }
            .frame(maxWidth: .infinity).frame(height: 38)
            .foregroundStyle(actionEnabled ? Color(red: 0.03, green: 0.20, blue: 0.17) : Color.secondary)
            .background(actionEnabled ? accent : panel, in: RoundedRectangle(cornerRadius: 9))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!actionEnabled)
        .keyboardShortcut(.defaultAction)
    }

    @ViewBuilder
    private var footer: some View {
        VStack(alignment: .leading, spacing: 4) {
            if let error = model.error {
                Text(error).foregroundStyle(Color(nsColor: .systemOrange))
            } else if model.phase == .restore {
                Text("Restore the settings saved before the last session.")
            } else if model.phase == .existing {
                Text("macOS was already sharing to this adapter.")
            } else if model.phase == .ready, !model.isLoading, model.source == nil {
                Text("Connect your Mac to the internet to get started.")
            } else if model.phase == .ready, !model.isLoading, model.destination == nil {
                Text("Connect another adapter to share your internet connection.")
            } else if model.phase == .ready, model.destination?.kind == .wifi {
                Text("Uses the Wi-Fi name and password saved in macOS Internet Sharing.")
            } else if model.isSharing, model.sharingEnabled == false {
                Text("Internet Sharing is no longer active for this adapter.")
            }
        }
        .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
    }

    private var statusText: String {
        switch model.phase {
        case .ready:
            if model.isLoading { return "Checking" }
            return model.canStart ? "Ready" : "Waiting"
        case .starting: return "Starting"
        case .sharing, .existing: return model.sharingEnabled == false ? "Sharing off" : "Sharing"
        case .stopping: return "Restoring"
        case .restore: return "Restore needed"
        }
    }

    private var statusColor: Color {
        if model.phase == .restore { return Color(nsColor: .systemOrange) }
        if model.isSharing, model.sharingEnabled != false { return ink }
        return .secondary
    }

    private var actionTitle: String {
        switch model.phase {
        case .ready: return "Start"
        case .starting: return "Starting…"
        case .sharing, .existing: return "Stop"
        case .stopping: return "Restoring…"
        case .restore: return "Restore settings"
        }
    }

    private var actionIcon: String {
        switch model.phase {
        case .sharing, .existing: return "stop.fill"
        case .restore: return "arrow.counterclockwise"
        default: return "play.fill"
        }
    }

    private var actionEnabled: Bool { model.canStart || model.isSharing || model.phase == .restore }

}
