using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using static Sloperator.Platform.Windows.ComAbi;

namespace Sloperator.Platform.Windows;

/// <summary>
/// Windows' own notifications (toasts) for an exe that isn't installed: Sloperator's name and icon
/// are registered for its app id under HKCU\Software\Classes\AppUserModelId, which Windows 10 and
/// 11 take in place of a Start menu shortcut, and a click is heard in the process while it runs.
/// The WinRT interfaces are called through their vtables (<see cref="ComAbi"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsToasts : Core.Platform.INotifications
{
    /// <summary>The app id the toasts go out under; Windows shows its display name over them.</summary>
    public const string AppId = "Sloperator";

    private static readonly Lock Gate = new();

    /// <summary>The toast on show for each tag, with its notifier, to take down again.</summary>
    private static readonly Dictionary<string, (nint Notifier, nint Toast)> Shown = [];

    /// <summary>What a click on each toast does, by the id its handler carries.</summary>
    private static readonly ConcurrentDictionary<int, Action> Clicks = new();

    private static int _nextId;
    private static string? _registeredIcon;

    /// <summary>
    /// Shows a toast: <paramref name="title"/> in bold over <paramref name="text"/>, in place of the
    /// one shown under <paramref name="tag"/> before. <paramref name="clicked"/> runs on a thread of
    /// Windows' when the user clicks it. False when Windows wouldn't show it (notifications off,
    /// an old Windows); nothing is thrown.
    /// </summary>
    public bool Show(string tag, string title, string text, string? iconPath, Action clicked)
    {
        lock (Gate)
        {
            try
            {
                // In the multithreaded apartment: the click comes on a thread of Windows' then, with no window's messages to wait for.
                Start();
                Register(iconPath);
                Take(tag);
                var notifier = CreateNotifier();
                var toast = CreateToast(Xml(title, text));
                try
                {
                    var id = Interlocked.Increment(ref _nextId);
                    Clicks[id] = clicked;
                    var handler = Handler.Create(id);
                    long token;
                    // IToastNotification: add_Activated 11.
                    Check(((delegate* unmanaged[Stdcall]<nint, nint, long*, int>)Slot(toast, 11))(toast, handler, &token));
                    Release(handler);
                    SetTag(toast, tag);
                    // IToastNotifier: Show 6.
                    Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(notifier, 6))(notifier, toast));
                }
                catch
                {
                    Release(toast);
                    Release(notifier);
                    throw;
                }
                Shown[tag] = (notifier, toast);
                return true;
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException or InvalidCastException)
            {
                return false;
            }
        }
    }

    /// <summary>Takes down the toast shown under <paramref name="tag"/>, from the screen and the notification centre.</summary>
    public void Hide(string tag)
    {
        lock (Gate)
        {
            try { Take(tag); }
            catch (COMException) { }
        }
    }

    /// <summary>Every toast taken down and the app id's name and icon taken out of the registry: Sloperator leaves nothing there with them off.</summary>
    public void Forget()
    {
        lock (Gate)
        {
            foreach (var tag in Shown.Keys.ToList())
            {
                try { Take(tag); }
                catch (COMException) { }
            }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AppUserModelId\" + AppId, throwOnMissingSubKey: false); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
            _registeredIcon = null;
        }
    }

    private static void Take(string tag)
    {
        if (!Shown.Remove(tag, out var shown))
            return;
        try
        {
            // IToastNotifier: Hide 7. One that already went is no error worth a word.
            _ = ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(shown.Notifier, 7))(shown.Notifier, shown.Toast);
        }
        finally
        {
            Release(shown.Toast);
            Release(shown.Notifier);
        }
    }

    /// <summary>
    /// The name and icon Windows shows for <see cref="AppId"/>, written at the first toast of each
    /// start (the folder, and so the icon, may have moved), not before the user turned toasts on.
    /// </summary>
    private static void Register(string? iconPath)
    {
        if (_registeredIcon == (iconPath ?? ""))
            return;
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId);
        key.SetValue("DisplayName", "Sloperator");
        if (iconPath is not null)
            key.SetValue("IconUri", iconPath);
        _registeredIcon = iconPath ?? "";
    }

    private static string Xml(string title, string text) =>
        "<toast><visual><binding template=\"ToastGeneric\">" +
        $"<text hint-maxLines=\"1\">{System.Security.SecurityElement.Escape(title)}</text>" +
        $"<text>{System.Security.SecurityElement.Escape(text)}</text>" +
        "</binding></visual></toast>";

    // ---- WinRT -------------------------------------------------------------------------------

    private static readonly Guid IToastNotificationManagerStatics = new("50ac103f-d235-4598-bbef-98fe4d1a3ad4");
    private static readonly Guid IToastNotificationFactory = new("04124b20-82c6-4229-b109-fd9ed4662b53");
    private static readonly Guid IToastNotification2 = new("9dfb9fd1-143a-490e-90bf-b9fba7132de7");
    private static readonly Guid IXmlDocument = new("f7f3a506-1e87-42d6-bcfb-b8c809fa5494");
    private static readonly Guid IXmlDocumentIO = new("6cd0e74e-ee65-4489-9ebf-ca43e87ba637");

    private static nint CreateNotifier()
    {
        var statics = Factory("Windows.UI.Notifications.ToastNotificationManager", IToastNotificationManagerStatics);
        try
        {
            using var id = new HString(AppId);
            nint notifier;
            // IToastNotificationManagerStatics: CreateToastNotifierWithId 7.
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(statics, 7))(statics, id.Handle, &notifier));
            return notifier;
        }
        finally { Release(statics); }
    }

    private static nint CreateToast(string xml)
    {
        nint inspectable = 0, io = 0, document = 0, factory = 0;
        try
        {
            inspectable = Activate("Windows.Data.Xml.Dom.XmlDocument");
            io = Query(inspectable, IXmlDocumentIO);
            using (var text = new HString(xml))
                // IXmlDocumentIO: LoadXml 6.
                Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(io, 6))(io, text.Handle));
            document = Query(inspectable, IXmlDocument);
            factory = Factory("Windows.UI.Notifications.ToastNotification", IToastNotificationFactory);
            nint toast;
            // IToastNotificationFactory: CreateToastNotification 6.
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(factory, 6))(factory, document, &toast));
            return toast;
        }
        finally
        {
            Release(factory);
            Release(document);
            Release(io);
            Release(inspectable);
        }
    }

    /// <summary>A toast with the same tag replaces the last one in the notification centre too.</summary>
    private static void SetTag(nint toast, string tag)
    {
        var second = Query(toast, IToastNotification2);
        try
        {
            using var text = new HString(tag);
            // IToastNotification2: put_Tag 6.
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(second, 6))(second, text.Handle));
        }
        finally { Release(second); }
    }

    /// <summary>
    /// The toast's Activated handler, a TypedEventHandler&lt;ToastNotification, object&gt; made by
    /// hand: a vtable of four and a count, with the id of what the click does. Agile, so Windows
    /// calls it on its own thread.
    /// </summary>
    private struct Handler
    {
        private void** _vtable;
        private int _refs;
        private int _id;

        private static readonly Guid IUnknown = new("00000000-0000-0000-c000-000000000046");
        private static readonly Guid IAgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
        private static readonly Guid TypedEventHandler = new("ab54de2d-97d9-5528-b6ad-105afe156530");

        private static readonly void** Vtable = MakeVtable();

        private static void** MakeVtable()
        {
            var table = (void**)NativeMemory.Alloc((nuint)(4 * sizeof(void*)));
            table[0] = (delegate* unmanaged[Stdcall]<Handler*, Guid*, nint*, int>)&QueryInterface;
            table[1] = (delegate* unmanaged[Stdcall]<Handler*, uint>)&AddRef;
            table[2] = (delegate* unmanaged[Stdcall]<Handler*, uint>)&ReleaseHandler;
            table[3] = (delegate* unmanaged[Stdcall]<Handler*, nint, nint, int>)&Invoke;
            return table;
        }

        public static nint Create(int id)
        {
            var handler = (Handler*)NativeMemory.Alloc((nuint)sizeof(Handler));
            handler->_vtable = Vtable;
            handler->_refs = 1;
            handler->_id = id;
            return (nint)handler;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int QueryInterface(Handler* self, Guid* iid, nint* result)
        {
            if (*iid == IUnknown || *iid == IAgileObject || *iid == TypedEventHandler)
            {
                Interlocked.Increment(ref self->_refs);
                *result = (nint)self;
                return 0;
            }
            *result = 0;
            return unchecked((int)0x80004002); // E_NOINTERFACE
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRef(Handler* self) => (uint)Interlocked.Increment(ref self->_refs);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint ReleaseHandler(Handler* self)
        {
            var left = Interlocked.Decrement(ref self->_refs);
            if (left == 0)
            {
                Clicks.TryRemove(self->_id, out _);
                NativeMemory.Free(self);
            }
            return (uint)left;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Invoke(Handler* self, nint sender, nint args)
        {
            // Nothing may escape into Windows' thread.
            try
            {
                if (Clicks.TryGetValue(self->_id, out var clicked))
                    clicked();
            }
            catch (Exception) { }
            return 0;
        }
    }
}
