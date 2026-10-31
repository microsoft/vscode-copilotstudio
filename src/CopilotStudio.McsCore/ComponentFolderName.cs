// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text.RegularExpressions;

namespace Microsoft.CopilotStudio.McsCore;

internal static class ComponentFolderName
{
    private static readonly Regex TrailingGuidPattern = new("([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static Guid? ExtractTrailingGuid(string folderName)
    {
        var match = TrailingGuidPattern.Match(folderName);
        return match.Success && Guid.TryParse(match.Value, out var id) ? id : null;
    }
}
