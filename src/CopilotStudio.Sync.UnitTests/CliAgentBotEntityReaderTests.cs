// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Unit tests for the OM-converged CliAgentBotEntityReader (TDD D22). The CLI agent
// identity now lives in the language-recognized settings.mcs.yml; this reader
// overlays the on-disk settings onto the cloud-cache BotEntity via the OM-native
// BotEntity.ApplySettingsYamlProperties. These exercise:
//   - IsCliLayoutAdopted (the destructive-delete gate, now keyed off settings.mcs.yml).
//   - Overlay success (the on-disk CLI shape survives the overlay onto cloud).
//   - Failure modes: missing/malformed file, missing/mismatched schemaName.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.CopilotStudio.McsCore;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CliAgentBotEntityReaderTests
{
    private static readonly AgentFilePath CachePath = new AgentFilePath(".mcs/botdefinition.json");
    private static readonly AgentFilePath SettingsPath = new AgentFilePath("settings.mcs.yml");

    private static readonly string TestDataResourcePrefix =
        typeof(CliAgentBotEntityReaderTests).Assembly.GetName().Name + ".TestData.CliAgentFixtures.";

    // --- IsCliLayoutAdopted -------------------------------------------------

    [Fact]
    public void IsCliLayoutAdopted_NoSettings_ReturnsFalse()
    {
        var accessor = new InMemoryFileAccessor(new DirectoryPath("c:/test/empty/"));
        Assert.False(CliAgentBotEntityReader.IsCliLayoutAdopted(accessor));
    }

    [Fact]
    public void IsCliLayoutAdopted_SettingsPresent_ReturnsTrue()
    {
        var accessor = new InMemoryFileAccessor(new DirectoryPath("c:/test/present/"));
        WriteSettings(accessor, LoadEntityFromFixture("FoodLogger"));
        Assert.True(CliAgentBotEntityReader.IsCliLayoutAdopted(accessor));
    }

    [Fact]
    public void IsCliLayoutAdopted_NullAccessor_ReturnsFalse()
    {
        Assert.False(CliAgentBotEntityReader.IsCliLayoutAdopted(null!));
    }

    // --- Overlay success ----------------------------------------------------

    [Fact]
    public void Overlay_PreservesCliShapeAndIdentity_OntoCloud()
    {
        var diskEntity = LoadEntityFromFixture("FoodLogger");
        var accessor = new InMemoryFileAccessor(new DirectoryPath("c:/test/overlay/"));
        WriteSettings(accessor, diskEntity);

        var result = CliAgentBotEntityReader.Overlay(accessor, diskEntity);

        // The overlaid entity keeps the CLI authoring shape (recognizer +
        // agentSettings) and the disk schemaName.
        Assert.Equal(AuthoringShape.CliCopilot, AgentClassifier.DetectAuthoringShape(result));
        Assert.Equal(diskEntity.SchemaName.Value, result.SchemaName.Value);
    }

    // --- Failure modes ------------------------------------------------------

    [Fact]
    public void Overlay_MissingFile_Throws()
    {
        var accessor = new InMemoryFileAccessor(new DirectoryPath("c:/test/missing/"));
        var cloud = LoadEntityFromFixture("FoodLogger");

        var ex = Assert.Throws<InvalidOperationException>(
            () => CliAgentBotEntityReader.Overlay(accessor, cloud));
        Assert.Contains("settings.mcs.yml", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Overlay_MalformedYaml_Throws()
    {
        var accessor = new InMemoryFileAccessor(new DirectoryPath("c:/test/malformed/"));
        await accessor.WriteAsync(SettingsPath, "{ this is : not [ valid yaml :\n  - nope", CancellationToken.None);

        var ex = Assert.Throws<InvalidOperationException>(
            () => CliAgentBotEntityReader.Overlay(accessor, LoadEntityFromFixture("FoodLogger")));
        Assert.Contains("settings.mcs.yml", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Overlay_MissingSchemaName_Throws()
    {
        var accessor = new InMemoryFileAccessor(new DirectoryPath("c:/test/noschema/"));
        await accessor.WriteAsync(SettingsPath, "displayName: FoodLogger\nlanguage: 1033\n", CancellationToken.None);

        var ex = Assert.Throws<InvalidOperationException>(
            () => CliAgentBotEntityReader.Overlay(accessor, LoadEntityFromFixture("FoodLogger")));
        Assert.Contains("schemaName", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlay_SchemaNameMismatch_Throws()
    {
        // Disk settings are FoodLogger; cloud entity is HRAgent (different schemaName).
        var accessor = new InMemoryFileAccessor(new DirectoryPath("c:/test/mismatch/"));
        WriteSettings(accessor, LoadEntityFromFixture("FoodLogger"));
        var cloud = LoadEntityFromFixture("HRAgent");

        var ex = Assert.Throws<InvalidOperationException>(
            () => CliAgentBotEntityReader.Overlay(accessor, cloud));
        Assert.Contains("does not match", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- Helpers ------------------------------------------------------------

    private static void WriteSettings(InMemoryFileAccessor accessor, BotEntity entity)
    {
        using var stream = accessor.OpenWrite(SettingsPath);
        using var sw = new StreamWriter(stream, new UTF8Encoding(false));
        using var ctx = YamlSerializationContext.UseStandardSerializationContextIfNotDefined(throwOnInvalidYaml: false);
        YamlSerializer.SerializeWithoutKind(sw, entity.WithOnlySettingsYamlProperties());
    }

    private static BotEntity LoadEntityFromFixture(string fixtureName)
    {
        var bytes = LoadFixtureBytes(fixtureName);
        var accessor = new InMemoryFileAccessor(new DirectoryPath("c:/test/reader-load/"));
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
        using var stream = typeof(CliAgentBotEntityReaderTests).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
