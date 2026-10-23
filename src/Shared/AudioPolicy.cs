using System.Runtime.InteropServices;

// Windows' audio policy interface (the one Sound settings uses): set the default
// playback/recording device (no admin) and rename/hide endpoints (admin).
// Undocumented but stable since Windows 7.
internal static class AudioPolicy
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        int GetMixFormat(string deviceId, IntPtr format);
        int GetDeviceFormat(string deviceId, int defaultFormat, IntPtr format);
        int ResetDeviceFormat(string deviceId);
        int SetDeviceFormat(string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        int GetProcessingPeriod(string deviceId, int defaultPeriod, IntPtr defaultPeriodOut, IntPtr minimumPeriod);
        int SetProcessingPeriod(string deviceId, IntPtr period);
        int GetShareMode(string deviceId, IntPtr mode);
        int SetShareMode(string deviceId, IntPtr mode);
        int GetPropertyValue(string deviceId, int fxStore, ref PropertyKey key, out PropVariant value);
        int SetPropertyValue(string deviceId, int fxStore, ref PropertyKey key, ref PropVariant value);
        int SetDefaultEndpoint(string deviceId, int role);
        int SetEndpointVisibility(string deviceId, int visible);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient
    {
    }

    // PKEY_Device_DeviceDesc: the main name shown in Sound settings.
    private static readonly Guid DeviceDescFormat = new("a45c254e-df1c-4efd-8020-67d146a850e0");

    private static IPolicyConfig Create() => (IPolicyConfig)new PolicyConfigClient();

    /// <summary>Makes the endpoint the default for all roles (console, multimedia, communications).</summary>
    public static void SetDefault(string deviceId)
    {
        IPolicyConfig policy = Create();
        for (int role = 0; role < 3; role++)
        {
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, role));
        }
    }

    // PKEY_DeviceInterface_FriendlyName: the grey second line in Sound settings.
    private static readonly Guid InterfaceNameFormat = new("026e516e-b814-414b-83cd-856d6fef4822");

    /// <summary>Renames an endpoint (admin): main name and the grey second line.</summary>
    public static void Rename(string deviceId, string name, string? secondLine = null)
    {
        SetString(deviceId, DeviceDescFormat, name);
        if (secondLine is not null)
        {
            SetString(deviceId, InterfaceNameFormat, secondLine);
        }
    }

    private static void SetString(string deviceId, Guid format, string text)
    {
        IPolicyConfig policy = Create();
        var key = new PropertyKey { FormatId = format, PropertyId = 2 };
        string name = text;
        var value = new PropVariant { VarType = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(name) };
        try
        {
            Marshal.ThrowExceptionForHR(policy.SetPropertyValue(deviceId, 0, ref key, ref value));
        }
        finally
        {
            Marshal.FreeCoTaskMem(value.Pointer);
        }
    }

    /// <summary>Shows or hides an endpoint in Sound settings (admin).</summary>
    public static void SetVisible(string deviceId, bool visible)
    {
        Marshal.ThrowExceptionForHR(Create().SetEndpointVisibility(deviceId, visible ? 1 : 0));
    }
}
