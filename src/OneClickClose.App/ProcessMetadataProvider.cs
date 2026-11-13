using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace OneClickClose
{
    public sealed class ProcessMetadata
    {
        public Dictionary<int, int> ParentMap { get; private set; }
        public Dictionary<int, string> PathMap { get; private set; }

        public ProcessMetadata()
        {
            ParentMap = new Dictionary<int, int>();
            PathMap = new Dictionary<int, string>();
        }
    }

    public static class ProcessMetadataProvider
    {
        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(
            IntPtr hProcess,
            uint dwFlags,
            StringBuilder lpExeName,
            ref uint lpdwSize);

        public static ProcessMetadata Get()
        {
            ProcessMetadata result = new ProcessMetadata();
            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            {
                return result;
            }

            try
            {
                PROCESSENTRY32 entry = new PROCESSENTRY32();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (!Process32First(snapshot, ref entry))
                {
                    return result;
                }

                do
                {
                    int processId = (int)entry.th32ProcessID;
                    if (processId <= 0)
                    {
                        continue;
                    }

                    result.ParentMap[processId] = (int)entry.th32ParentProcessID;
                    if (processId > 4)
                    {
                        string path = QueryPath(processId);
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            result.PathMap[processId] = path;
                        }
                    }
                    entry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                }
                while (Process32Next(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }
            return result;
        }

        public static string GetProcessPath(int processId)
        {
            return QueryPath(processId);
        }

        private static string QueryPath(int processId)
        {
            if (processId <= 0)
            {
                return string.Empty;
            }

            IntPtr processHandle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (processHandle == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                uint bufferSize = 1024;
                StringBuilder buffer = new StringBuilder((int)bufferSize);
                if (QueryFullProcessImageName(processHandle, 0, buffer, ref bufferSize))
                {
                    return buffer.ToString();
                }
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }
    }
}
