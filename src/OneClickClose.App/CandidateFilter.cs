using System;

namespace OneClickClose
{
    public static class CandidateFilter
    {
        public static bool Matches(AppCandidate app, string searchText, bool showAllWindows)
        {
            return Matches(app, searchText, showAllWindows, false);
        }

        public static bool Matches(
            AppCandidate app,
            string searchText,
            bool showAllWindows,
            bool includeSuspendedUwp)
        {
            if (app == null)
            {
                return false;
            }
            if (!showAllWindows &&
                !app.IsTaskbarApp &&
                !(includeSuspendedUwp && app.IsSuspendedUwp))
            {
                return false;
            }

            string text = string.IsNullOrWhiteSpace(searchText) ? string.Empty : searchText.Trim();
            if (text.Length == 0)
            {
                return true;
            }

            string haystack = (app.DisplayName ?? string.Empty) + " " +
                (app.Name ?? string.Empty) + " " +
                (app.Titles ?? string.Empty);
            return haystack.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
