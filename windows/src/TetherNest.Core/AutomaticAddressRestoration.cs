namespace TetherNest.Core;

// ICS can restore DHCP itself while leaving DNS to be restored by the caller.
public static class AutomaticAddressRestoration
{
    public static void Restore(Func<AddressSettings?> read, Action restoreAddress, Action restoreDns)
    {
        AddressSettings Read() => read() ?? throw new InvalidOperationException("The cable adapter is missing. Reconnect it and retry.");
        if (!Read().Dhcp) restoreAddress();
        // Re-read: Windows can change both settings when DHCP is enabled.
        if (!Read().AutomaticDns) restoreDns();
    }
}
