using System.Runtime.InteropServices;

namespace Neto.Sdk.Windows;

public sealed class Win32TrayIcon : IDisposable
{
    private const uint WM_NULL = 0x0000;
    private const uint WM_ENDSESSION = 0x0016;
    private const uint WM_USER = 0x0400;
    private const uint WM_TRAYICON = WM_USER + 42;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint MF_STRING = 0x00000000;
    private const uint MF_GRAYED = 0x00000001;
    private const uint MF_DISABLED = 0x00000002;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_POPUP = 0x00000010;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_BYCOMMAND = 0x00000000;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const int GWLP_WNDPROC = -4;
    private static readonly IntPtr IDI_APPLICATION = new(32512);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly IntPtr _hwnd;
    private readonly IntPtr _origWndProc;
    private readonly WndProcDelegate _wndProcDelegate;
    private readonly IntPtr _ownedIcon;
    private readonly uint _taskbarCreatedMsg;
    private NOTIFYICONDATAW _nid;
    private List<NetoNodeItem> _nodes = [];
    private int _selectedNodeIndex;
    private bool _disposed;

    public bool Connected { get; set; }
    public bool Connecting { get; set; }
    public bool AllowConnect { get; set; } = true;
    public bool SystemProxyEnabled { get; set; }
    public bool TunEnabled { get; set; }
    public bool CanUseTun { get; set; } = true;
    public string RoutingMode { get; set; } = "rule";
    public string StatusSummary { get; set; } = "";

    public event Action? ShowRequested;
    public event Action? PrimaryClickRequested;
    public event Action? ToggleConnectRequested;
    public event Action<string>? SelectRoutingModeRequested;
    public event Action<int>? SelectNodeRequested;
    public event Action? ToggleSystemProxyRequested;
    public event Action? ToggleTunRequested;
    public event Action? CopyProxyCommandRequested;
    public event Action? RestartElevatedRequested;
    public event Action? SessionEnding;
    public event Action? ExitRequested;

    public Win32TrayIcon(IntPtr windowHandle, string tooltip)
    {
        _hwnd = windowHandle;
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");
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

    public void SyncFromSnapshot(NetoSnapshot snap, string appTitle, bool allowConnect = true)
    {
        Connected = snap.Connection.Connected;
        Connecting = snap.Connection.Connecting;
        AllowConnect = allowConnect;
        SystemProxyEnabled = snap.Settings.SystemProxyEnabled;
        TunEnabled = snap.Settings.TunEnabled;
        CanUseTun = snap.Connection.CanUseTun;
        RoutingMode = snap.Settings.RoutingMode;
        _selectedNodeIndex = snap.SelectedNodeIndex;
        _nodes = snap.Nodes.Where(n => n.Supported).Take(30).ToList();

        if (snap.Connection.Connected)
        {
            var nodeLabel = string.IsNullOrWhiteSpace(snap.Connection.ActiveNodeName) ? "已连接" : snap.Connection.ActiveNodeName;
            var up = NetoFormat.FormatSpeed(snap.Telemetry.UploadSpeedBps);
            var down = NetoFormat.FormatSpeed(snap.Telemetry.DownloadSpeedBps);
            StatusSummary = $"{nodeLabel}  ↑{up} ↓{down}";
            UpdateTooltip($"{appTitle} · {nodeLabel}\n↑ {up}  ↓ {down}");
        }
        else
        {
            StatusSummary = "";
            UpdateTooltip(snap.Connection.Connecting ? $"{appTitle} · 连接中" : appTitle);
        }
    }

    private static IntPtr TryExtractProcessSmallIcon()
    {
        var assetIcon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(assetIcon))
        {
            var loaded = LoadImageW(IntPtr.Zero, assetIcon, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
            if (loaded != IntPtr.Zero) return loaded;
        }
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return IntPtr.Zero;
        return ExtractIconExW(exePath, 0, IntPtr.Zero, out var smallIcon, 1) > 0 ? smallIcon : IntPtr.Zero;
    }

    private static string EscapeMenuText(string text) => text.Replace("&", "&&");

    public void UpdateTooltip(string tooltip)
    {
        if (_disposed) return;
        _nid.szTip = TruncateTip(tooltip);
        _nid.uFlags = NIF_TIP;
        Shell_NotifyIconW(NIM_MODIFY, ref _nid);
    }

    private IntPtr SubclassWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg && !_disposed)
        {
            _nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            Shell_NotifyIconW(NIM_ADD, ref _nid);
            return IntPtr.Zero;
        }
        if (msg == WM_ENDSESSION && wParam != IntPtr.Zero)
        {
            SessionEnding?.Invoke();
        }
        if (msg == WM_TRAYICON)
        {
            var mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg == WM_LBUTTONDBLCLK)
            {
                ShowRequested?.Invoke();
                return IntPtr.Zero;
            }
            if (mouseMsg == WM_LBUTTONUP)
            {
                if (PrimaryClickRequested != null) PrimaryClickRequested.Invoke();
                else ShowRequested?.Invoke();
                return IntPtr.Zero;
            }
            if (mouseMsg == WM_RBUTTONUP || mouseMsg == WM_CONTEXTMENU)
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
        var nodeIndexLookup = new Dictionary<int, int>();
        try
        {
            AppendMenuW(hMenu, MF_STRING, 1, "打开主窗口");
            SetMenuDefaultItem(hMenu, 1, 0);

            if (Connected && !string.IsNullOrEmpty(StatusSummary))
            {
                AppendMenuW(hMenu, MF_STRING | MF_GRAYED | MF_DISABLED, 900, EscapeMenuText(StatusSummary));
            }

            var connectFlags = MF_STRING | ((!AllowConnect || Connecting) ? (MF_GRAYED | MF_DISABLED) : 0);
            var connectLabel = Connecting ? "连接中..." : (Connected ? "断开连接" : "立即连接");
            AppendMenuW(hMenu, connectFlags, 2, connectLabel);
            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);

            var hModeMenu = CreatePopupMenu();
            if (hModeMenu != IntPtr.Zero)
            {
                AppendMenuW(hModeMenu, MF_STRING, 10, "规则模式");
                AppendMenuW(hModeMenu, MF_STRING, 11, "全局模式");
                AppendMenuW(hModeMenu, MF_STRING, 12, "直连模式");
                var activeModeCmd = RoutingMode switch { "global" => 11u, "direct" => 12u, _ => 10u };
                CheckMenuRadioItem(hModeMenu, 10, 12, activeModeCmd, MF_BYCOMMAND);
                AppendMenuW(hMenu, MF_POPUP, (nuint)hModeMenu, "出站模式");
            }

            var hNodesMenu = CreatePopupMenu();
            if (hNodesMenu != IntPtr.Zero)
            {
                if (_nodes.Count == 0)
                {
                    AppendMenuW(hNodesMenu, MF_STRING | MF_GRAYED | MF_DISABLED, 999, "暂无可用节点");
                }
                else
                {
                    uint activeCmd = 0;
                    for (var i = 0; i < _nodes.Count; i++)
                    {
                        var n = _nodes[i];
                        var cmdId = (uint)(1000 + i);
                        nodeIndexLookup[(int)cmdId] = n.Index;
                        var label = n.LatencyMs.HasValue ? $"{n.Name}  ({n.LatencyMs.Value} ms)" : n.Name;
                        AppendMenuW(hNodesMenu, MF_STRING, cmdId, EscapeMenuText(label));
                        if (n.Index == _selectedNodeIndex) activeCmd = cmdId;
                    }
                    if (activeCmd != 0)
                    {
                        CheckMenuRadioItem(hNodesMenu, 1000, (uint)(1000 + _nodes.Count - 1), activeCmd, MF_BYCOMMAND);
                    }
                }
                AppendMenuW(hMenu, MF_POPUP, (nuint)hNodesMenu, "切换节点");
            }

            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
            AppendMenuW(hMenu, MF_STRING | (SystemProxyEnabled ? MF_CHECKED : 0), 20, "系统代理");
            AppendMenuW(hMenu, MF_STRING | (TunEnabled ? MF_CHECKED : 0), 21, CanUseTun ? "TUN 接管" : "TUN 接管（需管理员）");
            AppendMenuW(hMenu, MF_STRING, 22, "复制终端代理命令");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
            if (!CanUseTun)
            {
                AppendMenuW(hMenu, MF_STRING, 23, "以管理员身份重启");
            }
            AppendMenuW(hMenu, MF_STRING, 30, "退出");

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd);
            var cmd = TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (nodeIndexLookup.TryGetValue(cmd, out var targetNodeIndex))
            {
                SelectNodeRequested?.Invoke(targetNodeIndex);
                return;
            }

            switch (cmd)
            {
                case 1: ShowRequested?.Invoke(); break;
                case 2: ToggleConnectRequested?.Invoke(); break;
                case 10: SelectRoutingModeRequested?.Invoke("rule"); break;
                case 11: SelectRoutingModeRequested?.Invoke("global"); break;
                case 12: SelectRoutingModeRequested?.Invoke("direct"); break;
                case 20: ToggleSystemProxyRequested?.Invoke(); break;
                case 21: ToggleTunRequested?.Invoke(); break;
                case 22: CopyProxyCommandRequested?.Invoke(); break;
                case 23: RestartElevatedRequested?.Invoke(); break;
                case 30: ExitRequested?.Invoke(); break;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

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

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

    [DllImport("user32.dll")]
    private static extern bool CheckMenuRadioItem(IntPtr hmenu, uint first, uint last, uint check, uint flags);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);
}
