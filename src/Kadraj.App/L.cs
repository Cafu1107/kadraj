using System.Globalization;

namespace Kadraj;

/// <summary>
/// Minimal EN/TR text switch; follows the Windows display language.
/// KADRAJ_LANG=en or KADRAJ_LANG=tr overrides it.
/// </summary>
static class L
{
    static readonly bool Turkish =
        (Environment.GetEnvironmentVariable("KADRAJ_LANG") ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
        .StartsWith("tr", StringComparison.OrdinalIgnoreCase);

    public static string T(string en, string tr) => Turkish ? tr : en;
}
