using System.Globalization;
using System.Text.RegularExpressions;

namespace Numbering.Core;

public sealed class NumberTemplate
{
    private static readonly Regex Tokens = new(@"\{[^{}]+\}|[{}]", RegexOptions.Compiled);

    private readonly string _pattern;
    private readonly string _numberFormat;

    private NumberTemplate(string pattern, int width)
    {
        _pattern = pattern;
        _numberFormat = "D" + width.ToString(CultureInfo.InvariantCulture);
    }

    public static bool TryParse(string pattern, out NumberTemplate? template, out string? error)
    {
        template = null;
        error = null;
        int width = 0;

        foreach (Match match in Tokens.Matches(pattern))
        {
            string token = match.Value;
            if (token is "{Префикс}" or "{Уровень}")
                continue;

            if (token.Length == 1)
            {
                error = "Проверьте парность фигурных скобок.";
                return false;
            }

            if (token.StartsWith("{N:", StringComparison.Ordinal))
            {
                int length = token.Length - 4;
                if (length < 1 || length > 9 || token.Substring(3, length).Any(c => c != '0'))
                {
                    error = "Укажите от 1 до 9 нулей, например {N:000}.";
                    return false;
                }

                if (width != 0)
                {
                    error = "Подстановка номера должна встречаться один раз.";
                    return false;
                }

                width = length;
                continue;
            }

            error = "Неизвестная подстановка " + token + ".";
            return false;
        }

        if (width == 0)
        {
            error = "Добавьте номер в шаблон, например {N:000}.";
            return false;
        }

        template = new NumberTemplate(pattern, width);
        return true;
    }

    public string Render(string prefix, string level, int number) =>
        Tokens.Replace(_pattern, match => match.Value switch
        {
            "{Префикс}" => prefix,
            "{Уровень}" => level,
            _ => number.ToString(_numberFormat, CultureInfo.InvariantCulture)
        });
}
