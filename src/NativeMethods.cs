using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RoMulti
{
    internal static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr icon);

        public const uint PROCESS_DUP_HANDLE = 0x0040;
        public const uint PROCESS_SET_QUOTA = 0x0100;
        public const uint PROCESS_SET_INFORMATION = 0x0200;
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint DUPLICATE_CLOSE_SOURCE = 0x00000001;

        public const int SystemExtendedHandleInformation = 64;
        public const int ObjectNameInformation = 1;
        public const int ObjectTypeInformation = 2;

        public const uint STATUS_SUCCESS = 0x00000000;
        public const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_SHOW = 5;
        public const int SW_MINIMIZE = 6;
        public const int SW_RESTORE = 9;

        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        public const uint PW_CLIENTONLY = 0x00000001;
        public const uint PW_RENDERFULLCONTENT = 0x00000002;

        public const uint WM_MOUSEMOVE = 0x0200;

        public const uint INPUT_MOUSE = 0;
        public const uint INPUT_KEYBOARD = 1;
        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_SCANCODE = 0x0008;
        public const uint ASFW_ANY = 0xFFFFFFFF;
        public const uint PM_NOREMOVE = 0x0000;

        public const int ProcessPowerThrottling = 4;
        public const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

        private const int DWMWA_CLOAKED = 14;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        public const uint TOKEN_QUERY = 0x0008;
        public const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
        {
            public IntPtr Object;
            public UIntPtr UniqueProcessId;
            public UIntPtr HandleValue;
            public uint GrantedAccess;
            public ushort CreatorBackTraceIndex;
            public ushort ObjectTypeIndex;
            public uint HandleAttributes;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("ntdll.dll")]
        public static extern uint NtQuerySystemInformation(
            int SystemInformationClass,
            IntPtr SystemInformation,
            int SystemInformationLength,
            out int ReturnLength);

        [DllImport("ntdll.dll")]
        public static extern uint NtQueryObject(
            IntPtr ObjectHandle,
            int ObjectInformationClass,
            IntPtr ObjectInformation,
            int ObjectInformationLength,
            out int ReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(
            uint dwDesiredAccess,
            bool bInheritHandle,
            int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DuplicateHandle(
            IntPtr hSourceProcessHandle,
            IntPtr hSourceHandle,
            IntPtr hTargetProcessHandle,
            out IntPtr lpTargetHandle,
            uint dwDesiredAccess,
            bool bInheritHandle,
            uint dwOptions);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(
            IntPtr hProcess,
            uint dwFlags,
            StringBuilder lpExeName,
            ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessInformation(
            IntPtr hProcess,
            int ProcessInformationClass,
            ref PROCESS_POWER_THROTTLING_STATE ProcessInformation,
            int ProcessInformationSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool SetWindowText(IntPtr hWnd, string lpString);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(uint dwProcessId);

        [DllImport("user32.dll")]
        public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int attrValue, int attrSize);

        public static bool IsWindowCloaked(IntPtr hwnd)
        {
            int flags;
            return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out flags, sizeof(int)) == 0 && flags != 0;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool AdjustTokenPrivileges(
            IntPtr TokenHandle,
            bool DisableAllPrivileges,
            ref TOKEN_PRIVILEGES NewState,
            uint BufferLength,
            IntPtr PreviousState,
            IntPtr ReturnLength);

        public static void SetTitleBarTheme(IntPtr hwnd, bool darkTheme)
        {
            if (hwnd == IntPtr.Zero) return;
            int dark = darkTheme ? 1 : 0;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            if (hr != 0)
            {
                hr = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref dark, sizeof(int));
            }
            if (hr == 0)
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        public static bool TryEnableDebugPrivilege()
        {
            IntPtr hToken = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out hToken))
                    return false;

                LUID luid;
                if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out luid))
                    return false;

                var tp = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED
                };

                return AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)
                    && Marshal.GetLastWin32Error() == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (hToken != IntPtr.Zero) CloseHandle(hToken);
            }
        }
    }
}
