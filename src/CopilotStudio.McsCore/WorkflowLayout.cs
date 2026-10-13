// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore
{
    /// <summary>Names the files and folders a workflow occupies on disk.</summary>
    public static class WorkflowLayout
    {
        /// <summary>The folder every workflow lives beneath, relative to a workspace root.</summary>
        public const string WorkflowsFolderName = "workflows";

        /// <summary>The file holding a workflow's definition.</summary>
        public const string DefinitionFileName = "workflow.json";

        /// <summary>The file holding a workflow's Dataverse metadata.</summary>
        public const string MetadataFileName = "metadata.yml";

        /// <summary>The schema name prefix every workflow change carries.</summary>
        public const string SchemaNamePrefix = "Mcs.Workflow.";
    }
}
