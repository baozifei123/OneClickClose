using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OneClickClose
{
    public static class ProcessPrivilege
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenElevation = 20;

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_ELEVATION
        {
            public int TokenIsElevated;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            int tokenInformationClass,
            out TOKEN_ELEVATION tokenInformation,
            int tokenInformationLength,
            out int returnLength);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        public static bool IsCurrentProcessElevated()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool IsProcessElevated(int processId)
        {
            if (processId <= 0)
            {
                return false;
            }

            IntPtr processHandle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (processHandle == IntPtr.Zero)
            {
                return false;
            }

            IntPtr tokenHandle = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(processHandle, TOKEN_QUERY, out tokenHandle))
                {
                    return false;
                }

                TOKEN_ELEVATION elevation;
                int returnedSize;
                int size = Marshal.SizeOf(typeof(TOKEN_ELEVATION));
                if (!GetTokenInformation(tokenHandle, TokenElevation, out elevation, size, out returnedSize))
                {
                    return false;
                }
                return elevation.TokenIsElevated != 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (tokenHandle != IntPtr.Zero)
                {
                    CloseHandle(tokenHandle);
                }
                CloseHandle(processHandle);
            }
        }
    }
}
