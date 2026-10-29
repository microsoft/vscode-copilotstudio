// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore;

internal sealed record SyncedAssetKind(string ChangeKind, string SchemaNamePrefix, string DisplayName, string PrimaryFileName, IReadOnlyList<string> FileNames, AgentFilePath CloudCopyPath)
{
    internal static readonly SyncedAssetKind CustomConnector = new(CustomConnectorLayout.ChangeKind, CustomConnectorLayout.SchemaNamePrefix, "custom connector", CustomConnectorLayout.MetadataFileName, new[] { CustomConnectorLayout.MetadataFileName, CustomConnectorLayout.OpenApiDefinitionFileName, CustomConnectorLayout.ConnectionParametersFileName, CustomConnectorLayout.PolicyTemplateInstancesFileName, CustomConnectorLayout.IconFileName }, new AgentFilePath(".mcs/.connectors-cache.json"));

    internal static readonly SyncedAssetKind AiPrompt = new(AiPromptLayout.ChangeKind, AiPromptLayout.SchemaNamePrefix, "AI prompt", AiPromptLayout.PromptFileName, new[] { AiPromptLayout.PromptFileName, AiPromptLayout.MetadataFileName }, new AgentFilePath(".mcs/.aiprompts-cache.json"));

    private static readonly SyncedAssetKind[] Kinds = { CustomConnector, AiPrompt };

    internal static SyncedAssetKind? Find(string? changeKind) => Kinds.FirstOrDefault(kind => kind.ChangeKind == changeKind);

    internal static string? ReadCloudCopy(IFileAccessor fileAccessor, string schemaName) => Kinds.FirstOrDefault(candidate => schemaName.StartsWith(candidate.SchemaNamePrefix, StringComparison.OrdinalIgnoreCase)) is { } kind && kind.ReadCloudCopies(fileAccessor).TryGetValue(schemaName, out var content) ? content : null;

    internal static IReadOnlyDictionary<string, string> ToFiles(IEnumerable<(string FileName, string? Content)> files)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fileName, content) in files)
        {
            if (content != null)
            {
                result.Add(fileName, content);
            }
        }

        return result;
    }

    internal string GetFileSchemaName(Guid id, string fileName) => fileName == PrimaryFileName ? $"{SchemaNamePrefix}{id}" : $"{SchemaNamePrefix}{id}.{Path.GetFileNameWithoutExtension(fileName)}";

    internal Dictionary<string, string> ReadCloudCopies(IFileAccessor fileAccessor) => SyncStateMap.Read(fileAccessor, CloudCopyPath);

    internal void WriteCloudCopies(IFileAccessor fileAccessor, IReadOnlyDictionary<string, string> cloudCopies) => SyncStateMap.Write(fileAccessor, CloudCopyPath, cloudCopies);

    internal IReadOnlyDictionary<string, string> GetCloudCopy(IReadOnlyDictionary<string, string> cloudCopies, Guid id) => ToFiles(FileNames.Select(fileName => (fileName, cloudCopies.TryGetValue(GetFileSchemaName(id, fileName), out var content) ? content : null)));

    internal void SetCloudCopy(IDictionary<string, string> cloudCopies, Guid id, IReadOnlyDictionary<string, string> files)
    {
        foreach (var fileName in FileNames)
        {
            if (files.TryGetValue(fileName, out var content))
            {
                cloudCopies[GetFileSchemaName(id, fileName)] = content;
            }
            else
            {
                cloudCopies.Remove(GetFileSchemaName(id, fileName));
            }
        }
    }

    internal bool HasCloudCopy(IFileAccessor fileAccessor, string schemaName)
    {
        var assetSchemaName = SyncedAssetSchemaName.GetAssetSchemaName(schemaName);
        return ReadCloudCopies(fileAccessor).Keys.Any(cachedSchemaName => string.Equals(SyncedAssetSchemaName.GetAssetSchemaName(cachedSchemaName), assetSchemaName, StringComparison.OrdinalIgnoreCase));
    }
}
