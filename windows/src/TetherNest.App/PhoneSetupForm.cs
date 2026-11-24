using System.Diagnostics;
namespace TetherNest;
public sealed class PhoneSetupForm:Form
{
    public PhoneSettings? SelectedSettings {get;private set;}
    public PhoneSetupForm(WifiProfile[] profiles,PhoneSettings? prior)
    {
        Text="TetherNest · iPhone setup";ClientSize=new(470,350);StartPosition=FormStartPosition.CenterParent;
        FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=MinimizeBox=false;
        var layout=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(18),AutoScroll=true};Controls.Add(layout);
        layout.Controls.Add(new Label{AutoSize=true,MaximumSize=new(425,0),Text="One-time setup: add the TetherNest shortcut on your iPhone, enable its Bluetooth trigger, and turn Confirm Before Run off. Pair the phone in Windows. Save the hotspot by joining it once in Windows Wi-Fi settings."});
        var link=new LinkLabel{Text="Open iPhone setup link",AutoSize=true,Margin=new Padding(0,12,0,12)};
        link.LinkClicked+=(_,_)=>Process.Start(new ProcessStartInfo("https://www.icloud.com/shortcuts/d9444411a7cc4d6abbeb01ad9bff7124"){UseShellExecute=true});layout.Controls.Add(link);
        layout.Controls.Add(new Label{Text="Choose your saved iPhone hotspot",AutoSize=true});
        var picker=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=425};picker.Items.AddRange(profiles.Cast<object>().ToArray());
        picker.SelectedItem=profiles.FirstOrDefault(p=>p.InterfaceId==prior?.InterfaceId&&p.Name==prior.Profile);layout.Controls.Add(picker);
        var consent=new CheckBox{Text="Shortcut enabled; allow Bluetooth to restart when I connect",AutoSize=true,MaximumSize=new(425,0),Margin=new Padding(0,16,0,8),Checked=prior!=null};layout.Controls.Add(consent);
        layout.Controls.Add(new Label{Text="This briefly disconnects all Bluetooth devices. Nothing connects automatically when TetherNest opens.",AutoSize=true,MaximumSize=new(425,0)});
        var save=new Button{Text="Save setup",AutoSize=true,Margin=new Padding(0,15,0,0)};
        save.Click+=(_,_)=>{if(picker.SelectedItem is not WifiProfile p||!consent.Checked){MessageBox.Show(this,"Choose the hotspot and confirm its setup first.");return;}SelectedSettings=new(p.InterfaceId,p.Name);SelectedSettings.Save();DialogResult=DialogResult.OK;};layout.Controls.Add(save);
    }
}
