// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

internal static class ChildAgentLinkFile
{
    internal const string LinkFileName = ChildAgentLink.LinkFileName;


    internal readonly record struct ChildAgentFolder(string FolderName, SchemaLinkData? Link);


    internal static void DeleteLink(IFileAccessor fileAccessor, AgentFilePath agentDefinitionPath)
    {
        if (ChildAgentLink.TryGetFolderName(agentDefinitionPath.ToString(), out var folderName))
        {
            fileAccessor.Delete(ChildAgentLink.GetLegacyLinkPath(folderName));
        }
    }


    internal static IReadOnlyList<ChildAgentFolder> ListFolders(IFileAccessor fileAccessor)
    {
        var folders = new List<ChildAgentFolder>();

        foreach (var agentDefinitionPath in fileAccessor.ListFiles(ChildAgentLink.AgentsFolderName, ChildAgentLink.AgentDefinitionFileName))
        {
            if (!ChildAgentLink.TryGetFolderName(agentDefinitionPath.ToString(), out var folderName))
            {
                continue;
            }

            var link = ChildAgentLink.ReadLink(fileAccessor, agentDefinitionPath, folderName);
            folders.Add(new ChildAgentFolder(folderName, link));
        }

        return folders;
    }
}
