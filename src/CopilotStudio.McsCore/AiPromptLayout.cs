// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore
{
    /// <summary>Names the files and folders an AI prompt occupies on disk.</summary>
    public static class AiPromptLayout
    {
        /// <summary>The folder every AI prompt lives beneath, relative to a workspace root.</summary>
        public const string FolderName = "prompts";

        /// <summary>The file holding an AI prompt's Dataverse metadata.</summary>
        public const string MetadataFileName = "metadata.yml";

        /// <summary>The file holding an AI prompt's definition.</summary>
        public const string PromptFileName = "prompt.json";

        /// <summary>The schema name prefix every AI prompt change carries.</summary>
        public const string SchemaNamePrefix = "Mcs.AIPrompt.";

        /// <summary>The change kind every AI prompt change carries.</summary>
        public const string ChangeKind = "AIPrompt";
    }
}
