// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text.RegularExpressions;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>The identity an agent workspace declares for itself.</summary>
/// <param name="SchemaName">The agent's schema name.</param>
/// <param name="DisplayName">The agent's display name, or its schema name when the workspace declares none.</param>
/// <param name="Description">The agent's description, or <see langword="null"/> when the workspace declares none as text.</param>
public sealed record AgentWorkspaceIdentity(string SchemaName, string DisplayName, string? Description)
{
    /// <summary>The most characters an agent display name may hold, which is the limit Copilot Studio enforces when it creates or renames an agent (Dataverse error 10004) although the <c>bot.name</c> column holds 100.</summary>
    public const int MaximumDisplayNameLength = 42;

    private const string DescriptionKey = "description";

    private static readonly AgentFilePath[] IdentityFilePaths = { WorkspaceSynchronizer.SettingsPath, WorkspaceSynchronizer.TopAgentPath };

    private static readonly Regex DisplayNameDeclaration = new("^(?:displayName|\"displayName\"|'displayName')[ \\t]*:(?=[ \\t\\r\\n]|$)[^\\r\\n]*(?:\\r?\\n(?:[ \\t]*\\r?\\n)*[ \\t]+\\S[^\\r\\n]*)*", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>The display name the workspace declares, or <see langword="null"/> when it declares none, in which case a push leaves the bot's display name as it is.</summary>
    public string? DeclaredDisplayName { get; init; }

    /// <summary>Reads the identity from the workspace's settings file, or from its agent file when it holds no settings file, and returns <see langword="null"/> when that file is missing, unreadable, malformed or declares no schema name.</summary>
    /// <param name="fileAccessor">The workspace's root folder.</param>
    /// <returns>The identity the workspace declares, or <see langword="null"/>.</returns>
    public static AgentWorkspaceIdentity? TryRead(IFileAccessor fileAccessor)
    {
        if (fileAccessor == null)
        {
            throw new ArgumentNullException(nameof(fileAccessor));
        }

        var entity = IdentityFilePathOf(fileAccessor) is { } path && ConnectionReferenceStore.ReadTextOrNull(fileAccessor, path) is { } yaml ? ParseOrNull(yaml) : null;
        var schemaName = entity?.SchemaName.Value;

        return entity == null || schemaName is null || string.IsNullOrWhiteSpace(schemaName) ? null : new AgentWorkspaceIdentity(schemaName, entity.DisplayName ?? schemaName, DescriptionOf(entity)) { DeclaredDisplayName = entity.DisplayName };
    }

    /// <summary>Declares a display name in the file <see cref="TryRead"/> reads by replacing the top-level display name that file declares or adding one at its top, leaving the file untouched for a null or blank name and throwing <see cref="AgentScaffoldException"/> for a name an agent cannot take or <see cref="InvalidOperationException"/> when the workspace holds neither file.</summary>
    /// <param name="fileAccessor">The workspace's root folder.</param>
    /// <param name="displayName">The display name to declare, or <see langword="null"/> or blank to declare none.</param>
    public static void DeclareDisplayName(IFileAccessor fileAccessor, string? displayName)
    {
        if (fileAccessor == null)
        {
            throw new ArgumentNullException(nameof(fileAccessor));
        }

        if (displayName is null || string.IsNullOrWhiteSpace(ValidateDisplayName(displayName)))
        {
            return;
        }

        var path = IdentityFilePathOf(fileAccessor) ?? throw new InvalidOperationException($"The workspace holds neither {WorkspaceSynchronizer.SettingsPath} nor {WorkspaceSynchronizer.TopAgentPath} to declare a display name in.");

        if (FileAccessorExtensions.EditText(fileAccessor.ReadBytes(path), text => Declare(text, $"displayName: {AgentScaffolder.ToYamlScalar(displayName)}")) is { } edited)
        {
            fileAccessor.Write(path, edited);
        }
    }

    /// <summary>Returns the display name unchanged when it is null, blank or a name an agent can take, which holds at most <see cref="MaximumDisplayNameLength"/> characters counting its leading and trailing spaces, a letter or digit and no control character, and otherwise throws <see cref="AgentScaffoldException"/> with the failure <see cref="AgentScaffoldFailure.DisplayNameTooLong"/> or <see cref="AgentScaffoldFailure.DisplayNameUnusable"/>.</summary>
    /// <param name="displayName">The display name to check.</param>
    public static string? ValidateDisplayName(string? displayName)
    {
        if (displayName is null || string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        RequireAllowedLength(displayName);

        return displayName.Any(char.IsLetterOrDigit) && !displayName.Any(char.IsControl) ? displayName : throw new AgentScaffoldException(AgentScaffoldFailure.DisplayNameUnusable, displayName, $"The name '{displayName}' cannot name an agent. Use a name that holds a letter or digit and no control character such as a tab or line break.");
    }

    internal static void RequireAllowedLength(string? displayName)
    {
        if (DescribeExcessLength(displayName) is { } reason)
        {
            throw new AgentScaffoldException(AgentScaffoldFailure.DisplayNameTooLong, displayName, reason);
        }
    }

    internal static string? DescribeExcessLength(string? displayName) => displayName is { Length: > MaximumDisplayNameLength } ? $"An agent display name can hold at most {MaximumDisplayNameLength} characters, and this one holds {displayName.Length}." : null;

    private static AgentFilePath? IdentityFilePathOf(IFileAccessor fileAccessor) => IdentityFilePaths.Where(fileAccessor.Exists).Select(path => (AgentFilePath?)path).FirstOrDefault();

    private static string Declare(string text, string declaration) => DisplayNameDeclaration.Match(text) is { Success: true } declared ? text.Substring(0, declared.Index) + declaration + text.Substring(declared.Index + declared.Length) : declaration + (text.Contains("\r\n") ? "\r\n" : "\n") + text;

    private static BotEntity? ParseOrNull(string yaml)
    {
        try
        {
            return CliAgentBotEntityReader.Parse(yaml);
        }
        catch (WorkspaceValidationException)
        {
            return null;
        }
    }

    private static string? DescriptionOf(BotEntity entity) => entity.ExtensionData != null && entity.ExtensionData.Properties.TryGetValue(DescriptionKey, out var value) && value is StringDataValue text ? text.Value : null;
}