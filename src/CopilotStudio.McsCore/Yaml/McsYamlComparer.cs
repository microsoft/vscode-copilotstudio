// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore.Yaml;

/// <summary>Compares parsed YAML documents so that formatting differences are not reported as content changes.</summary>
internal static class McsYamlComparer
{
    public static bool DocumentsMatch(string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        try
        {
            return AreEquivalent(McsYamlReader.Parse(left!), McsYamlReader.Parse(right!));
        }
        catch (McsYamlFormatException)
        {
            return false;
        }
    }

    private static bool AreEquivalent(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left is string leftText)
        {
            return right is string rightText && string.Equals(leftText, rightText, StringComparison.Ordinal);
        }

        if (left is IDictionary<string, object?> leftMap)
        {
            if (right is not IDictionary<string, object?> rightMap || leftMap.Count != rightMap.Count)
            {
                return false;
            }

            foreach (var entry in leftMap)
            {
                if (!rightMap.TryGetValue(entry.Key, out var other) || !AreEquivalent(entry.Value, other))
                {
                    return false;
                }
            }

            return true;
        }

        if (left is IList<object?> leftList)
        {
            if (right is not IList<object?> rightList || leftList.Count != rightList.Count)
            {
                return false;
            }

            for (var index = 0; index < leftList.Count; index++)
            {
                if (!AreEquivalent(leftList[index], rightList[index]))
                {
                    return false;
                }
            }

            return true;
        }

        return Equals(left, right);
    }
}
