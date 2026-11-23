using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace TaskHub.Platform.Windows.SystemInfo;

/// <summary>
/// Reads cameras' privacy control (KSPROPERTY_CAMERACONTROL_PRIVACY / UVC CT_PRIVACY_CONTROL): the state
/// of a privacy switch or electronic shutter, as reported by the camera itself (1 = closed / camera off).
/// This is what laptop makers' own apps use for their "camera off" overlays (e.g. Lenovo's side switch).
/// Reading it doesn't start the camera or turn its light on.
/// </summary>
internal static class CameraPrivacy
{
    private const int PrivacyProperty = 8; // KSPROPERTY_CAMERACONTROL_PRIVACY
    private static readonly Guid SystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
    private static readonly Guid VideoInputCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
    private static readonly Guid BaseFilter = new("56a86895-0ad4-11ce-b03a-0020af0ba770");

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig] int CreateClassEnumerator([In] ref Guid category, out IEnumMoniker? enumerator, int flags);
    }

    [ComImport, Guid("C6E13370-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAMCameraControl
    {
        [PreserveSig] int GetRange(int property, out int min, out int max, out int step, out int defaultValue, out int caps);
        [PreserveSig] int Set(int property, int value, int flags);
        [PreserveSig] int Get(int property, out int value, out int flags);
    }

    /// <summary>
    /// True if any camera reports its privacy switch/shutter closed, false if all that support it are open,
    /// null if no camera reports a privacy state (or it couldn't be read).
    /// </summary>
    public static bool? AnyClosed()
    {
        var type = Type.GetTypeFromCLSID(SystemDeviceEnum);
        if (type is null || Activator.CreateInstance(type) is not ICreateDevEnum devEnum) return null;
        IEnumMoniker? monikers = null;
        try
        {
            var category = VideoInputCategory;
            if (devEnum.CreateClassEnumerator(ref category, out monikers, 0) != 0 || monikers is null) return null;

            bool? result = null;
            var one = new IMoniker[1];
            while (monikers.Next(1, one, IntPtr.Zero) == 0)
            {
                var moniker = one[0];
                object? filter = null;
                try
                {
                    var iid = BaseFilter;
                    moniker.BindToObject(null!, null!, ref iid, out filter);
                    if (filter is IAMCameraControl control && control.Get(PrivacyProperty, out var value, out _) == 0)
                        result = (result ?? false) || value != 0;
                }
                catch (COMException)
                {
                    // camera busy or doesn't support it: skip
                }
                catch (InvalidCastException)
                {
                }
                finally
                {
                    if (filter is not null && Marshal.IsComObject(filter)) Marshal.ReleaseComObject(filter);
                    Marshal.ReleaseComObject(moniker);
                }
            }
            return result;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (monikers is not null) Marshal.ReleaseComObject(monikers);
            Marshal.ReleaseComObject(devEnum);
        }
    }
}
