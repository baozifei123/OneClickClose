using System;
using System.Collections.Generic;

namespace OneClickClose
{
    public static class ProcessSafety
    {
        private static readonly string[] HardProtectedNamesArray =
        {
            "system", "registry", "memory compression", "idle",
            "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
            "svchost", "dwm", "fontdrvhost", "sihost", "taskhostw", "ctfmon",
            "textinputhost", "explorer", "searchhost", "startmenuexperiencehost",
            "shellexperiencehost", "runtimebroker", "securityhealthservice",
            "securityhealthsystray", "msmpeng", "nissrv", "taskmgr", "wmiprvse",
            "wudfhost", "sogouinput", "sogoucloud", "qqpinyin", "baidupinyin", "wetype",
            "dllhost", "unsecapp", "lockapp", "systemsettingsbroker", "openconsole",
            "conhost", "rtkauduservice64", "nvcontainer", "windowspackagemanagerserver",
            "widgets", "widgetservice"
        };

        public static readonly HashSet<string> HardProtectedNames =
            new HashSet<string>(HardProtectedNamesArray, StringComparer.OrdinalIgnoreCase);

        public static bool IsHardProtected(string processName)
        {
            return HardProtectedNames.Contains(ProcessNameUtility.Normalize(processName));
        }
    }
}
