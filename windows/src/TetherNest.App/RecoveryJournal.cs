using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using TetherNest.Core;

namespace TetherNest;

public sealed class RecoveryJournal : IJournal
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TetherNest");
    public static string FilePath => Path.Combine(DirectoryPath, "sharing-v1.json");
    private readonly string directory;
    private string RecordPath => Path.Combine(directory, "sharing-v1.json");
    public RecoveryJournal(string? testDirectory = null) => directory = testDirectory ?? DirectoryPath;
    public bool Exists => File.Exists(RecordPath);
    public static void Prepare(string? testDirectory = null)
    {
        var folder = Directory.CreateDirectory(testDirectory ?? DirectoryPath);
        if ((folder.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The recovery folder must not be a filesystem link.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        folder.SetAccessControl(security);
    }
    public JournalRecord Load()
    {
        var info = new FileInfo(RecordPath);
        if (info.Length > 65536 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The recovery record cannot be safely read.");
        var acl = info.GetAccessControl();
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var owner = acl.GetOwner(typeof(SecurityIdentifier));
        if (!admins.Equals(owner) && !system.Equals(owner))
            throw new InvalidOperationException("The recovery record does not have a trusted owner.");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier))) {
            var writable = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writable) != 0 &&
                !admins.Equals(rule.IdentityReference) && !system.Equals(rule.IdentityReference))
                throw new InvalidOperationException("The recovery record is writable outside the administrator boundary.");
        }
        var record = JsonSerializer.Deserialize<JournalRecord>(File.ReadAllText(RecordPath))
            ?? throw new InvalidOperationException("The recovery record is empty.");
        if (record.Version != 1 || record.Source == Guid.Empty || record.Destination == Guid.Empty ||
            record.Source == record.Destination || record.Before == null || record.Expected == null ||
            record.Before.Roles.Count != 0 || record.Before.Destination is not { Dhcp:true, AutomaticDns:true, HasGateway:false } ||
            record.Expected.Roles.Any(p => !(p.Key == record.Source && p.Value == 0) && !(p.Key == record.Destination && p.Value == 1)))
            throw new InvalidOperationException("The recovery record is not a supported TetherNest session. No network settings were changed.");
        return record;
    }
    public void Save(JournalRecord record)
    {
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                JsonSerializer.Serialize(stream, record);
                stream.Flush(true);
            }
            var file = new FileInfo(temporary);
            var acl = file.GetAccessControl();
            acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            file.SetAccessControl(acl);
            File.Move(temporary, RecordPath, true);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Clear() => File.Delete(RecordPath);
}
