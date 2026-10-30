using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Sloperator.Platform.Windows;

/// <summary>
/// COM and WinRT objects called through their vtables, only the few methods each caller uses:
/// .NET no longer marshals WinRT, and its projection would have changed every project's target
/// framework and brought megabytes of assemblies. Shared by Media Foundation, the PDF renderer,
/// text recognition and the toasts, which each had their own copy of these.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class ComAbi
{
    private static readonly Guid IAsyncInfo = new("00000036-0000-0000-C000-000000000046");
    private static readonly Guid IClosable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");

    /// <summary>COM on the calling thread, in the multithreaded apartment where nothing else has been set up; an STA thread works too.</summary>
    public static void Start() => _ = CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */);

    /// <summary>The method at <paramref name="index"/> of the object's vtable (IUnknown's QueryInterface 0, AddRef 1, Release 2).</summary>
    public static void* Slot(nint obj, int index) => (*(void***)obj)[index];

    public static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    public static void Release(nint obj)
    {
        if (obj != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, 2))(obj);
    }

    /// <summary>The object's interface <paramref name="iid"/>, to be released by the caller; throws when it has none.</summary>
    public static nint Query(nint obj, Guid iid)
    {
        nint result;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(obj, 0))(obj, &iid, &result));
        return result;
    }

    // ---- WinRT ----------------------------------------------------------------------------------

    /// <summary>The activation factory (or statics) of <paramref name="runtimeClass"/> as interface <paramref name="iid"/>.</summary>
    public static nint Factory(string runtimeClass, Guid iid)
    {
        using var name = new HString(runtimeClass);
        nint factory;
        Check(RoGetActivationFactory(name.Handle, &iid, &factory));
        return factory;
    }

    /// <summary>A new <paramref name="runtimeClass"/> made by its default constructor, as IInspectable.</summary>
    public static nint Activate(string runtimeClass)
    {
        using var name = new HString(runtimeClass);
        nint instance;
        Check(RoActivateInstance(name.Handle, &instance));
        return instance;
    }

    /// <summary>A string property: the HSTRING getter at <paramref name="slot"/>.</summary>
    public static string Text(nint obj, int slot)
    {
        nint hstring;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(obj, slot))(obj, &hstring));
        try
        {
            uint length;
            var chars = WindowsGetStringRawBuffer(hstring, &length);
            return chars == null ? "" : new string(chars, 0, (int)length);
        }
        finally { _ = WindowsDeleteString(hstring); }
    }

    /// <summary>The items of an IVectorView (GetAt 6, get_Size 7), each to be released by the caller.</summary>
    public static List<nint> Items(nint vector)
    {
        uint count;
        Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(vector, 7))(vector, &count));
        var items = new List<nint>((int)count);
        for (uint i = 0; i < count; i++)
        {
            nint item;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(vector, 6))(vector, i, &item));
            items.Add(item);
        }
        return items;
    }

    /// <summary>
    /// Waits for a WinRT async operation or action to end, polling (each one waited for is short);
    /// throws what it failed with.
    /// </summary>
    public static void Wait(nint async)
    {
        var info = Query(async, IAsyncInfo);
        try
        {
            // IAsyncInfo: get_Status 7 (0 started, 1 completed, 2 canceled, 3 error), get_ErrorCode 8.
            for (var spins = 0; ; spins++)
            {
                int status;
                Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(info, 7))(info, &status));
                if (status == 1)
                    return;
                if (status == 2)
                    throw new OperationCanceledException();
                if (status == 3)
                {
                    int error;
                    Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(info, 8))(info, &error));
                    Marshal.ThrowExceptionForHR(error);
                }
                if (spins < 20) Thread.Yield();
                else Thread.Sleep(1);
            }
        }
        finally { Release(info); }
    }

    /// <summary>IClosable.Close (6), through the object's own IClosable.</summary>
    public static void Close(nint obj)
    {
        var closable = Query(obj, IClosable);
        try { Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(closable, 6))(closable)); }
        finally { Release(closable); }
    }

    /// <summary>A WinRT string, freed on Dispose.</summary>
    public readonly struct HString : IDisposable
    {
        public nint Handle { get; }

        public HString(string text)
        {
            nint handle;
            fixed (char* chars = text)
                Check(WindowsCreateString(chars, (uint)text.Length, &handle));
            Handle = handle;
        }

        public void Dispose() => _ = WindowsDeleteString(Handle);
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint coInit);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint activatableClassId, Guid* iid, nint* factory);
    [DllImport("combase.dll")] private static extern int RoActivateInstance(nint activatableClassId, nint* instance);
    [DllImport("combase.dll")] private static extern int WindowsCreateString(char* source, uint length, nint* hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint hstring);
    [DllImport("combase.dll")] private static extern char* WindowsGetStringRawBuffer(nint hstring, uint* length);
}
