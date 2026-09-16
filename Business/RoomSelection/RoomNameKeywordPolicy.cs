using System;
using System.Collections.Generic;
using System.Linq;

namespace FCUAutoDesign.Business.RoomSelection
{
    internal static class RoomNameKeywordPolicy
    {
        private static readonly char[] Separators = { ';', '；', ',', '，', '\r', '\n' };

        public static IList<string> Parse(string value)
        {
            return (value ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static bool Matches(string roomName, IEnumerable<string> keywords)
        {
            if (string.IsNullOrWhiteSpace(roomName) || keywords == null) return false;
            return keywords.Any(keyword => !string.IsNullOrWhiteSpace(keyword)
                && roomName.IndexOf(keyword.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
