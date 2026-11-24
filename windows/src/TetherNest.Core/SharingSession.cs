namespace TetherNest.Core;

public sealed record AddressSettings(bool Dhcp, bool AutomaticDns, string Fingerprint, bool HasGateway);
public sealed record Snapshot(Dictionary<Guid, int> Roles, AddressSettings? Destination)
{
    public bool SameAs(Snapshot other) => Destination == other.Destination &&
        Roles.Count == other.Roles.Count && Roles.All(p => other.Roles.TryGetValue(p.Key, out var role) && role == p.Value);
}
public sealed record JournalRecord(int Version, Guid SessionId, Guid Source, Guid Destination,
    Snapshot Before, Snapshot Expected, bool MutationPending, string Phase);
public interface IJournal
{
    bool Exists { get; }
    JournalRecord Load();
    void Save(JournalRecord record);
    void Clear();
}
public interface ISharingBackend
{
    void ValidatePair(Guid source, Guid destination);
    Snapshot Read(Guid destination);
    void Enable(Guid id, int role);
    void Disable(Guid id);
    void RestoreAutomatic(Guid destination);
    void VerifyStarted(Guid source, Guid destination);
    bool PairConnected(Guid source, Guid destination);
}
public sealed class SharingSession(ISharingBackend backend, IJournal journal)
{
    public string State { get; private set; } = journal.Exists ? "restore" : "idle";
    public bool HasRecovery => journal.Exists;
    private Guid source, destination;
    public string Start(Guid from, Guid to)
    {
        if (State is "sharing" or "existing") throw new InvalidOperationException("This window already has a session.");
        if (journal.Exists) throw new InvalidOperationException("Restore the previous session before starting again.");
        backend.ValidatePair(from, to);
        var before = backend.Read(to);
        source = from; destination = to;
        if (before.Roles.Count == 2 && before.Roles.GetValueOrDefault(from, -1) == 0 &&
            before.Roles.GetValueOrDefault(to, -1) == 1)
            return State = "existing";
        if (before.Roles.Count != 0) throw new InvalidOperationException("Windows is already sharing another connection. Stop that sharing first.");
        if (before.Destination is not { Dhcp: true, AutomaticDns: true, HasGateway: false })
            throw new InvalidOperationException("The cable adapter needs automatic addressing and DNS, with no existing gateway. This version will not replace a custom setup.");
        journal.Save(new(1, Guid.NewGuid(), from, to, before, before, false, "prepared"));
        State = "starting";
        try
        {
            Step("enable-source", () => backend.Enable(from, 0));
            Step("enable-cable", () => backend.Enable(to, 1));
            backend.VerifyStarted(from, to);
            // Capture the settled address after Windows has finished configuring the private adapter.
            var settled = backend.Read(to);
            var record = journal.Load();
            RequireOwnedRoles(settled, record);
            journal.Save(record with { Expected = settled, Phase = "sharing" });
            return State = "sharing";
        }
        catch (Exception original)
        {
            try { Stop(); }
            catch (Exception recovery)
            {
                throw new InvalidOperationException(original.Message + " Recovery is still needed: " + recovery.Message, original);
            }
            throw;
        }
    }
    private void Step(string phase, Action action)
    {
        var record = journal.Load();
        var current = backend.Read(record.Destination);
        if (!current.SameAs(record.Expected))
            throw new InvalidOperationException("Network settings changed outside TetherNest. No further changes were made.");
        journal.Save(record with { MutationPending = true, Phase = phase });
        action();
        var observed = backend.Read(record.Destination);
        RequireOwnedRoles(observed, record);
        journal.Save(record with { Expected = observed, MutationPending = false, Phase = phase });
    }
    private static void RequireOwnedRoles(Snapshot snapshot, JournalRecord record)
    {
        if (snapshot.Roles.Any(p => !(p.Key == record.Source && p.Value == 0) &&
                                     !(p.Key == record.Destination && p.Value == 1)))
            throw new InvalidOperationException("Another sharing configuration appeared. TetherNest will not overwrite it.");
    }
    public string Stop()
    {
        if (!journal.Exists) return State = "idle"; // Includes observing somebody else's existing sharing.
        State = "restore";
        var record = journal.Load();
        var current = backend.Read(record.Destination);
        RequireOwnedRoles(current, record);
        if (Restored(current))
        {
            journal.Clear();
            return State = "idle";
        }
        if (current.Destination == null)
            throw new InvalidOperationException("Reconnect the same cable adapter, then choose Restore settings.");
        // An interrupted call has unknown side effects. Reconcile only if the observed result is
        // exactly the last persisted state; never guess an address configuration after a crash.
        if (!current.SameAs(record.Expected))
            throw new InvalidOperationException("Settings differ from the saved session. They were left untouched. See Details for recovery guidance.");
        if (record.MutationPending) journal.Save(record with { MutationPending = false });
        if (current.Roles.ContainsKey(record.Destination))
            Step("disable-cable", () => backend.Disable(record.Destination));
        current = backend.Read(record.Destination);
        if (current.Roles.ContainsKey(record.Source))
            Step("disable-source", () => backend.Disable(record.Source));
        current = backend.Read(record.Destination);
        if (!Restored(current))
            Step("restore-automatic-addressing", () => backend.RestoreAutomatic(record.Destination));
        if (!Restored(backend.Read(record.Destination)))
            throw new InvalidOperationException("Windows has not confirmed restoration. Keep the cable adapter attached and retry.");
        journal.Clear();
        return State = "idle";
    }
    private static bool Restored(Snapshot state) => state.Roles.Count == 0 &&
        state.Destination is { Dhcp: true, AutomaticDns: true, HasGateway: false };
    public string Check()
    {
        if (State == "sharing")
        {
            if (!backend.PairConnected(source, destination)) { Stop(); return State; }
            var record = journal.Load();
            if (!backend.Read(destination).SameAs(record.Expected))
            {
                State = "conflict";
                throw new InvalidOperationException("Network settings changed outside TetherNest. Sharing ownership needs review.");
            }
        }
        return State;
    }
}
