namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Collections.Immutable;

    internal class StandaloneWorkflowView
    {
        public Guid WorkflowId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? Description { get; set; }

        public bool IsActivated { get; set; }

        public DateTimeOffset? ModifiedOn { get; set; }

        public ImmutableArray<string> ConnectionReferences { get; set; } = ImmutableArray<string>.Empty;
    }

    internal class ListStandaloneWorkflowsRequest : DataverseRequest, IDefaultContextRequest
    {
        public const string MessageName = "powerplatformls/listStandaloneWorkflows";

        public string? NameFilter { get; set; }

        public int? MaximumCount { get; set; }
    }

    internal class ListStandaloneWorkflowsResponse : ResponseBase
    {
        public ImmutableArray<StandaloneWorkflowView> Workflows { get; set; } = ImmutableArray<StandaloneWorkflowView>.Empty;
    }

    internal class CloneStandaloneWorkflowRequest : DataverseRequest, IDefaultContextRequest
    {
        public const string MessageName = "powerplatformls/cloneStandaloneWorkflow";

        public required Guid WorkflowId { get; set; }

        public required Uri RootFolder { get; set; }
    }

    internal class CloneStandaloneWorkflowResponse : ResponseBase
    {
        public StandaloneWorkflowView? Workflow { get; set; }

        public string? ProjectFolder { get; set; }

        public string? FolderName { get; set; }

        public bool Changed { get; set; }
    }

    internal class CreateStandaloneWorkflowRequest : IDefaultContextRequest
    {
        public const string MessageName = "powerplatformls/createStandaloneWorkflow";

        public required string Name { get; set; }

        public required Uri RootFolder { get; set; }
    }

    internal class CreateStandaloneWorkflowResponse : ResponseBase
    {
        public Guid WorkflowId { get; set; }

        public string? ProjectFolder { get; set; }

        public string? FolderName { get; set; }

        public ImmutableArray<string> WrittenFiles { get; set; } = ImmutableArray<string>.Empty;
    }

    internal class DeleteStandaloneWorkflowRequest : DataverseRequest, IDefaultContextRequest
    {
        public const string MessageName = "powerplatformls/deleteStandaloneWorkflow";

        public required Guid WorkflowId { get; set; }
    }

    internal class DeleteStandaloneWorkflowResponse : ResponseBase
    {
        public bool Deleted { get; set; }
    }

    internal class GetStandaloneWorkflowAgentUseRequest : DataverseRequest, IDefaultContextRequest
    {
        public const string MessageName = "powerplatformls/getStandaloneWorkflowAgentUse";

        public required Guid WorkflowId { get; set; }
    }

    internal class GetStandaloneWorkflowAgentUseResponse : ResponseBase
    {
        public ImmutableArray<string> AgentNames { get; set; } = ImmutableArray<string>.Empty;

        public string Warning { get; set; } = string.Empty;
    }
}
