// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

internal static class SkillLinkFile
{
    internal static void WriteLink(IFileAccessor fileAccessor, AgentFilePath skillFilePath, string schemaName)
    {
        if (!SkillLink.TryGetSkillName(skillFilePath, out var skillName))
        {
            return;
        }

        var json = SchemaLink.Serialize(new SchemaLinkData { SchemaName = schemaName, FolderName = skillName });
        using var stream = fileAccessor.OpenWrite(new AgentFilePath($"{LspProjection.BehaviorsFolder}{skillName}/{SkillLink.LinkFileName}"));
        using var textWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        textWriter.Write(json);
    }

    internal static void DeleteLink(IFileAccessor fileAccessor, AgentFilePath skillFilePath)
    {
        if (SkillLink.TryGetSkillName(skillFilePath, out var skillName))
        {
            fileAccessor.Delete(new AgentFilePath($"{LspProjection.BehaviorsFolder}{skillName}/{SkillLink.LinkFileName}"));
        }
    }
}
