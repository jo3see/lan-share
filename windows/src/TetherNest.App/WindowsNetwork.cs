using System.Collections;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TetherNest.Core;

namespace TetherNest;

public sealed record Adapter(Guid Id, string Name, string Description, bool Wifi, bool Ethernet,
    bool Physical, bool Connected, long Speed, bool Gateway, string Addresses)
{
    public string ConnectionClass { get; init; } = "Wi-Fi";
    public string? ChoiceLabel { get; init; }
    public override string ToString() => ChoiceLabel ?? (Wifi ? "Wi-Fi · " + (Connected ? "connected" : "not connected")
        : "Cable connection · " + (Connected ? "connected" : "plug in a cable"));
}
public static class NetworkInventory
{
    public static Adapter[] Read() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => Guid.TryParse(n.Id, out _))
        .Select(n => {
            var ip = n.GetIPProperties();
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Network\{4d36e972-e325-11ce-bfc1-08002be10318}\{" + Guid.Parse(n.Id) + @"}\Connection");
            string pnp = key?.GetValue("PnpInstanceID") as string ?? "";
            bool physical = pnp.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) ||
                            pnp.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase);
            return new Adapter(Guid.Parse(n.Id), n.Name, n.Description,
                n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                n.NetworkInterfaceType == NetworkInterfaceType.Ethernet, physical,
                n.OperationalStatus == OperationalStatus.Up, n.Speed,
                ip.GatewayAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                    a.Address.ToString() != "0.0.0.0"),
                string.Join(", ", ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()))) { ConnectionClass = pnp.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) ? "USB Wi-Fi" : "Built-in Wi-Fi" };
        }).ToArray();
    public static NetworkInterface Find(Guid id) => NetworkInterface.GetAllNetworkInterfaces()
        .FirstOrDefault(n => Guid.TryParse(n.Id, out var parsed) && parsed == id)
        ?? throw new InvalidOperationException("The cable adapter is missing. Reconnect it and retry.");
}
public sealed class WindowsNetwork : ISharingBackend
{
    private static object Manager() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.HNetShare", true)!)!;
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    private static T WithConnections<T>(Func<dynamic, IEnumerable, T> action)
    {
        object manager = Manager();
        object? connections = null;
        try {
            dynamic com = manager;
            if (!(bool)com.SharingInstalled) throw new InvalidOperationException("Internet Connection Sharing is unavailable on this Windows installation.");
            connections = com.EnumEveryConnection;
            return action(com, (IEnumerable)connections);
        } finally { Release(connections); Release(manager); }
    }
    public static Dictionary<Guid,int> Roles() => WithConnections((manager, connections) => {
        var roles = new Dictionary<Guid,int>();
        foreach (object connection in connections) {
            object? properties = null, configuration = null;
            try {
                properties = manager.NetConnectionProps(connection);
                configuration = manager.INetSharingConfigurationForINetConnection(connection);
                dynamic p = properties, c = configuration;
                if ((bool)c.SharingEnabled) roles.Add(Guid.Parse((string)p.Guid), (int)c.SharingConnectionType);
            } finally { Release(configuration); Release(properties); Release(connection); }
        }
        return roles;
    });
    public void ValidatePair(Guid source, Guid destination)
    {
        var adapters = NetworkInventory.Read();
        if (source == destination ||
            !adapters.Any(a => a.Id == source && AdapterChoices.IsUsableSource(a)) ||
            !adapters.Any(a => a.Id == destination && a.Physical && a.Ethernet && a.Connected))
            throw new InvalidOperationException("Connect this laptop to Wi-Fi and plug the cable into a separate Ethernet adapter and device.");
    }
    public Snapshot Read(Guid destination)
    {
        AddressSettings? settings = null;
        try {
            var adapter = NetworkInventory.Find(destination);
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{" + destination + "}");
            if (key == null) throw new InvalidOperationException("Windows IPv4 settings could not be read.");
            bool dhcp = Convert.ToInt32(key.GetValue("EnableDHCP", 0)) == 1;
            string dns = key.GetValue("NameServer") as string ?? "";
            string Value(string name) => key.GetValue(name) is string[] array ? string.Join(",", array) :
                Convert.ToString(key.GetValue(name)) ?? "";
            string fingerprint = dhcp ? "DHCP|" + dns :
                "STATIC|" + Value("IPAddress") + "|" + Value("SubnetMask") + "|" + Value("DefaultGateway") + "|" + dns;
            bool gateway = adapter.GetIPProperties().GatewayAddresses.Any(a =>
                a.Address.AddressFamily == AddressFamily.InterNetwork && a.Address.ToString() != "0.0.0.0");
            settings = new(dhcp, string.IsNullOrWhiteSpace(dns), fingerprint, gateway);
        } catch (InvalidOperationException) when (!NetworkInterface.GetAllNetworkInterfaces().Any(n => Guid.TryParse(n.Id, out var id) && id == destination)) { }
        return new(Roles(), settings);
    }
    private static void Change(Guid id, int? role)
    {
        bool found = WithConnections((manager, connections) => {
            foreach (object connection in connections) {
                object? properties = null, configuration = null;
                try {
                    properties = manager.NetConnectionProps(connection);
                    dynamic p = properties;
                    if (Guid.Parse((string)p.Guid) != id) continue;
                    configuration = manager.INetSharingConfigurationForINetConnection(connection);
                    dynamic c = configuration;
                    if (role.HasValue) c.EnableSharing(role.Value); else c.DisableSharing();
                    return true;
                } finally { Release(configuration); Release(properties); Release(connection); }
            }
            return false;
        });
        if (!found) throw new InvalidOperationException("Windows no longer lists the selected adapter.");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline) {
            var roles = Roles();
            if (role.HasValue ? roles.GetValueOrDefault(id, -1) == role.Value : !roles.ContainsKey(id)) return;
            Thread.Sleep(200);
        }
        throw new TimeoutException("Windows did not confirm the sharing change.");
    }
    public void Enable(Guid id, int role) => Change(id, role);
    public void Disable(Guid id) => Change(id, null);
    public void RestoreAutomatic(Guid destination)
    {
        int index = NetworkInventory.Find(destination).GetIPProperties().GetIPv4Properties().Index;
        AutomaticAddressRestoration.Restore(() => Read(destination).Destination,
            () => RunNetsh("interface", "ipv4", "set", "address", "name=" + index, "source=dhcp"),
            () => RunNetsh("interface", "ipv4", "set", "dnsservers", "name=" + index, "source=dhcp"));
    }
    private static void RunNetsh(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) {
            process.Kill();
            throw new TimeoutException("Windows address restoration timed out.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException("Windows could not restore automatic cable addressing: " + output.Result.Trim() + " " + error.Result.Trim());
    }
    public void VerifyStarted(Guid source, Guid destination)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline) {
            var snapshot = Read(destination);
            var adapter = NetworkInventory.Find(destination);
            bool privateAddress = adapter.GetIPProperties().UnicastAddresses.Any(a =>
                a.Address.AddressFamily == AddressFamily.InterNetwork &&
                !a.Address.ToString().StartsWith("169.254.") && a.Address.ToString() != "0.0.0.0");
            if (snapshot.Roles.GetValueOrDefault(source, -1) == 0 &&
                snapshot.Roles.GetValueOrDefault(destination, -1) == 1 && privateAddress &&
                snapshot.Destination is { Dhcp: false }) return;
            Thread.Sleep(250);
        }
        throw new InvalidOperationException("Windows did not finish configuring the cable connection. TetherNest will attempt restoration.");
    }
    public bool PairConnected(Guid source, Guid destination)
    {
        var adapters = NetworkInventory.Read();
        return adapters.Any(a => a.Id == source && a.Connected && a.Gateway) &&
               adapters.Any(a => a.Id == destination && a.Connected);
    }
}
