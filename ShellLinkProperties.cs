using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
internal static class ShellLinkProperties
{
    // PKEY_AppUserModel_ID / System.AppUserModel.ID.
    internal static readonly PropertyKey AppUserModelIdKey = new()
    {
        FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        PropertyId = 5
    };

    public static string ReadAppUserModelId(object shellLink)
    {
        var store = (IPropertyStore)shellLink;
        PropertyKey key = AppUserModelIdKey;
        PropVariant value = default;
        try
        {
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out value));
            return value.Type switch
            {
                0 => string.Empty, // An absent property returns VT_EMPTY, not an error.
                31 => Marshal.PtrToStringUni(value.Pointer) ?? string.Empty, // VT_LPWSTR
                8 => Marshal.PtrToStringBSTR(value.Pointer), // VT_BSTR
                _ => throw new InvalidDataException("AppUserModelID has an unexpected property type.")
            };
        }
        finally { Marshal.ThrowExceptionForHR(PropVariantClear(ref value)); }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    // The application and test runner target win-x64; PROPVARIANT's union occupies 16 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
}
