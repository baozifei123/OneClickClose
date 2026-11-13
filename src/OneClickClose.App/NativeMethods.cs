using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace OneClickClose
{
    public static class NativeMethods
    {
        public const int WM_CLOSE = 0x0010;
        public const int WM_HOTKEY = 0x0312;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const uint GW_OWNER = 4;
        public const int DWMWA_CLOAKED = 14;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hWnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        public static bool IsTaskbarEligibleWindow(IntPtr hWnd)
        {
            bool taskbarEligible;
            bool cloakedUwp;
            GetWindowFlags(hWnd, out taskbarEligible, out cloakedUwp);
            return taskbarEligible;
        }

        public static bool IsCloakedUwpWindow(IntPtr hWnd)
        {
            bool taskbarEligible;
            bool cloakedUwp;
            GetWindowFlags(hWnd, out taskbarEligible, out cloakedUwp);
            return cloakedUwp;
        }

        private static void GetWindowFlags(
            IntPtr hWnd,
            out bool taskbarEligible,
            out bool cloakedUwp)
        {
            taskbarEligible = false;
            cloakedUwp = false;
            if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd))
            {
                return;
            }
            if (GetWindowTextLength(hWnd) <= 0)
            {
                return;
            }

            int extendedStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            bool appWindow = (extendedStyle & WS_EX_APPWINDOW) != 0;
            bool toolWindow = (extendedStyle & WS_EX_TOOLWINDOW) != 0;
            bool hasOwner = GetWindow(hWnd, GW_OWNER) != IntPtr.Zero;

            bool cloaked = false;
            try
            {
                int cloakedValue;
                if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloakedValue, 4) == 0 &&
                    cloakedValue != 0)
                {
                    cloaked = true;
                }
            }
            catch
            {
            }

            StringBuilder className = new StringBuilder(256);
            GetClassName(hWnd, className, className.Capacity);
            string windowClass = className.ToString();
            bool shellWindow =
                string.Equals(windowClass, "Shell_TrayWnd", StringComparison.Ordinal) ||
                string.Equals(windowClass, "Shell_SecondaryTrayWnd", StringComparison.Ordinal) ||
                string.Equals(windowClass, "Progman", StringComparison.Ordinal) ||
                string.Equals(windowClass, "WorkerW", StringComparison.Ordinal);

            cloakedUwp = cloaked &&
                (string.Equals(windowClass, "ApplicationFrameWindow", StringComparison.Ordinal) ||
                 string.Equals(windowClass, "Windows.UI.Core.CoreWindow", StringComparison.Ordinal));

            taskbarEligible =
                !(toolWindow && !appWindow) &&
                !(hasOwner && !appWindow) &&
                !cloaked &&
                !shellWindow;
        }

        public static List<WindowInfo> GetVisibleWindows()
        {
            List<WindowInfo> result = new List<WindowInfo>();
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hWnd))
                {
                    return true;
                }

                int length = GetWindowTextLength(hWnd);
                if (length <= 0)
                {
                    return true;
                }

                StringBuilder builder = new StringBuilder(length + 1);
                GetWindowText(hWnd, builder, builder.Capacity);
                string title = builder.ToString();
                if (string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                uint processId;
                GetWindowThreadProcessId(hWnd, out processId);

                bool taskbarEligible;
                bool cloakedUwp;
                GetWindowFlags(hWnd, out taskbarEligible, out cloakedUwp);
                result.Add(new WindowInfo
                {
                    Handle = hWnd,
                    Title = title,
                    ProcessId = processId,
                    IsMinimized = IsIconic(hWnd),
                    IsTaskbarWindow = taskbarEligible,
                    IsCloakedUwp = cloakedUwp
                });
                return true;
            }, IntPtr.Zero);
            return result;
        }
    }

    public sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int HotKeyId = 0x4150;
        private readonly HotkeyDefinition _definition;
        private bool _registered;
        private bool _disposed;

        public event EventHandler HotkeyPressed;

        public bool Registered
        {
            get { return _registered; }
        }

        public HotkeyWindow(HotkeyDefinition definition)
        {
            _definition = definition ?? HotkeyDefinition.Default;
            CreateHandle(new CreateParams { Caption = "OneClickClose.Hotkey" });
            _registered = NativeMethods.RegisterHotKey(
                Handle,
                HotKeyId,
                _definition.Modifiers,
                _definition.VirtualKey);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HotKeyId)
            {
                EventHandler handler = HotkeyPressed;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_registered)
            {
                try { NativeMethods.UnregisterHotKey(Handle, HotKeyId); }
                catch { }
                _registered = false;
            }
            try { DestroyHandle(); }
            catch { }
        }
    }
}
