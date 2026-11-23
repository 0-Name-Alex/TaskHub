# Camera key check, round 2: asks the camera itself for its privacy state, and records which
# key code the camera key sends. About a minute.
$out = Join-Path $PSScriptRoot "camera-check.txt"

Add-Type -ReferencedAssemblies System.Windows.Forms -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class CamCheck
{
    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICreateDevEnum { [PreserveSig] int CreateClassEnumerator([In] ref Guid clsid, out IEnumMoniker e, int flags); }

    [ComImport, Guid("C6E13370-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAMCameraControl
    {
        [PreserveSig] int GetRange(int p, out int min, out int max, out int step, out int def, out int caps);
        [PreserveSig] int Set(int p, int value, int flags);
        [PreserveSig] int Get(int p, out int value, out int flags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string name, out object value, IntPtr log);
        [PreserveSig] int Write(string name, ref object value);
    }

    public static string Privacy()
    {
        var lines = new List<string>();
        var devEnum = (ICreateDevEnum)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86")));
        var category = new Guid("860BB310-5D01-11d0-BD3B-00A0C911CE86");
        IEnumMoniker e;
        if (devEnum.CreateClassEnumerator(ref category, out e, 0) != 0 || e == null) return "no video devices";
        var m = new IMoniker[1];
        while (e.Next(1, m, IntPtr.Zero) == 0)
        {
            string name = "?";
            try
            {
                object bag; var bagId = typeof(IPropertyBag).GUID;
                m[0].BindToStorage(null, null, ref bagId, out bag);
                object v; ((IPropertyBag)bag).Read("FriendlyName", out v, IntPtr.Zero); name = "" + v;
            }
            catch (Exception) { }
            try
            {
                object filter; var baseFilter = new Guid("56a86895-0ad4-11ce-b03a-0020af0ba770");
                m[0].BindToObject(null, null, ref baseFilter, out filter);
                var cc = filter as IAMCameraControl;
                if (cc == null) { lines.Add(name + ": no camera controls"); }
                else
                {
                    int min, max, step, def, caps, value, flags;
                    int hrRange = cc.GetRange(8, out min, out max, out step, out def, out caps);
                    int hr = cc.Get(8, out value, out flags);
                    lines.Add(string.Format("{0}: privacy range hr=0x{1:X8} [{2}..{3}] get hr=0x{4:X8} value={5} flags={6}", name, hrRange, min, max, hr, value, flags));
                }
                Marshal.ReleaseComObject(filter);
            }
            catch (Exception ex) { lines.Add(name + ": " + ex.Message); }
            Marshal.ReleaseComObject(m[0]);
        }
        return string.Join("\n", lines.ToArray());
    }

    // ---- which key the camera key sends
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct KbdLL { public uint vk, scan, flags, time; public IntPtr extra; }
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    static List<string> keys = new List<string>();
    static HookProc proc = Hook;
    static IntPtr Hook(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            var k = (KbdLL)Marshal.PtrToStructure(l, typeof(KbdLL));
            keys.Add(string.Format("msg=0x{0:X} vk=0x{1:X2} scan=0x{2:X2} flags=0x{3:X}", (int)w, k.vk, k.scan, k.flags));
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }
    public static string Keys(int seconds)
    {
        keys.Clear();
        var h = SetWindowsHookEx(13, proc, GetModuleHandle(null), 0);
        var until = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < until) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); }
        UnhookWindowsHookEx(h);
        return keys.Count == 0 ? "(no key events seen)" : string.Join("\n", keys.ToArray());
    }
}
"@

$cs = Get-CimInstance Win32_ComputerSystem
"=== $(Get-Date -Format s)  $($cs.Manufacturer) $($cs.Model)" | Out-File $out -Encoding utf8

Write-Host "Step 1: make sure the camera is ON, then press Enter." -ForegroundColor Yellow
Read-Host | Out-Null
"--- camera ON:`n$([CamCheck]::Privacy())" | Out-File $out -Append -Encoding utf8

"--- Lenovo WMI classes:" | Out-File $out -Append -Encoding utf8
Get-CimClass -Namespace root\wmi -ErrorAction SilentlyContinue | Where-Object { $_.CimClassName -match 'lenovo|ideapad|vpc|camera|privacy' } |
    ForEach-Object { $_.CimClassName } | Out-File $out -Append -Encoding utf8
Register-WmiEvent -Namespace root\wmi -Query "SELECT * FROM __ExtrinsicEvent" -SourceIdentifier CamWmi -ErrorAction SilentlyContinue | Out-Null

Write-Host "Step 2: within the next 8 seconds, slide the camera SWITCH to OFF (once)." -ForegroundColor Yellow
"--- keys while using the camera switch:`n$([CamCheck]::Keys(8))" | Out-File $out -Append -Encoding utf8
"--- WMI events while using the camera switch:" | Out-File $out -Append -Encoding utf8
Get-Event -SourceIdentifier CamWmi -ErrorAction SilentlyContinue | ForEach-Object {
    $ev = $_.SourceEventArgs.NewEvent
    "$($ev.__CLASS): " + (($ev.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join '; ')
} | Out-File $out -Append -Encoding utf8
Unregister-Event -SourceIdentifier CamWmi -ErrorAction SilentlyContinue
"--- Lenovo processes:" | Out-File $out -Append -Encoding utf8
Get-Process | Where-Object { $_.Name -match 'lenovo|vantage|utility|hotkey|camera' } | ForEach-Object { "$($_.Name) $($_.Path)" } | Out-File $out -Append -Encoding utf8
Start-Sleep -Seconds 2
"--- camera OFF:`n$([CamCheck]::Privacy())" | Out-File $out -Append -Encoding utf8

Write-Host "Step 3: slide the camera switch back to ON, wait 3 seconds, then press Enter." -ForegroundColor Yellow
Read-Host | Out-Null
"--- camera ON again:`n$([CamCheck]::Privacy())" | Out-File $out -Append -Encoding utf8
"=== done" | Out-File $out -Append -Encoding utf8

Write-Host ""
Write-Host "Done! Go back to Claude and type: done" -ForegroundColor Green
Read-Host "Press Enter to close" | Out-Null
