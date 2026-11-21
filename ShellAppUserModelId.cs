using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace RadialMenu1;

internal static class ShellAppUserModelId
{
    private static readonly Guid StoreInterface = typeof(IPropertyStore).GUID;
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");
    private static readonly PropertyKey AppIdKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    private const ushort StringValue = 31;

    public static string? ForShortcut(string path)
    {
        object? link = null;
        try
        {
            if (Type.GetTypeFromCLSID(ShellLinkClass) is not { } type) return null;
            link = Activator.CreateInstance(type);
            if (link is not IPersistFile file || link is not IPropertyStore store) return null;
            file.Load(path, 0);
            return Read(store);
        }
        catch (COMException) { return null; }
        finally
        {
            if (link != null) Marshal.ReleaseComObject(link);
        }
    }

    public static string? ForWindow(nint window)
    {
        if (SHGetPropertyStoreForWindow(window, in StoreInterface, out var store) < 0 || store == null)
            return null;

        try { return Read(store); }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static string? Read(IPropertyStore store)
    {
        PropertyValue value = default;
        try
        {
            return store.GetValue(in AppIdKey, out value) >= 0 && value.Type == StringValue
                ? Marshal.PtrToStringUni(value.Pointer)
                : null;
        }
        finally { PropVariantClear(ref value); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid format, uint id)
    {
        public readonly Guid Format = format;
        public readonly uint Id = id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyValue
    {
        public ushort Type;
        private ushort _reserved1, _reserved2, _reserved3;
        public nint Pointer;
        private nint _variantStorage;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(in PropertyKey key, out PropertyValue value);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(nint window,
        in Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropertyValue value);
}
