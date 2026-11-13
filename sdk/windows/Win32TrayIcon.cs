using System.Runtime.InteropServices;

namespace Neto.Sdk.Windows;

public sealed class Win32TrayIcon : IDisposable
{
    private const uint WM_USER = 0x0400;
    private const uint WM_TRAYICON = WM_USER + 42;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint MF_STRING = 0x00000000;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const int GWLP_WNDPROC = -4;
    private static readonly IntPtr IDI_APPLICATION = new(32512);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly IntPtr _hwnd;
    private readonly IntPtr _origWndProc;
    private readonly WndProcDelegate _wndProcDelegate;
    private readonly IntPtr _ownedIcon;
    private NOTIFYICONDATAW _nid;
    private bool _disposed;

    public bool Connected { get; set; }
    public bool SystemProxyEnabled { get; set; }
    public bool TunEnabled { get; set; }

    public event Action? ShowRequested;
    public event Action? ToggleConnectRequested;
    public event Action? ToggleSystemProxyRequested;
    public event Action? ToggleTunRequested;
    public event Action? ExitRequested;

    public Win32TrayIcon(IntPtr windowHandle, string tooltip)
    {
        _hwnd = windowHandle;
        _wndProcDelegate = SubclassWndProc;
        _origWndProc = SetWindowLongPtrW(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
        _ownedIcon = TryExtractProcessSmallIcon();

        _nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _ownedIcon != IntPtr.Zero ? _ownedIcon : LoadIconW(IntPtr.Zero, IDI_APPLICATION),
            szTip = TruncateTip(tooltip),
        };
        Shell_NotifyIconW(NIM_ADD, ref _nid);
    }

    private static IntPtr TryExtractProcessSmallIcon()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return IntPtr.Zero;
        return ExtractIconExW(exePath, 0, IntPtr.Zero, out var smallIcon, 1) > 0 ? smallIcon : IntPtr.Zero;
    }

    public void UpdateTooltip(string tooltip)
    {
        if (_disposed) return;
        _nid.szTip = TruncateTip(tooltip);
        _nid.uFlags = NIF_TIP;
        Shell_NotifyIconW(NIM_MODIFY, ref _nid);
    }

    private IntPtr SubclassWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYICON)
        {
            var mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg == WM_LBUTTONUP || mouseMsg == WM_LBUTTONDBLCLK)
            {
                ShowRequested?.Invoke();
                return IntPtr.Zero;
            }
            if (mouseMsg == WM_RBUTTONUP)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }
        }
        return CallWindowProcW(_origWndProc, hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;
        try
        {
            AppendMenuW(hMenu, MF_STRING, 1, "打开窗口");
            AppendMenuW(hMenu, MF_STRING, 2, Connected ? "断开连接" : "立即连接");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
            AppendMenuW(hMenu, MF_STRING | (SystemProxyEnabled ? MF_CHECKED : 0), 3, "系统代理");
            AppendMenuW(hMenu, MF_STRING | (TunEnabled ? MF_CHECKED : 0), 4, "TUN 接管");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
            AppendMenuW(hMenu, MF_STRING, 5, "退出");

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd);
            var cmd = TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            switch (cmd)
            {
                case 1: ShowRequested?.Invoke(); break;
                case 2: ToggleConnectRequested?.Invoke(); break;
                case 3: ToggleSystemProxyRequested?.Invoke(); break;
                case 4: ToggleTunRequested?.Invoke(); break;
                case 5: ExitRequested?.Invoke(); break;
            }
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    private static string TruncateTip(string text) =>
        text.Length > 120 ? text[..120] : text;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shell_NotifyIconW(NIM_DELETE, ref _nid);
        if (_ownedIcon != IntPtr.Zero)
        {
            DestroyIcon(_ownedIcon);
        }
        if (_origWndProc != IntPtr.Zero)
        {
            SetWindowLongPtrW(_hwnd, GWLP_WNDPROC, _origWndProc);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);
}
