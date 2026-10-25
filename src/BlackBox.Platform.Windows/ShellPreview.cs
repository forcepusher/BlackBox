using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BlackBox.Platform.Windows;

/// <summary>
/// A file shown by the previewer Windows has for its kind, as Explorer's preview pane shows it: Word,
/// Excel and PowerPoint's own with Office installed, the PDF reader's (Edge's, Acrobat's), Windows'
/// own for HTML and RTF. It draws into a window of BlackBox's; the previewer runs in Windows'
/// preview host (prevhost.exe), so one that fails or hangs takes only that process down. Called
/// through the interfaces' tables directly (no COM interop in .NET's trimmed build).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class ShellPreview : IDisposable
{
    private static readonly Guid IID_IPreviewHandler = new("8895b1c6-b41f-4c1c-a562-0d564250836f");
    private static readonly Guid IID_IInitializeWithFile = new("b7d14566-0509-4cce-a71f-0a554233bd9b");
    private static readonly Guid IID_IInitializeWithItem = new("7f73be3f-fb79-493c-a6c7-7ee14e245841");
    private static readonly Guid IID_IInitializeWithStream = new("b824b49d-22ac-4161-ac8a-9916e8fa3f7f");
    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    private const uint CLSCTX_INPROC_SERVER = 0x1, CLSCTX_LOCAL_SERVER = 0x4;
    private const uint STGM_READ = 0, STGM_SHARE_DENY_NONE = 0x40;
    private const uint ASSOCF_INIT_DEFAULTTOSTAR = 0x4, ASSOCSTR_SHELLEXTENSION = 16;

    private nint _handler;

    private ShellPreview(nint handler) => _handler = handler;

    /// <summary>
    /// Edge's PDF previewer, which Windows 10 and 11 have: tried for a PDF after the one the PDF's
    /// program registered. Acrobat's ("Adobe PDF Preview Handler for Vista") was the one registered on
    /// the laptop and failed with E_FAIL on every PDF (2026-10-25).
    /// </summary>
    private static readonly Guid EdgePdfPreviewer = new("3A84F9C2-6164-485C-A7D9-4B27F8AC009E");

    /// <summary>The previewer Windows has for files like <paramref name="path"/> (by its extension); null when none.</summary>
    public static Guid? HandlerFor(string path) => Handlers(path).Cast<Guid?>().FirstOrDefault();

    /// <summary>The previewers to try for files like <paramref name="path"/>, the registered one first.</summary>
    private static List<Guid> Handlers(string path)
    {
        var handlers = new List<Guid>();
        var ext = Path.GetExtension(path);
        if (ext.Length == 0)
            return handlers;
        var buffer = new char[64];
        var size = (uint)buffer.Length;
        fixed (char* b = buffer)
            if (AssocQueryStringW(ASSOCF_INIT_DEFAULTTOSTAR, ASSOCSTR_SHELLEXTENSION, ext, "{8895b1c6-b41f-4c1c-a562-0d564250836f}", b, ref size) == 0
                && Guid.TryParse(new string(buffer, 0, (int)Math.Max(0, size - 1)), out var clsid))
                handlers.Add(clsid);
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) && !handlers.Contains(EdgePdfPreviewer)
            && Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{EdgePdfPreviewer}}}") is { } edge)
        {
            edge.Dispose();
            handlers.Add(EdgePdfPreviewer);
        }
        return handlers;
    }

    /// <summary>
    /// Shows <paramref name="path"/> in the window <paramref name="hwnd"/>, <paramref name="width"/> by
    /// <paramref name="height"/> pixels, with the first previewer for it that can. On the window's
    /// thread, which COM was started on as a single-threaded apartment (the UI thread: Main is
    /// [STAThread]). Throws <see cref="COMException"/> (the first previewer's) when none can.
    /// </summary>
    public static ShellPreview Open(string path, nint hwnd, int width, int height)
    {
        COMException? failed = null;
        foreach (var clsid in Handlers(path))
            try
            {
                return Open(clsid, path, hwnd, width, height);
            }
            catch (COMException ex)
            {
                failed ??= ex;
            }
        throw failed ?? new COMException("No previewer for this kind of file.", unchecked((int)0x80040154));
    }

    private static ShellPreview Open(Guid clsid, string path, nint hwnd, int width, int height)
    {
        var iid = IID_IPreviewHandler;
        nint handler;
        // In Windows' preview host as Explorer does, else in BlackBox's own process.
        var hr = CoCreateInstance(&clsid, 0, CLSCTX_LOCAL_SERVER, &iid, &handler);
        if (hr < 0)
            Check(CoCreateInstance(&clsid, 0, CLSCTX_INPROC_SERVER, &iid, &handler));
        var preview = new ShellPreview(handler);
        var step = "taking the file";
        try
        {
            preview.Initialize(path);
            step = "showing it";
            var rect = new RECT { Right = width, Bottom = height };
            Check(((delegate* unmanaged<nint, nint, RECT*, int>)Vtbl(handler)[3])(handler, hwnd, &rect)); // SetWindow
            Check(((delegate* unmanaged<nint, RECT*, int>)Vtbl(handler)[4])(handler, &rect)); // SetRect
            Check(((delegate* unmanaged<nint, int>)Vtbl(handler)[5])(handler)); // DoPreview
            return preview;
        }
        catch (COMException ex)
        {
            preview.Dispose();
            throw new COMException($"{ex.Message} ({step})", ex.ErrorCode);
        }
        catch
        {
            preview.Dispose();
            throw;
        }
    }

    /// <summary>Hands the previewer the file, the way it takes one: by its path, as a shell item, or as a stream.</summary>
    private void Initialize(string path)
    {
        if (Query(IID_IInitializeWithFile) is var withFile && withFile != 0)
            try
            {
                fixed (char* p = path)
                    Check(((delegate* unmanaged<nint, char*, uint, int>)Vtbl(withFile)[3])(withFile, p, STGM_READ));
                return;
            }
            finally { Release(withFile); }
        if (Query(IID_IInitializeWithItem) is var withItem && withItem != 0)
            try
            {
                var iid = IID_IShellItem;
                nint item;
                Check(SHCreateItemFromParsingName(path, 0, &iid, &item));
                try { Check(((delegate* unmanaged<nint, nint, uint, int>)Vtbl(withItem)[3])(withItem, item, STGM_READ)); }
                finally { Release(item); }
                return;
            }
            finally { Release(withItem); }
        if (Query(IID_IInitializeWithStream) is var withStream && withStream != 0)
            try
            {
                nint stream;
                Check(SHCreateStreamOnFileEx(path, STGM_READ | STGM_SHARE_DENY_NONE, 0, false, 0, &stream));
                try { Check(((delegate* unmanaged<nint, nint, uint, int>)Vtbl(withStream)[3])(withStream, stream, STGM_READ)); }
                finally { Release(stream); }
                return;
            }
            finally { Release(withStream); }
        throw new COMException("The previewer takes no file.", unchecked((int)0x80004002));
    }

    /// <summary>The window it draws in is now <paramref name="width"/> by <paramref name="height"/> pixels.</summary>
    public void Resize(int width, int height)
    {
        if (_handler == 0)
            return;
        var rect = new RECT { Right = width, Bottom = height };
        _ = ((delegate* unmanaged<nint, RECT*, int>)Vtbl(_handler)[4])(_handler, &rect); // SetRect
    }

    public void Dispose()
    {
        var handler = _handler;
        _handler = 0;
        if (handler == 0)
            return;
        _ = ((delegate* unmanaged<nint, int>)Vtbl(handler)[6])(handler); // Unload
        Release(handler);
    }

    private nint Query(Guid iid)
    {
        nint result;
        return ((delegate* unmanaged<nint, Guid*, nint*, int>)Vtbl(_handler)[0])(_handler, &iid, &result) >= 0 ? result : 0;
    }

    private static nint* Vtbl(nint unknown) => *(nint**)unknown;

    private static void Release(nint unknown) => _ = ((delegate* unmanaged<nint, uint>)Vtbl(unknown)[2])(unknown);

    private static void Check(int hr)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* result);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryStringW(uint flags, uint str, string assoc, string extra, char* output, ref uint size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, Guid* iid, nint* item);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes, [MarshalAs(UnmanagedType.Bool)] bool create, nint template, nint* stream);
}
