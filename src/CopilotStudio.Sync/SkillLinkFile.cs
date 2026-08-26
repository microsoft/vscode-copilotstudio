// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

internal static class SkillLinkFile
{

    internal static void DeleteLink(IFileAccessor fileAccessor, AgentFilePath skillFilePath)
    {
        if (SkillLink.TryGetSkillName(skillFilePath, out var skillName))
        {
            fileAccessor.Delete(SkillLayout.GetLegacyLinkPath(skillName));
        }
    }
}
