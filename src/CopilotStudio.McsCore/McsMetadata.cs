// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore;

internal readonly record struct McsMetadata(string? ComponentName, string? Description, string? SchemaName, string? Bundle, string? ManifestSchemaName)
{
    internal const string PropertyName = "mcs.metadata";
    internal const string ComponentNameKey = "componentName";
    internal const string DescriptionKey = "description";
    internal const string SchemaNameKey = "schemaName";
    internal const string BundleKey = "bundle";
    internal const string ManifestSchemaNameKey = "manifestSchemaName";
}
