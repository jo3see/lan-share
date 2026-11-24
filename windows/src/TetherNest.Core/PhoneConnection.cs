namespace TetherNest.Core;
public interface IPhoneConnectionBackend
{
    string? CurrentProfile();
    Task CycleBluetooth(CancellationToken token);
    Task Join(string profile, CancellationToken token);
    Task<bool> VerifyInternet(CancellationToken token);
}
public static class PhoneConnection
{
    public static async Task Connect(IPhoneConnectionBackend backend, string target,
        Action<string> report, CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(target))throw new InvalidOperationException("Choose the saved iPhone hotspot first.");
        token.ThrowIfCancellationRequested();
        var original=backend.CurrentProfile();
        if(original==target && await backend.VerifyInternet(token)) {report("iPhone connected · internet verified");return;}
        bool joining=false;
        try {
            report("Restarting Bluetooth · other Bluetooth devices briefly disconnect");
            await backend.CycleBluetooth(token);
            token.ThrowIfCancellationRequested();
            var afterCycle=backend.CurrentProfile();
            if(afterCycle!=original && !(original==target&&afterCycle==null))
                throw new InvalidOperationException("Wi-Fi changed during connection. Your new connection was left alone.");
            report("Joining iPhone · waiting for the hotspot");
            joining=true;
            await backend.Join(target,token);
            report("Checking internet through iPhone");
            if(!await backend.VerifyInternet(token))throw new InvalidOperationException("Joined the iPhone, but internet could not be verified.");
            report("iPhone connected · internet verified");
        } catch(Exception failure) {
            // Restore only our target/disconnected state, never a different external selection.
            var current=backend.CurrentProfile();
            if(joining && original!=null && original!=target && (current==null||current==target)) {
                report("Connection failed · restoring previous Wi-Fi");
                try {await backend.Join(original,CancellationToken.None);}
                catch(Exception restore){throw new InvalidOperationException(failure.Message+" Previous Wi-Fi could not be restored: "+restore.Message,failure);}
            }
            throw;
        }
    }
}
