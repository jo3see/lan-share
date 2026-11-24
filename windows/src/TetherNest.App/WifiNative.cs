using System.ComponentModel;
using System.Runtime.InteropServices;
namespace TetherNest;
public sealed record WifiProfile(Guid InterfaceId,string Name)
{
    public override string ToString()=>Name;
}
public sealed class WifiNative:IDisposable
{
    private IntPtr handle;
    public WifiNative(){Check(WlanOpenHandle(2,IntPtr.Zero,out _,out handle));}
    public void Dispose(){if(handle!=IntPtr.Zero){WlanCloseHandle(handle,IntPtr.Zero);handle=IntPtr.Zero;}}
    static void Check(uint error){if(error==5)throw new InvalidOperationException("Windows denied Wi-Fi access. Enable Location access for desktop apps in Windows Settings and retry.");if(error!=0)throw new Win32Exception((int)error);}
    public Guid[] Interfaces(){Check(WlanEnumInterfaces(handle,IntPtr.Zero,out var list));try{
        int count=Marshal.ReadInt32(list);if(count<0||count>64)throw new InvalidOperationException("Invalid Wi-Fi interface list.");
        return Enumerable.Range(0,count).Select(i=>Marshal.PtrToStructure<Guid>(list+8+i*532)).ToArray();
    }finally{WlanFreeMemory(list);}}
    public WifiProfile[] Profiles(){var result=new List<WifiProfile>();foreach(var item in Interfaces()){
        var id=item;Check(WlanGetProfileList(handle,ref id,IntPtr.Zero,out var list));try{
            int count=Marshal.ReadInt32(list);if(count<0||count>4096)throw new InvalidOperationException("Invalid Wi-Fi profile list.");
            for(int i=0;i<count;i++)result.Add(new(id,Marshal.PtrToStringUni(list+8+i*516,256)!.TrimEnd('\0')));
        }finally{WlanFreeMemory(list);}
    }return result.OrderBy(p=>p.Name,StringComparer.OrdinalIgnoreCase).ToArray();}
    public string? Current(Guid id){uint error=WlanQueryInterface(handle,ref id,7,IntPtr.Zero,out var size,out var data,out _);
        if(error==5023)return null;Check(error);try {if(size<520)throw new InvalidOperationException("Incomplete Wi-Fi connection details.");return Marshal.ReadInt32(data)==1?Marshal.PtrToStringUni(data+8,256)!.TrimEnd('\0'):null;}finally{WlanFreeMemory(data);}}
    public void Connect(WifiProfile profile){var id=profile.InterfaceId;var parameters=new Connection{Mode=0,Profile=profile.Name,BssType=1};Check(WlanConnect(handle,ref id,ref parameters,IntPtr.Zero));}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct Connection{public int Mode;[MarshalAs(UnmanagedType.LPWStr)]public string Profile;public IntPtr Ssid,BssidList;public int BssType;public uint Flags;}
    [DllImport("wlanapi.dll")]static extern uint WlanOpenHandle(uint version,IntPtr reserved,out uint negotiated,out IntPtr handle);
    [DllImport("wlanapi.dll")]static extern uint WlanCloseHandle(IntPtr handle,IntPtr reserved);
    [DllImport("wlanapi.dll")]static extern uint WlanEnumInterfaces(IntPtr handle,IntPtr reserved,out IntPtr list);
    [DllImport("wlanapi.dll")]static extern uint WlanGetProfileList(IntPtr handle,ref Guid id,IntPtr reserved,out IntPtr list);
    [DllImport("wlanapi.dll")]static extern uint WlanQueryInterface(IntPtr handle,ref Guid id,int opcode,IntPtr reserved,out uint size,out IntPtr data,out int type);
    [DllImport("wlanapi.dll")]static extern uint WlanConnect(IntPtr handle,ref Guid id,ref Connection parameters,IntPtr reserved);
    [DllImport("wlanapi.dll")]static extern void WlanFreeMemory(IntPtr memory);
}
