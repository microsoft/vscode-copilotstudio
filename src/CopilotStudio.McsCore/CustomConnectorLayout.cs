// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore
{
    /// <summary>Names the files and folders a custom connector occupies on disk.</summary>
    public static class CustomConnectorLayout
    {
        /// <summary>The folder every custom connector lives beneath, relative to a workspace root.</summary>
        public const string FolderName = "connectors";

        /// <summary>The file holding a custom connector's Dataverse metadata.</summary>
        public const string MetadataFileName = "metadata.yml";

        /// <summary>The file holding a custom connector's OpenAPI definition.</summary>
        public const string OpenApiDefinitionFileName = "openapidefinition.json";

        /// <summary>The file holding a custom connector's connection parameters.</summary>
        public const string ConnectionParametersFileName = "connectionparameters.json";

        /// <summary>The file holding a custom connector's policy template instances.</summary>
        public const string PolicyTemplateInstancesFileName = "policytemplateinstances.json";

        /// <summary>The file holding a custom connector's icon.</summary>
        public const string IconFileName = "iconblob.png";

        /// <summary>The schema name prefix every custom connector change carries.</summary>
        public const string SchemaNamePrefix = "Mcs.Connector.";

        /// <summary>The change kind every custom connector change carries.</summary>
        public const string ChangeKind = "CustomConnector";
    }
}
