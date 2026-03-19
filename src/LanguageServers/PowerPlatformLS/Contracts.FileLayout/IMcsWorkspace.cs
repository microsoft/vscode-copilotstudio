namespace Microsoft.PowerPlatformLS.Contracts.FileLayout
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;

    public interface IMcsWorkspace
    {
        DirectoryPath FolderPath { get; }

        DefinitionBase Definition { get; }
    }
}
