using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using TetherNest.Core;
namespace TetherNest;
public sealed record PhoneSettings(Guid InterfaceId,string Profile)
{
    public static string DirectoryPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TetherNest");
    static string FilePath=>Path.Combine(DirectoryPath,"iphone.json");
    public static PhoneSettings? Load(){try{return JsonSerializer.Deserialize<PhoneSettings>(File.ReadAllText(FilePath));}catch{return null;}}
    public void Save(){Directory.CreateDirectory(DirectoryPath);File.WriteAllText(FilePath,JsonSerializer.Serialize(this));}
}
public sealed class PhoneBackend(PhoneSettings settings):IPhoneConnectionBackend
{
    public static string RadioJournal=>Path.Combine(PhoneSettings.DirectoryPath,"bluetooth-restore.json");
    public string? CurrentProfile(){using var wifi=new WifiNative();return wifi.Current(settings.InterfaceId);}
    static async Task Set(Radio radio,RadioState state){if(await radio.SetStateAsync(state)!=RadioAccessStatus.Allowed)throw new InvalidOperationException("Windows refused the Bluetooth change. Check Bluetooth permissions in Settings.");
        for(int i=0;i<30&&radio.State!=state;i++)await Task.Delay(100);
        if(radio.State!=state)throw new InvalidOperationException("Windows did not confirm Bluetooth "+state+".");}
    public static async Task RecoverBluetooth()
    {
        if(!File.Exists(RadioJournal))return;
        if(await Radio.RequestAccessAsync()!=RadioAccessStatus.Allowed)throw new InvalidOperationException("Allow Bluetooth control to restore the radio.");
        if(new FileInfo(RadioJournal).Length>65536)throw new InvalidOperationException("Invalid Bluetooth recovery record.");
        var ids=JsonSerializer.Deserialize<string[]>(File.ReadAllText(RadioJournal))??throw new InvalidOperationException("Invalid Bluetooth recovery record.");
        if(ids.Length>16)throw new InvalidOperationException("Invalid Bluetooth recovery record.");
        foreach(var id in ids){var radio=await Radio.FromIdAsync(id);if(radio==null||radio.Kind!=RadioKind.Bluetooth)throw new InvalidOperationException("Reconnect the Bluetooth adapter before restoring it.");if(radio.State!=RadioState.On)await Set(radio,RadioState.On);}
        File.Delete(RadioJournal);
    }
    public async Task CycleBluetooth(CancellationToken token)
    {
        if(File.Exists(RadioJournal))throw new InvalidOperationException("Restore Bluetooth before trying a new connection.");
        if(await Radio.RequestAccessAsync()!=RadioAccessStatus.Allowed)throw new InvalidOperationException("Windows did not allow Bluetooth control. Enable permission in Windows Settings.");
        var devices=await DeviceInformation.FindAllAsync(Radio.GetDeviceSelector());
        var radios=new List<(string Id,Radio Radio)>();
        foreach(var device in devices){var radio=await Radio.FromIdAsync(device.Id);if(radio?.Kind==RadioKind.Bluetooth&&radio.State==RadioState.On)radios.Add((device.Id,radio));}
        if(radios.Count==0)throw new InvalidOperationException("Turn Bluetooth on in Windows, then retry.");
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(PhoneSettings.DirectoryPath);
        // Durable intent before any radio is turned off. Recovery is explicit after a crash.
        using(var file=new FileStream(RadioJournal,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){
            JsonSerializer.Serialize(file,radios.Select(r=>r.Id).ToArray());file.Flush(true);}
        try {
            foreach(var radio in radios){token.ThrowIfCancellationRequested();await Set(radio.Radio,RadioState.Off);}
            await Task.Delay(TimeSpan.FromSeconds(5),token);
        } finally {
            var failures=new List<Exception>();
            foreach(var radio in radios)try {await Set(radio.Radio,RadioState.On);}catch(Exception e){failures.Add(e);}
            if(failures.Count>0)throw new InvalidOperationException("Bluetooth needs restoration. Use Restore Bluetooth, or turn it on in Windows Settings.",failures[0]);
            File.Delete(RadioJournal);
        }
        // Give the existing phone connection and its three-second automation time to run.
        await Task.Delay(TimeSpan.FromSeconds(10),token);
    }
    public async Task Join(string profile,CancellationToken token)
    {
        using var wifi=new WifiNative();
        if(!wifi.Profiles().Any(p=>p.InterfaceId==settings.InterfaceId&&p.Name==profile))throw new InvalidOperationException("This Wi-Fi profile is no longer saved. Connect once in Windows Settings, then choose it in iPhone setup.");
        var original=wifi.Current(settings.InterfaceId);
        for(int attempt=0;attempt<4;attempt++){
            token.ThrowIfCancellationRequested();
            var selected=wifi.Current(settings.InterfaceId);
            if(selected!=null&&selected!=original&&selected!=profile)throw new InvalidOperationException("Wi-Fi changed outside TetherNest; your selection was left alone.");
            wifi.Connect(new(settings.InterfaceId,profile));
            for(int i=0;i<20;i++){await Task.Delay(500,token);if(wifi.Current(settings.InterfaceId)==profile)return;}
        }
        throw new InvalidOperationException("The iPhone hotspot did not connect. Check the phone is nearby and the imported Bluetooth trigger is enabled.");
    }
    public async Task<bool> VerifyInternet(CancellationToken token)
    {
        // Bind HTTPS sockets to this Wi-Fi interface's address. Do not certify a VPN/other adapter's internet.
        var adapter=NetworkInventory.Find(settings.InterfaceId);
        var local=adapter.GetIPProperties().UnicastAddresses.FirstOrDefault(a=>a.Address.AddressFamily==AddressFamily.InterNetwork&&!a.Address.ToString().StartsWith("169.254."))?.Address;
        if(local==null||CurrentProfile()!=settings.Profile)return false;
        using var handler=new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,ConnectCallback=async(context,ct)=>{
            var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
            try{socket.SetSocketOption(SocketOptionLevel.IP,(SocketOptionName)31 /* Windows IP_UNICAST_IF */,IPAddress.HostToNetworkOrder(adapter.GetIPProperties().GetIPv4Properties().Index));socket.Bind(new IPEndPoint(local,0));await socket.ConnectAsync(context.DnsEndPoint,ct);return new NetworkStream(socket,true);}catch{socket.Dispose();throw;}
        }};
        using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(10)};
        try{using var response=await client.GetAsync("https://www.gstatic.com/generate_204",token);
            return response.StatusCode==HttpStatusCode.NoContent&&CurrentProfile()==settings.Profile;
        }catch(OperationCanceledException)when(!token.IsCancellationRequested){return false;}catch(HttpRequestException){return false;}
    }
}
