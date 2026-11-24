using TetherNest;
using TetherNest.Core;
using System.Runtime.InteropServices;

internal static class Program
{
    static readonly Guid Source=Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid Dest=Guid.Parse("22222222-2222-2222-2222-222222222222");
    static int count;
    static void Check(bool good,string name) { if(!good) throw new Exception("FAIL: "+name); Console.WriteLine("PASS: "+name); count++; }
    static void Throws(Action action,string name) { try { action(); } catch(InvalidOperationException) { Check(true,name);return; } throw new Exception("Expected rejection: "+name); }
    [STAThread]
    static int Main(string[] args)
    {
        try {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Console.WriteLine("Architecture: "+RuntimeInformation.ProcessArchitecture);
            if(args.Contains("--phone-inspect")) {
                using var wifi=new WifiNative();var profiles=wifi.Profiles();
                Check(profiles.Length>0,"native WLAN enumerates saved profiles without passwords");
                foreach(var id in wifi.Interfaces()){
                    var current=wifi.Current(id);Console.WriteLine("Wi-Fi connected profile: "+current);
                    if(current!=null)Check(new PhoneBackend(new(id,current)).VerifyInternet(CancellationToken.None).GetAwaiter().GetResult(),"bound HTTPS internet verification works on current Wi-Fi");
                }
                var radios=Windows.Devices.Radios.Radio.GetRadiosAsync().AsTask().GetAwaiter().GetResult();
                Console.WriteLine("Bluetooth radios: "+radios.Count(r=>r.Kind==Windows.Devices.Radios.RadioKind.Bluetooth));
                Check(radios.Any(r=>r.Kind==Windows.Devices.Radios.RadioKind.Bluetooth),"WinRT can inspect real Bluetooth radio without toggling it");
            }
            TestSession();
            TestAutomaticRestoration();
            TestPhoneConnection().GetAwaiter().GetResult();
            TestSourceChoices();
            if(args.Contains("--inspect")) {
                string journalDir=Path.Combine(Environment.CurrentDirectory,"windows",".build","journal-check-"+Guid.NewGuid().ToString("N"));
                RecoveryJournal.Prepare(journalDir);
                var diskJournal=new RecoveryJournal(journalDir);
                var baseline=new Snapshot(new(),new(true,true,"DHCP",false));
                var record=new JournalRecord(1,Guid.NewGuid(),Source,Dest,baseline,baseline,false,"prepared");
                diskJournal.Save(record);
                Check(diskJournal.Load().Before.SameAs(baseline),"protected journal round-trips durable state");
                diskJournal.Save(record with {Version=999});
                Throws(()=>diskJournal.Load(),"unknown journal version is rejected");
                diskJournal.Clear();Directory.Delete(journalDir);
                Check(!diskJournal.Exists,"verified journal can be cleared");
                var actual=NetworkInventory.Read();
                Console.WriteLine("Physical Wi-Fi: "+actual.Count(a=>a.Physical&&a.Wifi&&a.Connected));
                Console.WriteLine("Physical Ethernet link up: "+actual.Count(a=>a.Physical&&a.Ethernet&&a.Connected));
                Console.WriteLine("Existing shared adapters: "+WindowsNetwork.Roles().Count);
                string executable=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"TetherNest.exe"));
                using var helper=BrokerClient.ConnectAsync(false,executable).GetAwaiter().GetResult();
                var inspected=helper.Send(new("inspect")).GetAwaiter().GetResult();
                Check(inspected.Ok,"real broker handshake and read-only ICS inspection");
                var refused=helper.Send(new("not-a-command")).GetAwaiter().GetResult();
                Check(!refused.Ok,"broker rejects unsupported/untrusted commands");
            }
            if(args.Length>0 && args[0].EndsWith(".png",StringComparison.OrdinalIgnoreCase)) {



                using var form=new MainForm();
                form.Show();
                PumpFor(1800);
                Check(form.IsIdle,"real UI opens without connection management or sharing");
                                bool link=NetworkInventory.Read().Any(a=>a.Physical&&a.Ethernet&&a.Connected);
                Check(form.SharingReady==link,link?"detected Wi-Fi and cable enable Start sharing":"disconnected cable correctly prevents sharing without blocking phone setup");
                Check(form.SourceChoiceCount==AdapterChoices.Sources(NetworkInventory.Read()).Length,"live dropdown contains only eligible source choices");
                Console.WriteLine("Live source choices: "+form.SourceChoiceCount);
                using var bitmap=new Bitmap(form.Width,form.Height);
                form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
                bitmap.Save(args[0]);
                form.Close();
            }
            Console.WriteLine(count+" checks passed.");
            return 0;
        } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static void PumpFor(int ms) { var until=DateTime.UtcNow.AddMilliseconds(ms); while(DateTime.UtcNow<until) {Application.DoEvents();Thread.Sleep(10);} }
    static void TestSourceChoices()
    {
        var connected=new Adapter(Source,"Wi-Fi","Same radio",true,false,true,true,1000,true,"") {ConnectionClass="Built-in Wi-Fi"};
        var down=connected with {Id=Guid.NewGuid(),Name="Wi-Fi 2",Connected=false,Gateway=false};
        var hidden=down with {Id=Guid.NewGuid(),Name="Local Area Connection* 3"};
        var absent4=down with {Id=Guid.NewGuid(),Name="Wi-Fi 4"};
        var absent5=down with {Id=Guid.NewGuid(),Name="Wi-Fi 5"};
        Check(AdapterChoices.Sources([connected,down,hidden,absent4,absent5]).Length==1,
            "reported five-entry inventory yields only connected usable source");
        Check(AdapterChoices.Sources([connected,connected]).Length==1,"duplicate adapter identity is not repeated");
        Check(AdapterChoices.Sources([connected with {Physical=false}]).Length==0,"filter interfaces are not internet sources");
        Check(AdapterChoices.Sources([connected with {Gateway=false}]).Length==0,"connected source without IPv4 gateway is not offered for this milestone");
        var second=connected with {Id=Dest,Name="Second Wi-Fi",ConnectionClass="USB Wi-Fi"};
        var choices=AdapterChoices.Sources([second,connected]);
        Check(choices.Length==2,"distinct legitimate sources survive identical descriptions");
        Check(choices.Select(a=>a.ToString()).Distinct().Count()==2,"multiple real sources have friendly distinct labels");
        using var box=new ComboBox();
        AdapterChoices.RefreshPicker(box,choices);
        box.SelectedItem=choices.Single(a=>a.Id==Dest);
        for(int i=0;i<5;i++)AdapterChoices.RefreshPicker(box,AdapterChoices.Sources([connected,second]));
        Check(box.Items.Count==2&&((Adapter)box.SelectedItem!).Id==Dest,"repeated/reordered refresh preserves selection without accumulation");
        AdapterChoices.RefreshPicker(box,AdapterChoices.Sources([connected,second with {Addresses="changed"}]));
        Check(((Adapter)box.SelectedItem!).Id==Dest,"changed adapter details preserve selection by identity");
        AdapterChoices.RefreshPicker(box,AdapterChoices.Sources([connected]));
        Check(box.Items.Count==1&&((Adapter)box.SelectedItem!).Id==Source,"disappeared source falls back to remaining eligible source");
    }
    static async Task TestPhoneConnection()
    {
        var b=new FakePhone();await PhoneConnection.Connect(b,"phone",_=>{},CancellationToken.None);
        Check(string.Join(",",b.Events)=="cycle,join:phone,verify","phone sequence cycles Bluetooth before joining and verifying");
        b=new(){Current="phone"};await PhoneConnection.Connect(b,"phone",_=>{},CancellationToken.None);
        Check(string.Join(",",b.Events)=="verify","healthy existing phone connection avoids disruptive restart");
        b=new(){Internet=false};try{await PhoneConnection.Connect(b,"phone",_=>{},CancellationToken.None);}catch(InvalidOperationException){}
        Check(b.Current=="home"&&b.Events.Last()=="join:home","failed internet check restores prior Wi-Fi");
        b=new(){CycleChange="external"};try{await PhoneConnection.Connect(b,"phone",_=>{},CancellationToken.None);}catch(InvalidOperationException){}
        Check(b.Current=="external"&&!b.Events.Any(e=>e.StartsWith("join:")),"external Wi-Fi change during Bluetooth cycle is preserved");
        b=new(){ExternalDuringVerify=true};try{await PhoneConnection.Connect(b,"phone",_=>{},CancellationToken.None);}catch(InvalidOperationException){}
        Check(b.Current=="external"&&!b.Events.Contains("join:home"),"failed verification never replaces an external Wi-Fi selection");
        b=new(){Current="phone",Internet=false,DisconnectOnCycle=true};try{await PhoneConnection.Connect(b,"phone",_=>{},CancellationToken.None);}catch(InvalidOperationException){}
        Check(b.Events.Contains("join:phone"),"resetting an unhealthy current hotspot permits its expected disconnect");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();b=new();
        try{await PhoneConnection.Connect(b,"phone",_=>{},cancelled.Token);}catch(OperationCanceledException){}
        Check(b.Events.Count==0,"cancelled phone operation does not touch network or radio");
        b=new(){FailJoin=true};try{await PhoneConnection.Connect(b,"phone",_=>{},CancellationToken.None);}catch(InvalidOperationException){}
        Check(b.Current=="home","partially failed join restores previous Wi-Fi");
        b=new(){Current=null,Internet=false};try{await PhoneConnection.Connect(b,"phone",_=>{},CancellationToken.None);}catch(InvalidOperationException){}
        Check(!b.Events.Contains("join:home"),"no previous Wi-Fi does not invent a restore target");
    }
    sealed class FakePhone:IPhoneConnectionBackend
    {
        public string? Current="home",CycleChange;
        public bool Internet=true,ExternalDuringVerify,FailJoin,DisconnectOnCycle;
        public List<string> Events=new();
        public string? CurrentProfile()=>Current;
        public Task CycleBluetooth(CancellationToken token){Events.Add("cycle");if(DisconnectOnCycle)Current=null;if(CycleChange!=null)Current=CycleChange;return Task.CompletedTask;}
        public Task Join(string profile,CancellationToken token){Events.Add("join:"+profile);Current=profile;if(FailJoin&&profile=="phone")throw new InvalidOperationException("Join failed");return Task.CompletedTask;}
        public Task<bool> VerifyInternet(CancellationToken token){Events.Add("verify");if(ExternalDuringVerify){Current="external";return Task.FromResult(false);}return Task.FromResult(Internet);}
    }
    static void TestAutomaticRestoration()
    {
        var state=new AddressSettings(true,false,"DHCP|dns",false);
        int address=0,dns=0;
        AutomaticAddressRestoration.Restore(()=>state,
            ()=>throw new Exception("DHCP is already enabled"),()=>dns++);
        Check(dns==1,"already enabled DHCP does not prevent DNS restoration");
        state=new(true,true,"DHCP|",false);dns=0;
        AutomaticAddressRestoration.Restore(()=>state,()=>address++,()=>dns++);
        Check(address==0&&dns==0,"automatic configuration needs no redundant netsh writes");
        state=new(false,false,"STATIC",false);
        AutomaticAddressRestoration.Restore(()=>state,
            ()=>{address++;state=new(true,true,"DHCP|",false);},()=>dns++);
        Check(address==1&&dns==0,"address restoration re-reads DNS changed by Windows");
        Throws(()=>AutomaticAddressRestoration.Restore(()=>null,()=>address++,()=>dns++),
            "missing adapter fails before configuration writes");
        Check(address==1&&dns==0,"missing adapter causes no restoration commands");
    }
    static void TestSession()
    {
        var b=new FakeBackend(); var j=new FakeJournal(); var session=new SharingSession(b,j);
        Check(session.State=="idle" && b.Writes==0,"new session is idle and nonmutating");
        Check(session.Start(Source,Dest)=="sharing" && j.Exists,"start journals and owns configured pair");
        Check(j.Last is { MutationPending:false, Phase:"sharing" },"settled state is journaled");
        Throws(()=>session.Start(Source,Dest),"duplicate Start rejected");
        Check(session.Stop()=="idle" && !j.Exists && b.Automatic,"Stop restores automatic addressing and clears verified journal");
        int writes=b.Writes; session.Stop(); Check(b.Writes==writes,"repeated Stop is idempotent");
        b.Roles[Source]=0;b.Roles[Dest]=1;
        session=new(b,j); writes=b.Writes;
        Check(session.Start(Source,Dest)=="existing" && !j.Exists,"matching existing sharing is observed");
        session.Stop(); Check(b.Writes==writes && b.Roles.Count==2,"Stop leaves existing sharing untouched");
        b=new(); j=new(); b.Roles[Guid.NewGuid()]=0; session=new(b,j);
        Throws(()=>session.Start(Source,Dest),"unrelated sharing rejected"); Check(b.Writes==0&&!j.Exists,"conflict does not mutate or journal");
        b=new(){Automatic=false};j=new();session=new(b,j);
        Throws(()=>session.Start(Source,Dest),"custom destination configuration rejected");
        b=new();j=new(){FailSave=true};session=new(b,j);
        Throws(()=>session.Start(Source,Dest),"journal failure prevents start");Check(b.Writes==0,"no network writes before durable journal");
        b=new(){FailBefore="enable-source"};j=new();session=new(b,j);
        Throws(()=>session.Start(Source,Dest),"failure before first network change is safe");
        Check(!j.Exists&&b.Writes==0,"no-op failed start leaves no recovery debt");
        b=new(){FailVerify=true};j=new();session=new(b,j);
        Throws(()=>session.Start(Source,Dest),"missing start postcondition triggers rollback");
        Check(!j.Exists&&b.Automatic&&b.Roles.Count==0,"failed verification restores baseline");
        b=new(){FailBefore="enable-cable"};j=new();session=new(b,j);
        Throws(()=>session.Start(Source,Dest),"failed private sharing triggers rollback");
        Check(!j.Exists && b.Roles.Count==0 && b.Automatic,"partial source sharing restored");
        b=new(){FailAfter="enable-cable"};j=new();session=new(b,j);
        Throws(()=>session.Start(Source,Dest),"ambiguous interrupted mutation reports recovery");
        Check(j.Exists && j.Last!.MutationPending,"unknown side effects retain journal");
        b=new();j=new();session=new(b,j);session.Start(Source,Dest);
        b.FailBefore="disable-cable";
        Throws(()=>session.Stop(),"failed Stop leaves recovery pending");
        Check(j.Exists,"failed restore keeps journal");
        b.FailBefore=null;
        new SharingSession(b,j).Stop();Check(!j.Exists&&b.Automatic,"fresh engine recovers known state");
        b=new();j=new();session=new(b,j);session.Start(Source,Dest);
        b.FailBefore="automatic";
        Throws(()=>session.Stop(),"automatic-address restore failure remains pending");
        Check(j.Exists&&b.Roles.Count==0,"journal survives IP restoration failure");
        new SharingSession(b,j).Stop();
        Check(!j.Exists&&b.Automatic,"retry completes address restoration");
        b=new();j=new();session=new(b,j);session.Start(Source,Dest);
        b.External=true;writes=b.Writes;
        Throws(()=>session.Stop(),"external address changes protected");
        Check(b.Writes==writes&&j.Exists,"external conflict leaves settings untouched");
        b=new();j=new();session=new(b,j);session.Start(Source,Dest);
        b.Roles[Guid.NewGuid()]=0;writes=b.Writes;
        Throws(()=>session.Stop(),"external sharing changes protected");Check(b.Writes==writes,"no writes over external sharing");
        b=new();j=new();session=new(b,j);session.Start(Source,Dest);b.Missing=true;
        Throws(()=>session.Stop(),"missing adapter keeps recovery pending");Check(j.Exists,"missing adapter journal retained");
        b.Missing=false;session.Stop();Check(!j.Exists,"reconnected adapter can be restored");
        b=new();j=new();session=new(b,j);session.Start(Source,Dest);b.Connected=false;
        Check(session.Check()=="idle"&&!j.Exists,"link loss stops owned sharing");
        var t=new SessionTraffic();t.Update(Dest,100,200);t.Update(Dest,140,260);
        Check(t.Received==40&&t.Sent==60,"session totals use destination deltas");
        t.Update(Dest,1,2);t.Update(Dest,11,22);
        Check(t.Received==50&&t.Sent==80,"counter reset avoids negative or inflated total");
        t.Gap();t.Update(Dest,10000,20000);
        Check(t.Received==50&&t.Sent==80,"missing samples rebaseline without invented traffic");
    }
    sealed class FakeJournal:IJournal
    {
        public JournalRecord? Last;
        public bool FailSave;
        public bool Exists=>Last!=null;
        public JournalRecord Load()=>Last??throw new InvalidOperationException("Missing journal");
        public void Save(JournalRecord r) { if(FailSave)throw new InvalidOperationException("Disk full");Last=r; }
        public void Clear()=>Last=null;
    }
    sealed class FakeBackend:ISharingBackend
    {
        public Dictionary<Guid,int> Roles=new();
        public bool Automatic=true,External,Missing,Connected=true,FailVerify;
        public string? FailBefore,FailAfter;
        public int Writes;
        public void ValidatePair(Guid s,Guid d) {if(s==d||!Connected)throw new InvalidOperationException("Invalid pair");}
        public Snapshot Read(Guid d)=>new(new(Roles),Missing?null:new(Automatic,true,External?"external":Automatic?"DHCP":"STATIC",false));
        void Write(string stage,Action a) {
            if(FailBefore==stage){FailBefore=null;throw new InvalidOperationException("Before "+stage);}
            Writes++;a();
            if(FailAfter==stage){FailAfter=null;throw new InvalidOperationException("After "+stage);}
        }
        public void Enable(Guid id,int role)=>Write(role==0?"enable-source":"enable-cable",()=>{Roles[id]=role;if(role==1)Automatic=false;});
        public void Disable(Guid id)=>Write(id==Dest?"disable-cable":"disable-source",()=>Roles.Remove(id));
        public void RestoreAutomatic(Guid d)=>Write("automatic",()=>Automatic=true);
        public void VerifyStarted(Guid s,Guid d) {if(FailVerify||Roles.Count!=2)throw new InvalidOperationException("Not started");}
        public bool PairConnected(Guid s,Guid d)=>Connected;
    }
}
