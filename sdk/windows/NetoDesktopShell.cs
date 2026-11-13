using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Neto.Sdk.Windows;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _wakeEvent;
    private readonly CancellationTokenSource _cts = new();
    private bool _ownsMutex;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle wakeEvent, Action onSecondInstance)
    {
        _mutex = mutex;
        _wakeEvent = wakeEvent;
        _ownsMutex = true;
        var token = _cts.Token;
        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { _wakeEvent, token.WaitHandle };
            while (!token.IsCancellationRequested)
            {
                var idx = WaitHandle.WaitAny(handles);
                if (idx == 0 && !token.IsCancellationRequested)
                {
                    onSecondInstance();
                }
                else
                {
                    break;
                }
            }
        })
        {
            IsBackground = true,
            Name = "NetoSingleInstanceListener",
        };
        thread.Start();
    }

    public static SingleInstanceGuard? TryAcquireOrWakeExisting(string appId, Action onSecondInstance)
    {
        var safeId = new string((string.IsNullOrWhiteSpace(appId) ? "Neto" : appId)
            .Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')
            .ToArray());
        if (string.IsNullOrEmpty(safeId)) safeId = "Neto";

        var mutexName = $"Local\\{safeId}.SingleInstance.Mutex";
        var eventName = $"Local\\{safeId}.SingleInstance.WakeEvent";
        var wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        var mutex = new Mutex(false, mutexName);

        var acquired = false;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero, false);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            wakeEvent.Set();
            wakeEvent.Dispose();
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceGuard(mutex, wakeEvent, onSecondInstance);
    }

    public void ReleaseMutexEarly()
    {
        if (!_ownsMutex) return;
        _ownsMutex = false;
        try { _mutex.ReleaseMutex(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        ReleaseMutexEarly();
        _wakeEvent.Dispose();
        _mutex.Dispose();
        _cts.Dispose();
    }
}

public static class NetoWindowHelper
{
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_NOCHANGEDIR = 0x00000008;

    public static void ConfigureWindow(AppWindow appWindow, int width, int height, int minWidth = 780, int minHeight = 560)
    {
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = minWidth;
            presenter.PreferredMinimumHeight = minHeight;
        }
        appWindow.Resize(new SizeInt32(width, height));
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        if (area != null)
        {
            var work = area.WorkArea;
            var x = work.X + Math.Max(0, (work.Width - width) / 2);
            var y = work.Y + Math.Max(0, (work.Height - height) / 2);
            appWindow.Move(new PointInt32(x, y));
        }
    }

    public static void RestoreAndActivate(Window window, AppWindow appWindow, IntPtr hwnd)
    {
        appWindow.Show();
        if (appWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }
        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }
        else
        {
            ShowWindow(hwnd, SW_SHOW);
        }
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        window.Activate();
    }

    public static bool IsWindowForegroundAndVisible(AppWindow appWindow, IntPtr hwnd)
    {
        return appWindow.IsVisible && !IsIconic(hwnd) && GetForegroundWindow() == hwnd;
    }

    public static void ApplyTheme(FrameworkElement? root, IntPtr hwnd, string? themeMode)
    {
        var normalized = (themeMode ?? "system").Trim().ToLowerInvariant();
        if (root != null)
        {
            root.RequestedTheme = normalized switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
        var isDark = normalized switch
        {
            "dark" => 1,
            "light" => 0,
            _ => Application.Current?.RequestedTheme == ApplicationTheme.Dark ? 1 : 0,
        };
        if (hwnd != IntPtr.Zero)
        {
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref isDark, sizeof(int));
        }
    }

    public static string BuildTerminalProxyCommand(NetoSnapshot snap, string shellFormat = "powershell")
    {
        var httpAddr = !string.IsNullOrWhiteSpace(snap.Connection.HttpProxyAddr)
            ? snap.Connection.HttpProxyAddr
            : $"127.0.0.1:{snap.Settings.MixedPort}";
        var socksAddr = !string.IsNullOrWhiteSpace(snap.Connection.SocksAddr)
            ? snap.Connection.SocksAddr
            : httpAddr;
        return shellFormat.Trim().ToLowerInvariant() switch
        {
            "cmd" => $"set HTTP_PROXY=http://{httpAddr}& set HTTPS_PROXY=http://{httpAddr}& set ALL_PROXY=socks5://{socksAddr}",
            "bash" => $"export http_proxy=http://{httpAddr} https_proxy=http://{httpAddr} all_proxy=socks5://{socksAddr}",
            _ => $"$env:HTTP_PROXY='http://{httpAddr}'; $env:HTTPS_PROXY='http://{httpAddr}'; $env:ALL_PROXY='socks5://{socksAddr}'",
        };
    }

    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    public static bool SetClipboardText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes);
            if (hGlobal == IntPtr.Zero) return false;
            var target = GlobalLock(hGlobal);
            if (target == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return false;
            }
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * 2, 0);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }
            if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return false;
            }
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static string? GetClipboardText()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return null;
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static string? PickOpenYamlFile(IntPtr ownerHwnd)
    {
        var buffer = new string('\0', 2048);
        var ofn = new OPENFILENAMEW
        {
            lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
            hwndOwner = ownerHwnd,
            lpstrFilter = "YAML 配置文件 (*.yaml;*.yml)\0*.yaml;*.yml\0所有文件 (*.*)\0*.*\0\0",
            lpstrFile = buffer,
            nMaxFile = buffer.Length,
            Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
        };
        if (!GetOpenFileNameW(ref ofn)) return null;
        var nulIdx = ofn.lpstrFile.IndexOf('\0');
        return nulIdx >= 0 ? ofn.lpstrFile[..nulIdx] : ofn.lpstrFile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAMEW
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public string lpstrFile;
        public int nMaxFile;
        public string? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAMEW lpofn);
}
