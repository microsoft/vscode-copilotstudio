// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Expressions;
using Microsoft.Agents.ObjectModel.PowerFx;

namespace Microsoft.CopilotStudio.McsCore;

/// <summary>Finds the environment variables that components depend on, the same way Dataverse links them when the components are saved.</summary>
public static class EnvironmentVariableDependencies
{
    private static readonly Lazy<IExpressionSyntaxAnalyzer> LazyDefaultAnalyzer = new(() => new PowerFxSyntaxAnalyzer(DefaultFeatureConfiguration.Instance));

    /// <summary>Gets the shared Power Fx analyzer used when a host does not provide one.</summary>
    public static IExpressionSyntaxAnalyzer DefaultAnalyzer => LazyDefaultAnalyzer.Value;

    /// <summary>Gets the schema names of the environment variables the components reference, ignoring case.</summary>
    /// <param name="components">The components to analyze.</param>
    /// <param name="analyzer">The analyzer that finds Power Fx dependencies.</param>
    public static ISet<string> GetReferencedSchemaNames(IEnumerable<BotComponentBase> components, IExpressionSyntaxAnalyzer analyzer) => new HashSet<string>(components.SelectMany(component => FindSchemaNames(component, analyzer)), StringComparer.OrdinalIgnoreCase);

    /// <summary>Groups the components by the schema names of the environment variables they reference, ignoring case.</summary>
    /// <param name="components">The components to analyze.</param>
    /// <param name="analyzer">The analyzer that finds Power Fx dependencies.</param>
    public static ILookup<string, BotComponentBase> GetReferencingComponents(IEnumerable<BotComponentBase> components, IExpressionSyntaxAnalyzer analyzer) => components.SelectMany(component => FindSchemaNames(component, analyzer).Distinct(StringComparer.OrdinalIgnoreCase), (component, schemaName) => (component, schemaName)).ToLookup(reference => reference.schemaName, reference => reference.component, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> FindSchemaNames(BotComponentBase component, IExpressionSyntaxAnalyzer analyzer) => component.GetReferencedEnvironmentVariableDefinitions(analyzer).Select(schemaName => schemaName.Value);
}
