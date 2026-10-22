using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace ThermoSync;

/// <summary>
/// Manages Windows System Tray (Taskbar Notification Area) icon and interactions using Win32 API.
/// Does not require WinForms references, preventing WPF symbol ambiguities.
/// </summary>
public sealed class TrayManager : IDisposable
{
    private const int WM_USER = 0x0400;
    public const int WM_TRAY_CALLBACK = WM_USER + 1024;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;

    private const int NIIF_INFO = 0x00000001;
    private const int NIIF_WARNING = 0x00000002;
    private const int NIIF_ERROR = 0x00000003;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly Window _window;
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private IntPtr _hIcon = IntPtr.Zero;
    private bool _added;
    private ContextMenu? _contextMenu;

    public event Action? OnOpenRequested;

    public TrayManager(Window window)
    {
        _window = window;
    }

    public void Initialize(ContextMenu contextMenu)
    {
        _contextMenu = contextMenu;
        _hwnd = new WindowInteropHelper(_window).EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        LoadIcon();
        CreateTrayIcon("ThermoSync - Monitor de Hardware & Homelab");
    }

    private void LoadIcon()
    {
        try
        {
            string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(icoPath))
            {
                _hIcon = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
            }
        }
        catch { }
    }

    private void CreateTrayIcon(string tooltip)
    {
        if (_hwnd == IntPtr.Zero) return;

        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY_CALLBACK,
            hIcon = _hIcon,
            szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip
        };

        _added = Shell_NotifyIcon(NIM_ADD, ref nid);
    }

    public void UpdateTooltip(string tooltip)
    {
        if (!_added || _hwnd == IntPtr.Zero) return;

        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = NIF_TIP,
            szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip
        };

        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    public void ShowBalloon(string title, string message, int severity = 1)
    {
        if (!_added || _hwnd == IntPtr.Zero) return;

        int infoFlags = severity switch
        {
            2 => NIIF_WARNING,
            3 => NIIF_ERROR,
            _ => NIIF_INFO
        };

        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = NIF_INFO,
            szInfoTitle = title.Length > 63 ? title[..63] : title,
            szInfo = message.Length > 255 ? message[..255] : message,
            dwInfoFlags = infoFlags,
            uTimeoutOrVersion = 3000
        };

        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAY_CALLBACK)
        {
            int action = lParam.ToInt32();
            if (action is WM_LBUTTONDBLCLK or WM_LBUTTONUP)
            {
                OnOpenRequested?.Invoke();
                handled = true;
            }
            else if (action == WM_RBUTTONUP)
            {
                if (_contextMenu != null)
                {
                    SetForegroundWindow(_hwnd);
                    _contextMenu.IsOpen = true;
                }
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_added && _hwnd != IntPtr.Zero)
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1001
            };
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _added = false;
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }
}
