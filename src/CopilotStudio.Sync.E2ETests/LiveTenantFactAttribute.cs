// Copyright (C) Microsoft Corporation. All rights reserved.

using Xunit;

namespace Microsoft.CopilotStudio.Sync.E2ETests;

/// <summary>
/// xUnit [Fact] that auto-skips when live tenant environment variables are not set.
/// Required variables: COPILOT_TEST_TENANT_ID, COPILOT_TEST_CLIENT_ID,
/// COPILOT_TEST_ENVIRONMENT_ID, COPILOT_TEST_ENVIRONMENT_URL, COPILOT_TEST_AGENT_SCHEMA_NAME.
/// </summary>
public sealed class LiveTenantFactAttribute : FactAttribute
{
    private static readonly string[] RequiredVars =
    [
        "COPILOT_TEST_TENANT_ID",
        "COPILOT_TEST_CLIENT_ID",
        "COPILOT_TEST_ENVIRONMENT_ID",
        "COPILOT_TEST_ENVIRONMENT_URL",
        "COPILOT_TEST_AGENT_SCHEMA_NAME",
    ];

    public LiveTenantFactAttribute()
    {
        var missing = RequiredVars
            .Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)))
            .ToArray();

        if (missing.Length > 0)
        {
            Skip = $"Live tenant env vars not set: {string.Join(", ", missing)}";
        }
    }
}
