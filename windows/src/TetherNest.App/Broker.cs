using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using TetherNest.Core;
using Microsoft.Win32.SafeHandles;

namespace TetherNest;
public sealed record Request(string Command, Guid Source = default, Guid Destination = default);
public sealed record Reply(bool Ok, string State, string Message);
internal static class Wire
{
    public static async Task Write(Stream stream, object message)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(message);
        if (data.Length > 8192) throw new IOException("Message too large.");
        await stream.WriteAsync(BitConverter.GetBytes(data.Length));
        await stream.WriteAsync(data);
        await stream.FlushAsync();
    }
    public static async Task<T> Read<T>(Stream stream)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix);
        int length = BitConverter.ToInt32(prefix);
        if (length < 1 || length > 8192) throw new IOException("Invalid helper message.");
        byte[] data = new byte[length];
        await stream.ReadExactlyAsync(data);
        return JsonSerializer.Deserialize<T>(data) ?? throw new IOException("Empty helper message.");
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
public sealed class BrokerClient : IDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly Process helper;
    private readonly SemaphoreSlim gate = new(1);
    private BrokerClient(NamedPipeServerStream pipe, Process helper) { this.pipe = pipe; this.helper = helper; }
    public static async Task<BrokerClient> ConnectAsync(bool elevate = true, string? executable = null)
    {
        string name = "TetherNest-" + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 8192, 8192, security);
        Process? helper = null;
        try {
            var start = new ProcessStartInfo(executable ?? Environment.ProcessPath!) { UseShellExecute = elevate };
            if (elevate) start.Verb = "runas";
            start.ArgumentList.Add("--broker");
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            helper = Process.Start(start) ?? throw new IOException("The helper did not start.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await pipe.WaitForConnectionAsync(deadline.Token);
            if (!Wire.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint actual) || actual != helper.Id)
                throw new IOException("The helper identity could not be verified.");
            return new(pipe, helper);
        } catch { pipe.Dispose(); helper?.Dispose(); throw; }
    }
    public async Task<Reply> Send(Request request)
    {
        await gate.WaitAsync();
        try {
            await Wire.Write(pipe, request);
            return await Wire.Read<Reply>(pipe).WaitAsync(TimeSpan.FromSeconds(90));
        } finally { gate.Release(); }
    }
    public void Dispose() { pipe.Dispose(); helper.Dispose(); }
}
internal static class Broker
{
    public static void Run(string name, int parentId)
    {
        if (!name.StartsWith("TetherNest-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name["TetherNest-".Length..], "N", out _)) return;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return;
        using var parent = Process.GetProcessById(parentId);
        // A test runner may launch a read-only broker check; production peers must be this binary.
        string parentPath = parent.MainModule?.FileName ?? "";
        bool productionPeer = string.Equals(parentPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(15000);
        if (!Wire.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint actual) || actual != parentId) return;
        using var mutex = new Mutex(false, @"Global\TetherNest.Sharing.v1");
        bool acquired;
        try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) {
            _ = Wire.Read<Request>(pipe).GetAwaiter().GetResult();
            Wire.Write(pipe, new Reply(false, "restore", "Another TetherNest window owns the sharing helper. Use that window to stop it.")).GetAwaiter().GetResult();
            return;
        }
        var journal = new RecoveryJournal();
        var backend = new WindowsNetwork();
        var session = new SharingSession(backend, journal);
        string lastError = "";
        bool cleanupAuthorized = false;
        try {
            while (!parent.HasExited && pipe.IsConnected)
            {
                var receive = Wire.Read<Request>(pipe);
                while (!receive.Wait(1000)) {
                    if (parent.HasExited) return;
                    try { session.Check(); } catch (Exception e) { lastError = e.Message; }
                }
                var request = receive.GetAwaiter().GetResult();
                Reply reply;
                try {
                    if (!productionPeer && request.Command != "inspect")
                        throw new InvalidOperationException("This client is permitted only read-only inspection.");
                    switch (request.Command) {
                        case "inspect":
                            reply = new(true, session.State, "ICS available; " + WindowsNetwork.Roles().Count + " shared adapters.");
                            break;
                        case "start":
                            RecoveryJournal.Prepare();
                            cleanupAuthorized = !journal.Exists;
                            string started = session.Start(request.Source, request.Destination);
                            lastError = "";
                            reply = new(true, started, started == "existing" ? "Observing an existing connection; Stop will leave it running." :
                                "Sharing is configured. Open a website on the cabled device to verify internet access.");
                            break;
                        case "stop":
                        case "recover":
                            cleanupAuthorized = true;
                            if (journal.Exists) RecoveryJournal.Prepare();
                            session.Stop(); lastError = "";
                            reply = new(true, "idle", "Session stopped. Original automatic cable settings are restored.");
                            break;
                        case "status":
                            session.Check();
                            reply = new(lastError.Length == 0, session.State, lastError);
                            break;
                        default: throw new InvalidOperationException("Unsupported helper request.");
                    }
                } catch (Exception e) {
                    lastError = e.Message;
                    reply = new(false, journal.Exists ? "restore" : session.State, e.Message);
                }
                Wire.Write(pipe, reply).GetAwaiter().GetResult();
            }
        } catch (IOException) { }
          catch (AggregateException e) when (e.InnerException is IOException) { }
        finally {
            try { if (cleanupAuthorized && journal.Exists) session.Stop(); } catch { /* Journal remains for explicit recovery. */ }
            mutex.ReleaseMutex();
        }
    }
}
