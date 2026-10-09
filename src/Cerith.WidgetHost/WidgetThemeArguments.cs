#nullable enable

using System;
using System.Collections.Generic;

namespace Cerith.WidgetHost;

internal enum WidgetThemePreference
{
    System,
    Light,
    Dark
}

internal static class WidgetThemeArguments
{
    internal static bool TryParse(IEnumerable<string> arguments, out WidgetThemePreference preference)
    {
        preference = WidgetThemePreference.System;
        bool found = false;
        foreach (string argument in arguments)
        {
            if (string.Equals(argument, "--theme", StringComparison.OrdinalIgnoreCase))
            {
                preference = WidgetThemePreference.System;
                return false;
            }
            if (!argument.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))
                continue;
            if (found)
            {
                preference = WidgetThemePreference.System;
                return false;
            }
            found = true;
            switch (argument[8..].ToLowerInvariant())
            {
                case "system": preference = WidgetThemePreference.System; break;
                case "light": preference = WidgetThemePreference.Light; break;
                case "dark": preference = WidgetThemePreference.Dark; break;
                default:
                    preference = WidgetThemePreference.System;
                    return false;
            }
        }
        return true;
    }
}
