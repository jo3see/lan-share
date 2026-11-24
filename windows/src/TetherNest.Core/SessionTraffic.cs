namespace TetherNest.Core;
public sealed class SessionTraffic
{
    private (Guid Id, long Received, long Sent)? previous;
    public long Received { get; private set; }
    public long Sent { get; private set; }
    public void Update(Guid id, long received, long sent)
    {
        if (previous is { } p && p.Id == id) {
            if (received >= p.Received) Received += received - p.Received;
            if (sent >= p.Sent) Sent += sent - p.Sent;
        }
        previous = (id, received, sent);
    }
    public void Gap() => previous = null;
}
