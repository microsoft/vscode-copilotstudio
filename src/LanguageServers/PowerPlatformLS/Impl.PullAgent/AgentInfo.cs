namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;

    internal class AgentInfo
    {
        public required Guid AgentId { get; set; }

        public required string DisplayName { get; set; }

        public string? IconBase64 { get; set; }

        public List<ComponentCollectionInfo> ComponentCollections { get; set; } = new();

        public string? SchemaName { get; set; }
    }

    internal class ComponentCollectionInfo
    {
        public required Guid Id { get; set; }

        public required string SchemaName { get; set; }

        public required string DisplayName { get; set; }
    }

    internal class SolutionInfo
    {
        // Dictionary to represent the Record<SolutionUniqueName, SolutionVersion>
        public Dictionary<string, Version> SolutionVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Represents the copilotStudioSolutionVersion
        public required Version CopilotStudioSolutionVersion { get; set; }

        public Version GetDataverseTableSearchSolutionUniqueName() => GetSolutionVersionByName("msft_AIPlatformExtensionsComponents");

        public Version GetRelevanceSearchSolutionVersion() => GetSolutionVersionByName("msdyn_RelevanceSearch");

        private Version GetSolutionVersionByName(string name)
        {
            if (SolutionVersions.TryGetValue(name, out var version))
            {
                return version;
            }

            throw new InvalidOperationException($"Missing solution version for {name}");
        }
    }
}
