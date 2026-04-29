// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Smoke tests targeting net48 to validate that the netstandard2.0 build of
// Microsoft.CopilotStudio.Sync is consumable from a net48 host.
//
// Compile-time gate (always active):
//   Net48's BCL does not provide System.Runtime.CompilerServices.IsExternalInit,
//   so any public init-only setter on a Sync type forces the consumer to bind
//   IsExternalInit at compile time. The test methods below construct
//   AgentSyncInfo via init setters; if Sync's polyfill is missing or unresolvable
//   from a net48 consumer, this project fails to compile. Building the project
//   is the test for that.
//
// Runtime tests (skipped by default):
//   The test bodies also assert the loaded type behaves correctly. Running them
//   requires loading the dev-built (delay-signed) Sync.dll, which fails strong-
//   name verification on net48 unless skip-verification is registered. See
//   Net48RuntimeFactAttribute.cs for how to enable. Published builds of the
//   package are fully signed, so this only affects local dev validation.

using Microsoft.CopilotStudio.Sync;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.Net48Smoke;

public class IsExternalInitTests
{
    [Net48RuntimeFact]
    public void Net48ConsumerCanConstructPublicRecordWithInitSetters()
    {
        var info = new AgentSyncInfo
        {
            DataverseEndpoint = new System.Uri("https://example.com/"),
            EnvironmentId = "smoke-test-env",
        };

        Assert.NotNull(info);
        Assert.Equal("smoke-test-env", info.EnvironmentId);
        Assert.Equal("https://example.com/", info.DataverseEndpoint.ToString());
    }

    [Net48RuntimeFact]
    public void Net48BinaryLoadsAndExposesPublicTypes()
    {
        Assert.NotNull(typeof(AgentSyncInfo));
    }
}
