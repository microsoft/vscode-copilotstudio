// Copyright (C) Microsoft Corporation. All rights reserved.
//
// CLI agent entity convergence (TDD D22): the CLI agent identity + recognizer +
// agentSettings are persisted to the language-recognized settings.mcs.yml via the
// OM serializer (the same path classic agents use), replacing the hand-coded
// agent.yaml writer. These tests verify the dispatch produces settings.mcs.yml and
// that the OM round-trip preserves the CLI authoring shape.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CliAgentEntityConvergenceTests
{
    private static readonly AgentFilePath CachePath = new AgentFilePath(".mcs/botdefinition.json");
    private static readonly AgentFilePath AgentYamlPath = new AgentFilePath("agent.yaml");
    private static readonly AgentFilePath SettingsPath = new AgentFilePath("settings.mcs.yml");

    private static readonly string TestDataResourcePrefix =
        typeof(CliAgentEntityConvergenceTests).Assembly.GetName().Name + ".TestData.CliAgentFixtures.";

    [Fact]
    public async Task CliAgentPush_ProducesSettingsMcsYml_NotAgentYaml()
    {
        var entity = LoadEntityFromFixture("FoodLogger");
        Assert.Equal(AuthoringShape.CliCopilot, AgentClassifier.DetectAuthoringShape(entity));

        var accessor = await RunPushWithEntityAsync(entity);

        Assert.True(accessor.Exists(SettingsPath),
            "CLI agent push should persist the entity to the language-recognized settings.mcs.yml.");
        Assert.False(accessor.Exists(AgentYamlPath),
            "CLI agent push must NOT produce the retired agent.yaml.");
    }

    [Fact]
    public async Task CliAgentSettingsMcsYml_RoundTrips_PreservesCliShape()
    {
        var entity = LoadEntityFromFixture("FoodLogger");

        var accessor = await RunPushWithEntityAsync(entity);

        // Read the produced settings.mcs.yml back as a BotEntity and confirm the CLI
        // recognizer + agentSettings survived the OM round-trip (the convergence's
        // core correctness property).
        var yaml = Encoding.UTF8.GetString(accessor.Files[SettingsPath.ToString()]);
        var roundTripped = CodeSerializer.Deserialize<BotEntity>(yaml);
        Assert.NotNull(roundTripped);
        Assert.Equal(AuthoringShape.CliCopilot, AgentClassifier.DetectAuthoringShape(roundTripped!));
        Assert.Equal(entity.SchemaName.Value, roundTripped!.SchemaName.Value);
    }

    [Fact]
    public async Task ClassicAgentPush_ProducesSettingsMcsYml_NotAgentYaml()
    {
        var entity = LoadEntityFromFixture("HRAgent");
        Assert.NotEqual(AuthoringShape.CliCopilot, AgentClassifier.DetectAuthoringShape(entity));

        var accessor = await RunPushWithEntityAsync(entity);

        Assert.True(accessor.Exists(SettingsPath),
            "Classic agent push should produce settings.mcs.yml (unchanged behavior).");
        Assert.False(accessor.Exists(AgentYamlPath),
            "Classic agent push must NOT produce agent.yaml.");
    }

    // --- Helpers ---------------------------------------------------------------------

    private static async Task<InMemoryFileAccessor> RunPushWithEntityAsync(BotEntity entity)
    {
        var (synchronizer, factory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/convergence-{Guid.NewGuid():N}/");
        var accessor = (InMemoryFileAccessor)factory.Create(workspace);

        WorkspaceSynchronizer.WriteCloudCache(accessor, new BotDefinition());
        await accessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "seed-token", CancellationToken.None);

        var confirmationChangeset = new PvaComponentChangeSet(null, entity, "next-token");
        mockIsland
            .Setup(x => x.SaveChangesAsync(
                It.IsAny<AuthoringOperationContextBase>(),
                It.IsAny<PvaComponentChangeSet>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(confirmationChangeset);

        await synchronizer.PushChangesetAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            confirmationChangeset,
            new Mock<ISyncDataverseClient>().Object,
            Guid.NewGuid(),
            null,
            default,
            CancellationToken.None);

        return accessor;
    }

    private static BotEntity LoadEntityFromFixture(string fixtureName)
    {
        var bytes = LoadFixtureBytes(fixtureName);
        var accessor = new InMemoryFileAccessor(new DirectoryPath("c:/test/convergence-load/"));
        using (var s = accessor.OpenWrite(CachePath))
        {
            s.Write(bytes, 0, bytes.Length);
        }
        var def = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)
                  ?? throw new InvalidOperationException($"ReadCloudCacheSnapshot returned null for {fixtureName}.");
        return ((BotDefinition)def).Entity
               ?? throw new InvalidOperationException($"Fixture {fixtureName} has a null entity.");
    }

    private static byte[] LoadFixtureBytes(string fixtureName)
    {
        var resourceName = TestDataResourcePrefix + fixtureName + ".botdefinition.json";
        using var stream = typeof(CliAgentEntityConvergenceTests).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
