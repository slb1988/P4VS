using System;
using System.Collections.Generic;

namespace Perforce.P4VS
{
    /// <summary>
    /// Canonical built-in defaults for well-known preference keys.
    /// Values equal to these are NOT persisted to LocalSettings.ini; absence at
    /// load time falls back to the call-site default (GetBool/GetInt/GetString
    /// already do that). Keeps the ini limited to true user overrides, so a
    /// later change to a built-in default actually reaches everyone.
    /// </summary>
    internal static class PreferenceDefaults
    {
        private static readonly Dictionary<string, object> Defaults =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            { "LazyLoadStatus", true },
            { "PreloadScmCache", false },
            { "LazyLoadFullMenu", false },
            { "TreatProjectsAsFolders", true },
            { "AutoUpdateFileData", false },
            { "OptimizeSolutionLoad", false },
            { "Number_files_cache", 500 },
            { "Size_files", 500 },
            { "Number_specs", 100 },
        };

        /// <summary>
        /// True only when the key has a registered canonical default and the
        /// value equals it. Unknown keys and complex (IPreference) values always
        /// persist.
        /// </summary>
        public static bool IsDefaultValue(string key, object value)
        {
            object def;
            if (value == null || !Defaults.TryGetValue(key, out def))
            {
                return false;
            }

            if (def is bool && value is bool)
            {
                return (bool)def == (bool)value;
            }

            if (def is string && value is string)
            {
                return (string)def == (string)value;
            }

            if (IsInteger(def) && IsInteger(value))
            {
                return Convert.ToInt64(def) == Convert.ToInt64(value);
            }

            return false;
        }

        private static bool IsInteger(object o)
        {
            return o is int || o is long || o is short || o is byte;
        }
    }
}
