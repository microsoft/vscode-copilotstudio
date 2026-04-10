// Copyright (C) Microsoft Corporation. All rights reserved.

using Xunit;

namespace Microsoft.CopilotStudio.Sync.E2ETests;

/// <summary>
/// xUnit test collection that serializes all live-tenant E2E tests. Tests in this
/// collection target the same Copilot Studio agent and must not run concurrently —
/// a push in one test would cause verify failures in another.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveTenantCollection
{
    public const string Name = "LiveTenant";
}
