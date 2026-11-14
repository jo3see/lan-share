import Darwin
import Foundation
import Security
import SystemConfiguration

// Internet Sharing loads the access point configuration from AirPort preferences
// and its passphrase from the System keychain. Keep the old keychain item intact.
struct HotspotPreferences {
    private let key = "InternetSharing" as CFString

    func requireSavedPassword(on device: String) throws {
        // Only look for Apple's keychain entry. macOS reads the passphrase when it starts the access point.
        guard try attributes(HotspotRestoreRecord.passwordService(for: device), keychain: systemKeychain()) != nil else {
            throw SharingError.message("Save a password for the Wi-Fi hotspot in System Settings → General → Sharing → Internet Sharing, then try again.")
        }
    }

    func restore(_ record: HotspotRestoreRecord) throws {
        let preferences = try open()
        let keychain = try systemKeychain()
        guard SCPreferencesLock(preferences, false) else { throw preferenceError() }
        defer { _ = SCPreferencesUnlock(preferences) }
        SCPreferencesSynchronize(preferences)

        let current = try read(preferences)
        let expected = try decode(record.applied)
        let previous = try decode(record.previous)
        let item = try attributes(record.service, keychain: keychain)
        if item?[kSecAttrComment as String] as? String == record.label {
            try check(SecItemDelete(query(record.service, keychain: keychain) as CFDictionary))
        }
        if try attributes(record.backupService, keychain: keychain) != nil {
            guard try attributes(record.service, keychain: keychain) == nil else {
                throw SharingError.message("The Mac’s hotspot password changed during sharing. LAN Share kept the saved password for recovery.")
            }
            try rename(record.backupService, to: record.service, keychain: keychain)
        }
        if equal(current, expected), !equal(current, previous) {
            try save(previous, in: preferences)
        } else if equal(current, previous), !SCPreferencesApplyChanges(preferences) {
            throw preferenceError()
        }
    }

    private func open() throws -> SCPreferences {
        guard geteuid() == 0,
              let preferences = SCPreferencesCreate(nil, "LAN Share" as CFString, "com.apple.airport.preferences.plist" as CFString) else {
            throw preferenceError("open")
        }
        return preferences
    }

    private func read(_ preferences: SCPreferences) throws -> [String: Any]? {
        guard let value = SCPreferencesGetValue(preferences, key) else {
            if SCError() == kSCStatusNoKey { return nil }
            throw preferenceError("read")
        }
        guard let dictionary = value as? [String: Any] else {
            throw SharingError.message("macOS returned an unrecognized Wi-Fi hotspot configuration.")
        }
        return dictionary
    }

    private func save(_ configuration: [String: Any]?, in preferences: SCPreferences) throws {
        let changed = configuration.map { SCPreferencesSetValue(preferences, key, $0 as CFDictionary) }
            ?? SCPreferencesRemoveValue(preferences, key)
        guard changed, SCPreferencesCommitChanges(preferences), SCPreferencesApplyChanges(preferences) else {
            throw preferenceError()
        }
    }

    private func decode(_ data: Data?) throws -> [String: Any]? {
        guard let data else { return nil }
        guard let values = try PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any] else {
            throw preferenceError()
        }
        return values
    }

    private func equal(_ lhs: [String: Any]?, _ rhs: [String: Any]?) -> Bool {
        guard let lhs, let rhs else { return lhs == nil && rhs == nil }
        return NSDictionary(dictionary: lhs).isEqual(to: rhs)
    }

    private func systemKeychain() throws -> SecKeychain {
        // The helper must fail instead of requesting authentication at runtime.
        try check(SecKeychainSetUserInteractionAllowed(false))
        var keychain: SecKeychain?
        try check(SecKeychainOpen("/Library/Keychains/System.keychain", &keychain))
        guard let keychain else { throw preferenceError() }
        return keychain
    }

    private func query(_ service: String, keychain: SecKeychain) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrAccount as String: "AirPort",
         kSecAttrService as String: service, kSecMatchSearchList as String: [keychain]]
    }

    private func attributes(_ service: String, keychain: SecKeychain) throws -> [String: Any]? {
        var request = query(service, keychain: keychain)
        request[kSecReturnAttributes as String] = true
        var result: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        try check(status)
        guard let attributes = result as? [String: Any] else { throw preferenceError() }
        return attributes
    }

    private func rename(_ service: String, to newService: String, keychain: SecKeychain) throws {
        let change = [kSecAttrService as String: newService]
        try check(SecItemUpdate(query(service, keychain: keychain) as CFDictionary, change as CFDictionary))
    }

    private func check(_ status: OSStatus) throws {
        guard status == errSecSuccess else {
            throw SharingError.message("macOS could not access the saved Wi-Fi hotspot password (\(status)).")
        }
    }

    private func preferenceError(_ operation: String = "update") -> SharingError {
        let status = SCError()
        if status == kSCStatusAccessError {
            return .message("This version of macOS blocks LAN Share from configuring a Wi-Fi hotspot. Choose a wired adapter to continue.")
        }
        return .message("macOS could not \(operation) its Wi-Fi hotspot settings (\(String(cString: SCErrorString(status)))).")
    }
}
