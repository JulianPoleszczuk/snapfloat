using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using SnapFloat.Core.Diagnostics;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace SnapFloat.Interop;

/// <summary>
/// Starts a native OLE drag of a real file on disk.
///
/// The data object is obtained from the Shell (IShellItem → BHID_DataObject), i.e. exactly the object Explorer
/// itself uses when you drag a file. It carries CF_HDROP, the Shell ID list and FileContents, so every target that
/// accepts a file dropped from Explorer (Windows Terminal, VS Code, browsers, chat apps, Explorer) accepts ours.
/// Only DROPEFFECT_COPY is offered, so Explorer can never move the screenshot away.
/// </summary>
internal static class FileDragSource
{
    public const int DROPEFFECT_NONE = 0;
    public const int DROPEFFECT_COPY = 1;

    private const int DRAGDROP_S_DROP = 0x00040100;
    private const int DRAGDROP_S_CANCEL = 0x00040101;
    private const int DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;
    private const int MK_LBUTTON = 0x0001;
    private const int DSH_ALLOWDROPDESCRIPTIONTEXT = 0x0001;

    private static readonly Guid BHID_DataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");
    private static readonly Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid CLSID_DragDropHelper = new("4657278A-411B-11D2-839A-00C04FD918D0");

    /// <summary>Result of a drag: the effect the target performed (COPY) or NONE when cancelled.</summary>
    public readonly record struct DragResult(bool Dropped, bool UsedShellDataObject);

    /// <summary>
    /// Runs a modal drag loop (messages keep pumping). Must be called on an STA UI thread while the left mouse
    /// button is down. <paramref name="dragImage"/> is an optional premultiplied 32-bpp HBITMAP whose ownership
    /// passes to the shell; <paramref name="cursorOffset"/> is where the cursor sits inside that image.
    /// </summary>
    public static DragResult DoFileDrag(string path, IntPtr dragImage, System.Drawing.Size imageSize, System.Drawing.Point cursorOffset)
    {
        ComIDataObject? data = null;
        var shell = false;
        try
        {
            data = CreateShellDataObject(path);
            shell = data is not null;
        }
        catch (Exception ex)
        {
            Log.Warn("Drag", "Shell data object unavailable, falling back", ("error", ex.GetType().Name));
        }

        if (data is null)
        {
            // Fallback: a WPF data object with CF_HDROP. WPF's object implements the COM IDataObject too.
            var wpf = new System.Windows.DataObject();
            wpf.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
            data = (ComIDataObject)wpf;
        }

        TrySetPreferredDropEffect(data, DROPEFFECT_COPY);

        var imageOwned = false;
        if (dragImage != IntPtr.Zero && shell)
            imageOwned = TryAttachDragImage(data, dragImage, imageSize, cursorOffset);
        if (!imageOwned && dragImage != IntPtr.Zero)
            Native.DeleteObject(dragImage);

        var hr = DoDragDrop(data, new DropSource(), DROPEFFECT_COPY, out var effect);
        Log.Info("Drag", "Drag finished", ("hr", $"0x{hr:X8}"), ("effect", effect), ("shellObject", shell));
        if (data is not null && Marshal.IsComObject(data)) Marshal.ReleaseComObject(data);
        return new DragResult(hr == DRAGDROP_S_DROP && effect != DROPEFFECT_NONE, shell);
    }

    private static ComIDataObject? CreateShellDataObject(string path)
    {
        var iidItem = IID_IShellItem;
        var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidItem, out var itemObj);
        if (hr != 0 || itemObj is null) return null;
        var item = (IShellItem)itemObj;
        try
        {
            var bhid = BHID_DataObject;
            var iid = IID_IDataObject;
            hr = item.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out var dataObj);
            return hr == 0 ? dataObj as ComIDataObject : null;
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    private static void TrySetPreferredDropEffect(ComIDataObject data, int effect)
    {
        try
        {
            var format = (short)Native.RegisterClipboardFormat("Preferred DropEffect");
            var hglobal = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(hglobal, effect);
            var fe = new FORMATETC { cfFormat = format, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
            var sm = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = hglobal };
            data.SetData(ref fe, ref sm, true); // data object takes ownership of the HGLOBAL
        }
        catch (Exception ex) when (ex is COMException or NotImplementedException or NotSupportedException)
        {
            Log.Debug("Drag", "Preferred DropEffect not set", ("error", ex.GetType().Name));
        }
    }

    private static bool TryAttachDragImage(ComIDataObject data, IntPtr hbitmap, System.Drawing.Size size, System.Drawing.Point offset)
    {
        object? helperObj = null;
        try
        {
            helperObj = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_DragDropHelper)!);
            if (helperObj is IDragSourceHelper2 helper2)
                helper2.SetFlags(DSH_ALLOWDROPDESCRIPTIONTEXT);
            var helper = (IDragSourceHelper)helperObj!;
            var image = new SHDRAGIMAGE
            {
                sizeDragImage = new Native.POINT(size.Width, size.Height),
                ptOffset = new Native.POINT(offset.X, offset.Y),
                hbmpDragImage = hbitmap,
                crColorKey = unchecked((int)0xFFFFFFFF), // CLR_NONE: rely on the alpha channel
            };
            var hr = helper.InitializeFromBitmap(ref image, data);
            return hr == 0; // on success the shell owns the bitmap
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Debug("Drag", "Drag image not attached", ("error", ex.GetType().Name));
            return false;
        }
        finally
        {
            if (helperObj is not null && Marshal.IsComObject(helperObj)) Marshal.ReleaseComObject(helperObj);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int DoDragDrop(ComIDataObject pDataObj, IOleDropSource pDropSource, int dwOKEffects, out int pdwEffect);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? item);

    [StructLayout(LayoutKind.Sequential)]
    private struct SHDRAGIMAGE
    {
        public Native.POINT sizeDragImage;
        public Native.POINT ptOffset;
        public IntPtr hbmpDragImage;
        public int crColorKey;
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? ppv);
        // Remaining IShellItem methods are not needed and intentionally omitted (they follow in the vtable).
    }

    [ComImport, Guid("DE5BF786-477A-11D2-839D-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDragSourceHelper
    {
        [PreserveSig] int InitializeFromBitmap(ref SHDRAGIMAGE pshdi, ComIDataObject pDataObject);
        [PreserveSig] int InitializeFromWindow(IntPtr hwnd, ref Native.POINT ppt, ComIDataObject pDataObject);
    }

    [ComImport, Guid("83E07D0D-0C5F-4163-BF1A-60B274051E40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDragSourceHelper2
    {
        [PreserveSig] int InitializeFromBitmap(ref SHDRAGIMAGE pshdi, ComIDataObject pDataObject);
        [PreserveSig] int InitializeFromWindow(IntPtr hwnd, ref Native.POINT ppt, ComIDataObject pDataObject);
        [PreserveSig] int SetFlags(int dwFlags);
    }

    [ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleDropSource
    {
        [PreserveSig] int QueryContinueDrag(int fEscapePressed, int grfKeyState);
        [PreserveSig] int GiveFeedback(int dwEffect);
    }

    [ComVisible(true)]
    private sealed class DropSource : IOleDropSource
    {
        public int QueryContinueDrag(int fEscapePressed, int grfKeyState)
        {
            if (fEscapePressed != 0) return DRAGDROP_S_CANCEL;
            if ((grfKeyState & MK_LBUTTON) == 0) return DRAGDROP_S_DROP;
            return 0; // S_OK: keep dragging
        }

        public int GiveFeedback(int dwEffect) => DRAGDROP_S_USEDEFAULTCURSORS;
    }
}
