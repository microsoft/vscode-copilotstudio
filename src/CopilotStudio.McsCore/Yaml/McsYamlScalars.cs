// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Globalization;

namespace Microsoft.CopilotStudio.McsCore.Yaml;

/// <summary>Scalar spellings shared by the reader, the writer and the object mapper.</summary>
internal static class McsYamlScalars
{
    public static bool IsNullLiteral(string text) => text.Length == 1
        ? text[0] == '~'
        : string.Equals(text, "null", StringComparison.Ordinal) || string.Equals(text, "Null", StringComparison.Ordinal) || string.Equals(text, "NULL", StringComparison.Ordinal);

    public static bool TryParseBoolean(string text, out bool value)
    {
        switch (text)
        {
            case "y":
            case "Y":
            case "true":
            case "True":
            case "TRUE":
            case "yes":
            case "Yes":
            case "YES":
            case "on":
            case "On":
            case "ON":
                value = true;
                return true;
            case "n":
            case "N":
            case "false":
            case "False":
            case "FALSE":
            case "no":
            case "No":
            case "NO":
            case "off":
            case "Off":
            case "OFF":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    public static bool TryParseInteger(string text, out decimal value)
    {
        value = 0;
        var digits = text.Replace("_", string.Empty);
        if (digits.Length == 0)
        {
            return false;
        }

        var negative = digits[0] == '-';
        if (negative || digits[0] == '+')
        {
            digits = digits.Substring(1);
        }

        if (digits.Length == 0)
        {
            return false;
        }

        decimal magnitude;
        try
        {
            if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                magnitude = Convert.ToUInt64(digits.Substring(2), 16);
            }
            else if (digits.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                magnitude = Convert.ToUInt64(digits.Substring(2), 2);
            }
            else if (digits.IndexOf(':') >= 0)
            {
                magnitude = ParseSexagesimal(digits);
            }
            else if (digits.Length > 1 && digits[0] == '0')
            {
                magnitude = Convert.ToUInt64(digits, 8);
            }
            else
            {
                magnitude = ulong.Parse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture);
            }
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
        {
            return false;
        }

        value = negative ? -magnitude : magnitude;
        return true;
    }

    /// <summary>Converts a scalar carrying an explicit standard tag into the value that tag denotes.</summary>
    public static object ResolveTaggedValue(string tag, string text)
    {
        switch (tag)
        {
            case "!!int":
                return TryParseInteger(text, out var integer) && integer >= long.MinValue && integer <= long.MaxValue
                    ? (long)integer
                    : throw new McsYamlFormatException($"'{text}' is not a valid '{tag}' value.");
            case "!!bool":
                return TryParseBoolean(text, out var flag)
                    ? flag
                    : throw new McsYamlFormatException($"'{text}' is not a valid '{tag}' value.");
            case "!!float":
                return TryParseFloat(text, out var number)
                    ? number
                    : throw new McsYamlFormatException($"'{text}' is not a valid '{tag}' value.");
            default:
                return text;
        }
    }

    private static bool TryParseFloat(string text, out double value)
    {
        switch (text)
        {
            case ".inf":
            case ".Inf":
            case ".INF":
            case "+.inf":
            case "+.Inf":
            case "+.INF":
                value = double.PositiveInfinity;
                return true;
            case "-.inf":
            case "-.Inf":
            case "-.INF":
                value = double.NegativeInfinity;
                return true;
            case ".nan":
            case ".NaN":
            case ".NAN":
                value = double.NaN;
                return true;
        }

        return double.TryParse(text.Replace("_", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static decimal ParseSexagesimal(string digits)
    {
        decimal total = 0;
        foreach (var part in digits.Split(':'))
        {
            total = (total * 60) + ulong.Parse(part, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        return total;
    }
}
