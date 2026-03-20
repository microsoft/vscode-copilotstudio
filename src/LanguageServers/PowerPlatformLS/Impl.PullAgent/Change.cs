namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    internal record Change
    {
        public required string Name{ get; set; }

        public required string Uri { get; set; }

        public required ChangeType ChangeType { get; set; }

        public required string ChangeKind { get; set; }

        public required string SchemaName { get; set; }

        public string? RemoteWorkflowContent { get; set; }
    }

    internal enum ChangeType
    {
        Create = 0,
        Update = 1,
        Delete = 2
    }
}