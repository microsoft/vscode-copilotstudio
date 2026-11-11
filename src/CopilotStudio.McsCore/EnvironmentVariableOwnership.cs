// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;

namespace Microsoft.CopilotStudio.McsCore;

/// <summary>Decides whether an environment variable belongs to an agent or component collection by its schema name prefix.</summary>
public static class EnvironmentVariableOwnership
{
    /// <summary>Returns whether the text before the first dot of the schema name equals the root schema name, comparing ordinally.</summary>
    /// <param name="schemaName">The environment variable schema name.</param>
    /// <param name="rootSchemaName">The schema name of the agent or component collection.</param>
    public static bool IsOwnedBy(string schemaName, string rootSchemaName)
    {
        var separatorIndex = schemaName.IndexOf('.');
        var prefixLength = separatorIndex > 0 ? separatorIndex : schemaName.Length;
        return prefixLength == rootSchemaName.Length && string.CompareOrdinal(schemaName, 0, rootSchemaName, 0, prefixLength) == 0;
    }

    /// <summary>Returns whether the environment variable belongs to the agent or component collection the definition describes.</summary>
    /// <param name="schemaName">The environment variable schema name.</param>
    /// <param name="definition">The agent or component collection definition.</param>
    public static bool IsOwnedBy(string schemaName, DefinitionBase definition) => IsOwnedBy(schemaName, definition is BotDefinition or BotComponentCollectionDefinition ? definition.GetRootSchemaName() : string.Empty);
}
