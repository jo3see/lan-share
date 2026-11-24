using System.ComponentModel;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using TetherNest.Core;

namespace TetherNest;
public sealed class MainForm : Form
{
    private readonly Button connectPhone = new RoundedButton();

    private CancellationTokenSource? phoneCancellation;
    private PhoneSettings? phoneSettings = PhoneSettings.Load();
    private string phoneProgress = "";
    private readonly Dictionary<Guid,string?> wifiProfiles = new();
    private readonly ComboBox source = new(), cable = new();
    private readonly Label status = new(), latency = new(), trafficLabel = new(), detail = new();
    private readonly Button start = new RoundedButton(), stop = new RoundedButton(), diagnose = new RoundedButton();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 3000 };
    private BrokerClient? broker;
    private CancellationTokenSource? probe;
    private SessionTraffic traffic = new();
    private Guid? sessionDestination;
    private Adapter[] adapters = [];
    private string state = "idle", lastMessage = "";
    private bool busy, refreshing, closing, sharedProbe;
    private readonly Color muted = Color.FromArgb(158,174,192);
    public bool IsIdle => state == "idle" && phoneCancellation == null;
    public bool SharingReady => start.Enabled;
    public int SourceChoiceCount => source.Items.Count;
    public string StatusText => status.Text;

    public MainForm()
    {
        SuspendLayout();
        Text = "TetherNest";
        BackColor = Color.FromArgb(18,25,36);
        ForeColor = Color.FromArgb(235,241,247);
        Font = new Font("Segoe UI", 10);
        AutoScaleDimensions = new SizeF(96,96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(390, 680);
        MinimumSize = new Size(390, 590);
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        Controls.Add(scroll);
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            ColumnCount = 1, Padding = new Padding(20), BackColor = BackColor };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        scroll.Controls.Add(layout);
        void Add(Control control) { control.Margin = new Padding(0,0,0,10); layout.Controls.Add(control); }
        Label TextLabel(string text, float size = 10) => new() { Text = text, AutoSize = true,
            Font = new Font("Segoe UI",size), ForeColor = muted, BackColor = Color.Transparent };
        var title = TextLabel("TetherNest", 20);
        title.ForeColor = ForeColor; title.Font = new Font("Segoe UI Semibold",20);
        Add(title);
        Add(TextLabel("CONNECT  /  SHARE  /  DIAGNOSE",8));
        Add(TextLabel("Internet from"));
        ConfigurePicker(source); Add(source);

        Style(connectPhone,"Connect to iPhone",Color.FromArgb(28,105,182));Add(connectPhone);
        Add(TextLabel("Share with a device connected by cable"));
        ConfigurePicker(cable); Add(cable);

        var card = new RoundedCard { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14), BackColor = Color.FromArgb(25,35,49) };
        var metrics = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, BackColor = card.BackColor };
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); card.Controls.Add(metrics);
        status.AutoSize = true; status.Margin = new Padding(0,0,0,8);
        status.ForeColor = Color.FromArgb(103,220,170);
        metrics.Controls.Add(status);
        metrics.SizeChanged += (_,_) => status.MaximumSize = new Size(Math.Max(100,metrics.ClientSize.Width),0);
        latency.AutoSize = true; latency.Font = new Font("Segoe UI Semibold",28); latency.Text = "—";
        latency.Margin = Padding.Empty; metrics.Controls.Add(latency);
        var caption = TextLabel("Laptop path ping · 1.1.1.1",8);
        caption.Margin = new Padding(0,0,0,10); metrics.Controls.Add(caption);
        trafficLabel.AutoSize = true; trafficLabel.Font = new Font("Segoe UI",9);
        trafficLabel.ForeColor = muted; trafficLabel.Text = "Cable traffic this session: —";
        metrics.Controls.Add(trafficLabel);
        Add(card);
        var actions = new TableLayoutPanel { Dock = DockStyle.Top, Height = 44, ColumnCount = 2 };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
        Style(start,"Start sharing",Color.FromArgb(28,105,182));
        Style(stop,"Stop sharing",Color.FromArgb(55,68,84));
        start.Margin = new Padding(0,0,5,0); stop.Margin = new Padding(5,0,0,0);
        actions.Controls.Add(start,0,0); actions.Controls.Add(stop,1,0); Add(actions);
        Style(diagnose,"Resume ping",Color.FromArgb(39,57,77)); diagnose.Height = 36; Add(diagnose);
        var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
        var pin = new CheckBox { Text = "Keep on top", AutoSize = true, Margin = new Padding(0,4,16,0) };
        pin.CheckedChanged += (_,_) => TopMost = pin.Checked;
        var details = new LinkLabel { Text = "Details", AutoSize = true, LinkColor = Color.FromArgb(120,185,245), Margin = new Padding(0,5,0,0) };
        details.LinkClicked += (_,_) => ShowDetails();
        var phoneSetup=new LinkLabel{Text="iPhone setup",AutoSize=true,LinkColor=Color.FromArgb(120,185,245),Margin=new Padding(12,5,0,0)};
        phoneSetup.LinkClicked+=(_,_)=>{if(!busy&&state=="idle")ConfigurePhone();};
        bottom.Controls.Add(pin); bottom.Controls.Add(details);bottom.Controls.Add(phoneSetup); Add(bottom);
        detail.AutoSize = true; detail.ForeColor = muted; detail.Font = new Font("Segoe UI",8);
        detail.Text = "Ping runs independently. Connecting and sharing stay manual.";
        Add(detail);
        layout.SizeChanged += (_,_) => detail.MaximumSize = new Size(Math.Max(100,layout.ClientSize.Width-layout.Padding.Horizontal),0);
        source.SelectedIndexChanged += (_,_) => UpdateControls();
        cable.SelectedIndexChanged += (_,_) => UpdateControls();
        connectPhone.Click += async (_,_) => await ConnectPhone();
        start.Click += async (_,_) => await StartSharing();
        stop.Click += async (_,_) => await StopSharing();
        diagnose.Click += (_,_) => { if (probe == null) StartProbe(); else { sharedProbe = false; StopProbe(); } };
        Shown += async (_,_) => { await RefreshStatus(); StartProbe(); timer.Start(); };
        timer.Tick += async (_,_) => await RefreshStatus();
        FormClosing += OnClosing;
        SystemEvents.PowerModeChanged += OnPower;
        UpdateControls();
        ResumeLayout(true);
    }
    private static void ConfigurePicker(ComboBox box)
    {
        box.DropDownStyle = ComboBoxStyle.DropDownList; box.Dock = DockStyle.Top; box.FlatStyle = FlatStyle.Flat;
        box.BackColor = Color.FromArgb(31,43,59); box.ForeColor = Color.White;
        box.IntegralHeight = false; box.DropDownHeight = 160;
        box.DrawMode = DrawMode.OwnerDrawFixed;
        box.DrawItem += (_, e) => {
            if (e.Index < 0 || e.Index >= box.Items.Count || e.Graphics == null) return;
            using var background = new SolidBrush(box.BackColor);
            e.Graphics.FillRectangle(background,e.Bounds);
            TextRenderer.DrawText(e.Graphics,box.Items[e.Index]?.ToString() ?? "",box.Font,e.Bounds,box.ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            e.DrawFocusRectangle();
        };
    }
    private static void Style(Button b,string text,Color color)
    {
        b.Text=text; b.BackColor=color; b.ForeColor=Color.White; b.Dock=DockStyle.Top;
        b.Height=44; b.Cursor=Cursors.Hand; b.AccessibleName=text;
    }
    private async Task RefreshStatus()
    {
        if (busy || refreshing || closing) return;
        refreshing = true;
        try {
            adapters = await Task.Run(NetworkInventory.Read);
            wifiProfiles.Clear();
            try {using var wifi=new WifiNative();foreach(var id in wifi.Interfaces())wifiProfiles[id]=wifi.Current(id);}catch(Exception e) when(e is Win32Exception or InvalidOperationException){ /* Friendly fallback labels; connection setup reports access errors. */ }
            if (state == "idle") {
                AdapterChoices.RefreshPicker(source,AdapterChoices.Sources(adapters).Select(a=>a with {
                    ChoiceLabel=wifiProfiles.GetValueOrDefault(a.Id) is string profile
                        ? (phoneSettings?.InterfaceId==a.Id&&phoneSettings.Profile==profile?"iPhone · ":"Wi-Fi · ")+profile : a.ChoiceLabel
                }).ToArray());
                AdapterChoices.RefreshPicker(cable,adapters.Where(a=>a.Ethernet && a.Physical).GroupBy(a=>a.Id).Select(g=>g.First()).OrderByDescending(a=>a.Connected).ThenBy(a=>a.Id).ToArray());
            }
            SampleTraffic();
            if (broker != null) {
                var reply=await broker.Send(new("status"));
                if (state != reply.State) state=reply.State;
                if (!reply.Ok) lastMessage=reply.Message;
                if (state=="idle" && sessionDestination != null) {
                    sessionDestination=null;
                    lastMessage="Sharing stopped after the connection changed; settings restored.";
                    if(sharedProbe) StopProbe();
                }
            }
            UpdateControls();
        } catch (Exception e) { lastMessage=Friendly(e); UpdateControls(); }
        finally { refreshing=false; UpdateControls(); }
    }
    private void UpdateControls()
    {
        bool radioPending=File.Exists(PhoneBackend.RadioJournal);
        bool phoneConnected=phoneSettings!=null&&wifiProfiles.GetValueOrDefault(phoneSettings.InterfaceId)==phoneSettings.Profile;
        connectPhone.Text=phoneCancellation!=null?"Cancel connection":radioPending?"Restore Bluetooth":phoneConnected?"Connected to iPhone":"Connect to iPhone";
        connectPhone.AccessibleName=connectPhone.Text;
        if(!phoneConnected&&lastMessage.StartsWith("iPhone connected",StringComparison.Ordinal))lastMessage="";
        connectPhone.Enabled=phoneCancellation!=null||(!busy&&!refreshing&&state=="idle"&&!File.Exists(RecoveryJournal.FilePath));

        bool pending=File.Exists(RecoveryJournal.FilePath);
        bool ready=source.SelectedItem is Adapter { Connected:true, Gateway:true } &&
                   cable.SelectedItem is Adapter { Connected:true };
        source.Enabled=cable.Enabled=!busy && state=="idle" && !pending;
        start.Text=pending && state is not "sharing" and not "existing" ? "Restore settings" : "Start sharing";
        start.Enabled=!busy && !refreshing && (pending && state is not "sharing" and not "existing" || state=="idle" && ready);
        stop.Enabled=!busy && state is "sharing" or "existing" or "restore" or "conflict";
        diagnose.Enabled=!busy;
        status.Text=busy ? (phoneCancellation!=null?phoneProgress:"Working… keep the cable connected") :
            lastMessage.Length>0 ? lastMessage :
            state=="sharing" ? "Sharing · check the connected device" :
            state=="existing" ? "Observing existing sharing" :
            pending ? "Previous session needs restoration" :
            ready ? (phoneConnected?"iPhone connected · sharing is off":"Ready · sharing is off") :
            source.SelectedItem is Adapter { Connected:true, Gateway:true } ? (phoneConnected?"iPhone connected · sharing is off":"Wi-Fi connected · sharing is off") :
            cable.Items.Count==0 ? "Plug in a USB Ethernet adapter" :
            cable.SelectedItem is Adapter { Connected:false } ? "Connect a cable to the other device" :
            "Connect this laptop to Wi-Fi first";
        status.ForeColor=state is "restore" or "conflict" ? Color.FromArgb(255,198,109) : Color.FromArgb(103,220,170);
    }
    private async Task StartSharing()
    {
        if(busy) return;
        busy=true; lastMessage=""; UpdateControls();
        try {
            broker ??= await BrokerClient.ConnectAsync();
            bool recovery=File.Exists(RecoveryJournal.FilePath);
            var from=source.SelectedItem as Adapter; var to=cable.SelectedItem as Adapter;
            var reply=await broker.Send(recovery ? new("recover") : new("start",from?.Id??Guid.Empty,to?.Id??Guid.Empty));
            state=reply.State; lastMessage=reply.Message;
            if(reply.Ok && state is "sharing" or "existing") {
                traffic=new(); sessionDestination=to!.Id; SampleTraffic();
                sharedProbe=probe==null;
                if(sharedProbe) StartProbe();
            }
        } catch(Exception e) {
            lastMessage=Friendly(e);
            broker?.Dispose(); broker=null;
            state=File.Exists(RecoveryJournal.FilePath)?"restore":"idle";
        } finally { busy=false; UpdateControls(); }
    }
    private async Task<bool> StopSharing()
    {
        if(busy) return false;
        busy=true; UpdateControls(); SampleTraffic();
        try {
            broker ??= await BrokerClient.ConnectAsync();
            string prior=state;
            var reply=await broker.Send(new("stop"));
            state=reply.State; lastMessage=reply.Message;
            if(reply.Ok) {
                if(prior=="existing") lastMessage="Monitoring stopped. The pre-existing sharing connection was left running.";
                sessionDestination=null;
                if(sharedProbe) StopProbe();
                sharedProbe=false;
                broker.Dispose(); broker=null;
            }
            return reply.Ok;
        } catch(Exception e) { lastMessage=Friendly(e); return false; }
        finally { busy=false; UpdateControls(); }
    }
    private bool ConfigurePhone()
    {
        try {using var wifi=new WifiNative();using var setup=new PhoneSetupForm(wifi.Profiles(),phoneSettings);
            if(setup.ShowDialog(this)!=DialogResult.OK)return false;phoneSettings=setup.SelectedSettings;UpdateControls();return true;
        }catch(Exception e){lastMessage=Friendly(e);UpdateControls();return false;}
    }
    private async Task ConnectPhone()
    {
        if(phoneCancellation!=null){phoneCancellation.Cancel();return;}
        if(busy||refreshing||state!="idle")return;
        if(!File.Exists(PhoneBackend.RadioJournal)&&phoneSettings==null&&!ConfigurePhone())return;
        // An idle helper can own the global mutex; release it before connecting.
        broker?.Dispose();broker=null;
        using var ownership=new Mutex(false,@"Global\TetherNest.Sharing.v1");
        bool held;try{held=ownership.WaitOne(0);}catch(AbandonedMutexException){held=true;}
        if(!held){lastMessage="Another TetherNest operation is active. Finish it before connecting.";UpdateControls();return;}
        busy=true;phoneCancellation=new();phoneProgress="Preparing iPhone connection";lastMessage="";UpdateControls();
        try {
            if(File.Exists(RecoveryJournal.FilePath)||WindowsNetwork.Roles().Count!=0)
                throw new InvalidOperationException("Stop sharing before changing your internet connection.");
            if(File.Exists(PhoneBackend.RadioJournal)){await PhoneBackend.RecoverBluetooth();lastMessage="Bluetooth restored. Connect to iPhone when ready.";return;}
            using(var wifi=new WifiNative())if(!wifi.Profiles().Any(p=>p.InterfaceId==phoneSettings!.InterfaceId&&p.Name==phoneSettings.Profile))
                throw new InvalidOperationException("Saved hotspot not found. Open iPhone setup to select it again.");
            await PhoneConnection.Connect(new PhoneBackend(phoneSettings!),phoneSettings!.Profile,message=>{
                phoneProgress=message;UpdateControls();},phoneCancellation.Token);
            lastMessage="iPhone connected · internet verified. Sharing is off.";
            sharedProbe=false;StartProbe();
        }catch(OperationCanceledException){lastMessage="Connection cancelled. Bluetooth restored.";}
        catch(Exception e){lastMessage=Friendly(e);}
        finally{phoneCancellation.Dispose();phoneCancellation=null;busy=false;ownership.ReleaseMutex();await RefreshStatus();}
    }
    private void SampleTraffic()
    {
        if(sessionDestination is not Guid id) return;
        try {
            var sample=NetworkInventory.Find(id).GetIPStatistics();
            traffic.Update(id,sample.BytesReceived,sample.BytesSent);
            trafficLabel.Text=$"Cable traffic this session: {Bytes(traffic.Received+traffic.Sent)}";
        } catch { traffic.Gap(); trafficLabel.Text="Cable traffic: adapter unavailable"; }
    }
    private static string Bytes(long n) => n>=1048576 ? $"{n/1048576d:0.0} MB" : $"{n/1024d:0.0} KB";
    private void StartProbe()
    {
        if(probe!=null) return;
        probe=new(); diagnose.Text="Pause ping"; _=ProbeLoop(probe);
    }
    private async Task ProbeLoop(CancellationTokenSource owner)
    {
        try {
            while(!owner.IsCancellationRequested) {
                using var ping=new Ping();
                try {
                    var reply=await ping.SendPingAsync(System.Net.IPAddress.Parse("1.1.1.1"),TimeSpan.FromSeconds(2),new byte[32],null,owner.Token);
                    if(probe!=owner || IsDisposed) break;
                    latency.Text=reply.Status==IPStatus.Success?$"{reply.RoundtripTime} ms":"No reply";
                } catch(PingException) { if(probe==owner) latency.Text="Unavailable"; }
                await Task.Delay(3000,owner.Token);
            }
        } catch(OperationCanceledException) { }
        finally { owner.Dispose(); }
    }
    private void StopProbe() { var old=probe; probe=null; old?.Cancel(); latency.Text="—"; diagnose.Text="Resume ping"; }
    private void ShowDetails()
    {
        string inventory=string.Join("\n\n",adapters.Where(a=>a.Physical).Select(a=>$"{a.Name}\n{a.Description}\n{(a.Connected?"Link up":"Link down")} · {a.Speed/1000000} Mbps\nIPv4: {a.Addresses}"));
        string sources=string.Join("\n",AdapterChoices.Sources(adapters).Select(a=>$"{a}: {a.Name} ({a.Description})"));
        MessageBox.Show(this, "Eligible internet sources:\n"+sources+"\n\nWindows adapter details (may include inactive entries):\n"+inventory+"\n\nSession cable traffic includes local traffic, not just internet usage.\n"+
            $"Received: {Bytes(traffic.Received)} · Sent: {Bytes(traffic.Sent)}\n"+
            "Laptop ping may follow a VPN or another route; no reply may mean ICMP is blocked.\n\n"+
            "Recovery record: "+RecoveryJournal.FilePath+"\n"+
            "If recovery is blocked by changed settings, leave those settings intact and inspect this record before manual repair. Reconnect the same adapter before retrying.\n\n"+
            "Connect to iPhone restarts Bluetooth to trigger the configured phone automation, then joins the saved hotspot. It does not start Ethernet sharing. Windows Wi-Fi output and automatic fallback are not implemented.",
            "TetherNest · Details",MessageBoxButtons.OK,MessageBoxIcon.Information);
    }
    private static string Friendly(Exception e) => e is Win32Exception { NativeErrorCode:1223 } ?
        "Administrator approval was cancelled. Sharing was not started." : e.Message;
    private async void OnClosing(object? sender,FormClosingEventArgs e)
    {
        if(closing) return;
        if(busy) { phoneCancellation?.Cancel(); e.Cancel=true; return; }
        if(broker!=null) {
            e.Cancel=true;
            if(!await StopSharing()) return;
            closing=true; Close();
        }
    }
    private void OnPower(object sender,PowerModeChangedEventArgs e)
    {
        if(IsDisposed || !IsHandleCreated) return;
        BeginInvoke((Action)(async ()=> {
            if(e.Mode==PowerModes.Suspend && state=="sharing" && !busy) await StopSharing();
            else if(e.Mode==PowerModes.Resume) await RefreshStatus();
        }));
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing) { timer.Dispose(); SystemEvents.PowerModeChanged-=OnPower; StopProbe(); broker?.Dispose(); }
        base.Dispose(disposing);
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e); int dark=1,rounded=2;
        _=DwmSetWindowAttribute(Handle,20,ref dark,4); _=DwmSetWindowAttribute(Handle,33,ref rounded,4);
    }
}
