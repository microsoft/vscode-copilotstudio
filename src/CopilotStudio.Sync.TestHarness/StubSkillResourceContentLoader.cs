// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.Agents.Platform.Content.Abstractions;

namespace Microsoft.CopilotStudio.Sync.TestHarness;

/// <summary>Loads no skill resource content, like the loader the VS Code extension host registers.</summary>
internal sealed class StubSkillResourceContentLoader : ISkillResourceContentLoader
{
    public Task<SkillResource?> LoadAsync(AuthoringOperationContextBase context, FileAttachmentComponent attachment, CancellationToken cancellationToken) => Task.FromResult<SkillResource?>(null);
}
