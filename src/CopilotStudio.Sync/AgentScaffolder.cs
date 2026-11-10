// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.FileProjection;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.McsCore.Yaml;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Writes a new agent workspace from the templates embedded in this assembly, in the form a clone of the agent writes it.</summary>
public static class AgentScaffolder
{
    private const string ResourceRoot = "Templates/agents/";

    private const string SubstitutionPrefix = "template_";

    private static readonly Regex YamlScalarWrapper = new(@"\$yaml\{(?<value>[^\r\n]*?)\}\$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Writes a template into a project directory and returns the workspace-relative paths it created, in ordinal order, throwing <see cref="AgentScaffoldException"/> before writing anything for a display name <see cref="AgentWorkspaceIdentity.ValidateDisplayName"/> refuses.</summary>
    /// <param name="projectDirectory">The directory the workspace is written to.</param>
    /// <param name="templateFolder">The embedded template folder to write, <see cref="AgentAuthoringModes.Classic"/> or <see cref="AgentAuthoringModes.CliCopilot"/>.</param>
    /// <param name="displayName">The agent's display name.</param>
    /// <param name="schemaName">The agent's schema name.</param>
    /// <param name="instructions">The agent's instructions.</param>
    public static IReadOnlyList<string> Scaffold(string projectDirectory, string templateFolder, string displayName, string schemaName, string instructions)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            throw new ArgumentException("A project directory is required.", nameof(projectDirectory));
        }

        return Scaffold(WorkflowWorkspace.OpenWorkspace(projectDirectory), templateFolder, displayName, schemaName, instructions);
    }

    /// <summary>Writes a template through a workspace accessor and returns the workspace-relative paths it created, in ordinal order, throwing <see cref="AgentScaffoldException"/> before writing anything for a display name <see cref="AgentWorkspaceIdentity.ValidateDisplayName"/> refuses.</summary>
    /// <param name="workspace">The workspace the template is written to.</param>
    /// <param name="templateFolder">The embedded template folder to write, <see cref="AgentAuthoringModes.Classic"/> or <see cref="AgentAuthoringModes.CliCopilot"/>.</param>
    /// <param name="displayName">The agent's display name.</param>
    /// <param name="schemaName">The agent's schema name.</param>
    /// <param name="instructions">The agent's instructions.</param>
    public static IReadOnlyList<string> Scaffold(IFileAccessor workspace, string templateFolder, string displayName, string schemaName, string instructions)
    {
        if (workspace is null)
        {
            throw new ArgumentNullException(nameof(workspace));
        }

        if (displayName is null)
        {
            throw new ArgumentNullException(nameof(displayName));
        }

        if (schemaName is null)
        {
            throw new ArgumentNullException(nameof(schemaName));
        }

        if (instructions is null)
        {
            throw new ArgumentNullException(nameof(instructions));
        }

        AgentWorkspaceIdentity.ValidateDisplayName(displayName);

        var assembly = typeof(AgentScaffolder).Assembly;
        var resourcePrefix = $"{ResourceRoot}{templateFolder}/";
        var resources = assembly.GetManifestResourceNames().Where(name => NormalizeSeparators(name).StartsWith(resourcePrefix, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (resources.Length == 0)
        {
            throw AgentScaffoldException.TemplateNotFound(templateFolder);
        }

        var substitutions = Substitutions(displayName, schemaName, instructions);
        var files = new List<KeyValuePair<string, byte[]>>(resources.Length);

        foreach (var resource in resources)
        {
            var relativePath = ToWorkspacePath(NormalizeSeparators(resource).Substring(resourcePrefix.Length), out var substitute);
            files.Add(new(relativePath, Render(assembly, resource, new AgentFilePath(relativePath), substitute ? substitutions : null, schemaName)));
        }

        foreach (var file in files)
        {
            workspace.Write(new AgentFilePath(file.Key), file.Value);
        }

        var created = files.Select(file => file.Key).ToList();

        if (string.Equals(templateFolder, AgentAuthoringModes.CliCopilot, StringComparison.OrdinalIgnoreCase))
        {
            WorkflowWorkspace.EnsureLayoutMarker(workspace);
            created.Add(AgentClassifier.WorkspaceLayoutMarkerFileName);
        }

        return created.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static string NormalizeSeparators(string resourceName) => resourceName.Replace('\\', '/');

    private static string ToWorkspacePath(string resourceRelativePath, out bool substitute)
    {
        var separator = resourceRelativePath.LastIndexOf('/');
        var directory = separator < 0 ? string.Empty : resourceRelativePath.Substring(0, separator + 1);
        var fileName = resourceRelativePath.Substring(separator + 1);

        substitute = fileName.StartsWith(SubstitutionPrefix, StringComparison.OrdinalIgnoreCase);

        return substitute ? directory + fileName.Substring(SubstitutionPrefix.Length) : resourceRelativePath;
    }

    private static byte[] Render(Assembly assembly, string resourceName, AgentFilePath path, IReadOnlyList<KeyValuePair<string, string>>? substitutions, string schemaName)
    {
        using var resource = assembly.GetManifestResourceStream(resourceName) ?? throw AgentScaffoldException.TemplateNotFound(resourceName);

        if (!path.ToString().EndsWith(WorkspaceSynchronizer.McsFileExtension, StringComparison.Ordinal))
        {
            using var copy = new MemoryStream();
            resource.CopyTo(copy);

            return copy.ToArray();
        }

        using var reader = new StreamReader(resource, Encoding.UTF8);
        var text = substitutions is null ? reader.ReadToEnd() : ApplySubstitutions(reader.ReadToEnd(), substitutions);

        return path.Equals(WorkspaceSynchronizer.SettingsPath)
            ? WorkspaceSynchronizer.SettingsFileContent(CliAgentBotEntityReader.Parse(text))
            : WorkspaceSynchronizer.ComponentFileContent(Compile(path, text, schemaName), new BotDefinition(), path);
    }

    private static BotComponentBase Compile(AgentFilePath path, string text, string schemaName) =>
        McsYamlValidator.Deserialize(text, typeof(BotElement), null) is { } element && new SyncMcsFileParser(LspProjectorService.Instance).CompileFile(path, element, new ProjectionContext(schemaName), AuthoringShape.Classic) is ({ } component, _)
            ? WorkspaceSynchronizer.RoundTripThroughServiceJson(component)
            : throw new InvalidOperationException($"The template file '{path}' does not compile into a component.");

    private static string ApplySubstitutions(string template, IReadOnlyList<KeyValuePair<string, string>> substitutions) => Replace(YamlScalarWrapper.Replace(template, match => ToYamlScalar(Replace(match.Groups["value"].Value, substitutions))), substitutions);

    private static string Replace(string content, IReadOnlyList<KeyValuePair<string, string>> substitutions)
    {
        foreach (var substitution in substitutions)
        {
            content = content.Replace(substitution.Key, substitution.Value);
        }

        return content;
    }

    private static KeyValuePair<string, string>[] Substitutions(string displayName, string schemaName, string instructions) =>
    [
        new("$agentDisplayName$", displayName),
        new("$agentSchemaName$", schemaName),
        new("$agentInstructions$", instructions),
        new("$agentDisplayNameYaml$", ToYamlScalar(displayName)),
        new("$agentSchemaNameYaml$", ToYamlScalar(schemaName)),
        new("$agentInstructionsYaml$", ToYamlScalar(instructions)),
    ];

    internal static string ToYamlScalar(string value) => string.IsNullOrEmpty(value) ? "\"\"" : $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r\n", "\\n").Replace("\n", "\\n")}\"";
}
