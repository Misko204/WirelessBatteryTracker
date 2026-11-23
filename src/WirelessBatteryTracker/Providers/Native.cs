using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BatteryTray.Providers;

[StructLayout(LayoutKind.Sequential)]
internal struct DEVPROPKEY
{
    public Guid fmtid;
    public uint pid;
    public DEVPROPKEY(string guid, uint pid) { fmtid = new Guid(guid); this.pid = pid; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct HIDP_CAPS
{
    public ushort Usage;
    public ushort UsagePage;
    public ushort InputReportByteLength;
    public ushort OutputReportByteLength;
    public ushort FeatureReportByteLength;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
    public ushort NumberLinkCollectionNodes;
    public ushort NumberInputButtonCaps;
    public ushort NumberInputValueCaps;
    public ushort NumberInputDataIndices;
    public ushort NumberOutputButtonCaps;
    public ushort NumberOutputValueCaps;
    public ushort NumberOutputDataIndices;
    public ushort NumberFeatureButtonCaps;
    public ushort NumberFeatureValueCaps;
    public ushort NumberFeatureDataIndices;
}

internal static class Native
{
    public const int CR_SUCCESS = 0;
    public const int CR_BUFFER_SMALL = 0x1A;
    public const int CM_GETIDLIST_FILTER_ENUMERATOR = 0x1;

    // {104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2 — battery % Windows reads from Bluetooth headsets (HFP) and LE devices.
    public static readonly DEVPROPKEY DEVPKEY_Bluetooth_Battery = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
    public static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    public static readonly DEVPROPKEY DEVPKEY_NAME = new("b725f130-47ef-101a-a5f1-02608c9eebac", 10);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_ID_List_SizeW(out int len, string? filter, int flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_ID_ListW(string? filter, char[] buffer, int len, int flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, int flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint propType,
        byte[]? buffer, ref int bufferSize, int flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_List_SizeW(out int len, ref Guid classGuid, string? deviceId, int flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_ListW(ref Guid classGuid, string? deviceId, char[] buffer, int len, int flags);

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("hid.dll")] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll")] public static extern bool HidD_GetSerialNumberString(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attrs);

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    public static (ushort vid, ushort pid)? GetHidIds(string path)
    {
        using var h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var a = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        return HidD_GetAttributes(h, ref a) ? (a.VendorID, a.ProductID) : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec,
        uint disposition, uint flags, IntPtr template);

    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);

    public static List<string> SplitMultiSz(char[] buffer)
    {
        var list = new List<string>();
        int start = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0') continue;
            if (i == start) break;
            list.Add(new string(buffer, start, i - start));
            start = i + 1;
        }
        return list;
    }

    public static List<string> GetDeviceIds(string enumerator)
    {
        if (CM_Get_Device_ID_List_SizeW(out int len, enumerator, CM_GETIDLIST_FILTER_ENUMERATOR) != CR_SUCCESS || len <= 1)
            return new();
        var buf = new char[len];
        if (CM_Get_Device_ID_ListW(enumerator, buf, len, CM_GETIDLIST_FILTER_ENUMERATOR) != CR_SUCCESS)
            return new();
        return SplitMultiSz(buf);
    }

    /// <summary>Returns the devnode handle only if the device is currently present.</summary>
    public static uint? Locate(string id) =>
        CM_Locate_DevNodeW(out uint inst, id, 0) == CR_SUCCESS ? inst : null;

    public static byte[]? GetProperty(uint devInst, DEVPROPKEY key, out uint type)
    {
        int size = 0;
        int cr = CM_Get_DevNode_PropertyW(devInst, ref key, out type, null, ref size, 0);
        if (cr != CR_BUFFER_SMALL || size == 0) return null;
        var buf = new byte[size];
        return CM_Get_DevNode_PropertyW(devInst, ref key, out type, buf, ref size, 0) == CR_SUCCESS ? buf : null;
    }

    public static string? GetStringProperty(uint devInst, DEVPROPKEY key)
    {
        var data = GetProperty(devInst, key, out _);
        return data == null ? null : System.Text.Encoding.Unicode.GetString(data).TrimEnd('\0');
    }

    public static List<string> GetHidInterfacePaths()
    {
        HidD_GetHidGuid(out var guid);
        if (CM_Get_Device_Interface_List_SizeW(out int len, ref guid, null, 0) != CR_SUCCESS || len <= 1) return new();
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref guid, null, buf, len, 0) != CR_SUCCESS) return new();
        return SplitMultiSz(buf);
    }

    public static HIDP_CAPS? GetHidCaps(string path)
    {
        // Access 0 lets us query caps even on keyboards/mice that Windows opens exclusively.
        using var h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        if (!HidD_GetPreparsedData(h, out var pp)) return null;
        try { return HidP_GetCaps(pp, out var caps) == 0x00110000 /* HIDP_STATUS_SUCCESS */ ? caps : null; }
        finally { HidD_FreePreparsedData(pp); }
    }
}
