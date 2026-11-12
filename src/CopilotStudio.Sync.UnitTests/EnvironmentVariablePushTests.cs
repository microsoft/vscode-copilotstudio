// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Collections.Immutable;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;
using static Microsoft.CopilotStudio.Sync.UnitTests.EnvironmentVariableTestFixtures;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class EnvironmentVariablePushTests
{
    [Theory]
    [InlineData(SharedActivity, 1)]
    [InlineData(UnreferencedActivity, 0)]
    public async Task GetLocalChanges_SharedEnvironmentVariableMissingFromCloud_IsInsertedOnlyWhenReferenced(string activity, int expectedInserts)
    {
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, _, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", activity) });
        WriteEnvironmentVariableFile(fileAccessor, CreateEnvironmentVariable(SharedSchemaName));

        var (changeSet, changes) = await synchronizer.GetLocalChangesAsync(workspace, await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None), CancellationToken.None);

        Assert.Equal(expectedInserts, changeSet.EnvironmentVariableChanges.OfType<EnvironmentVariableInsert>().Count(insert => insert.EnvironmentVariable?.SchemaName.Value == SharedSchemaName));
        Assert.Equal(expectedInserts, changes.Count(change => change.ChangeType == ChangeType.Create && change.Uri == SharedPath.ToString()));
    }

    [Theory]
    [InlineData(EnvironmentVariableType.Secret)]
    [InlineData(EnvironmentVariableType.DataSource)]
    [InlineData(EnvironmentVariableType.String)]
    public async Task GetLocalChanges_SharedEnvironmentVariableInsert_PreservesAuthoredValues(EnvironmentVariableType type)
    {
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, _, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) });
        WriteEnvironmentVariableFile(fileAccessor, CreateEnvironmentVariable(SharedSchemaName, type: type, value: "current"));

        var (changeSet, _) = await synchronizer.GetLocalChangesAsync(workspace, await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None), CancellationToken.None);

        Assert.Equal("current", Assert.Single(changeSet.EnvironmentVariableChanges.OfType<EnvironmentVariableInsert>()).EnvironmentVariable?.ValueComponent?.Value);
    }

    [Theory]
    [InlineData(SharedSchemaName, false, false)]
    [InlineData(SharedSchemaName, true, true)]
    [InlineData(SharedSchemaName, true, false)]
    [InlineData(OwnedSchemaName, true, true)]
    public async Task PushLocalChanges_LspDefinitionWithSourceMetadata_UsesTargetMetadataAndPreservesLocalValues(string schemaName, bool sourceHasValue, bool targetHasValue)
    {
        var source = CreateEnvironmentVariable(schemaName, defaultValue: "source-default", value: sourceHasValue ? "source-current" : null).WithVersion(100);
        var target = CreateEnvironmentVariable(schemaName, defaultValue: "target-default", value: targetHasValue ? "target-current" : null).WithVersion(200);
        if (target.ValueComponent is { } targetValue)
        {
            target = target.WithValueComponent(targetValue.WithVersion(300));
        }

        var (dataverse, lookups) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", $"{{Env.{schemaName}}}") }, target);
        WriteEnvironmentVariableFile(fileAccessor, source);
        var localDefinition = ReadCache(fileAccessor).WithEnvironmentVariables(new[] { source });
        var cacheBeforePush = fileAccessor.Files[CachePath.ToString()];
        var serverCopy = target.WithDefaultValue("server-confirmed").WithVersion(201);
        var saves = CaptureSaves(island, _ =>
        {
            Assert.Equal(cacheBeforePush, fileAccessor.Files[CachePath.ToString()]);
            return CreateChangeSet("token-2", environmentVariableChanges: new[] { new EnvironmentVariableUpdate(serverCopy) });
        });
        lookups.Clear();

        await PushLocalChangesAsync(synchronizer, workspace, dataverse, localDefinition);

        var pushed = Assert.IsType<EnvironmentVariableUpdate>(Assert.Single(Assert.Single(saves).EnvironmentVariableChanges)).EnvironmentVariable ?? throw new Xunit.Sdk.XunitException("Expected an environment variable.");
        Assert.Equal(target.Id, pushed.Id);
        Assert.Equal(target.Version, pushed.Version);
        Assert.Equal("source-default", pushed.DefaultValue);
        if (sourceHasValue)
        {
            var pushedValue = pushed.ValueComponent ?? throw new Xunit.Sdk.XunitException("Expected an environment variable value.");
            Assert.Equal("source-current", pushedValue.Value);
            Assert.Equal(target.Id, pushedValue.DefinitionId);
            Assert.Equal(target.ValueComponent?.Id ?? default, pushedValue.Id);
            Assert.Equal(target.ValueComponent?.Version ?? 0, pushedValue.Version);
        }
        else
        {
            Assert.Null(pushed.ValueComponent);
        }

        Assert.Equal(schemaName == SharedSchemaName ? new[] { SharedSchemaName } : Array.Empty<string>(), lookups.SelectMany(lookup => lookup));
        Assert.True(serverCopy.Equals(Assert.Single(ReadCache(fileAccessor).EnvironmentVariables), NodeComparison.Structural));
    }

    [Fact]
    public async Task GetLocalChanges_LspDefinitionWithOnlyStaleMetadata_DoesNotReportAnUpdate()
    {
        var target = CreateEnvironmentVariable(SharedSchemaName, value: "current").WithVersion(200);
        var sourceId = new EnvironmentVariableDefinitionId(Guid.NewGuid());
        var source = target.WithId(sourceId).WithVersion(100).WithValueComponent(new EnvironmentVariableValue(id: Guid.NewGuid(), definitionId: sourceId, value: "current", version: 100));
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, _, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, target);
        WriteEnvironmentVariableFile(fileAccessor, source);
        var cacheBeforePreview = fileAccessor.Files[CachePath.ToString()];

        var (changeSet, changes) = await synchronizer.GetLocalChangesAsync(workspace, ReadCache(fileAccessor).WithEnvironmentVariables(new[] { source }), CancellationToken.None);

        Assert.Empty(changeSet.EnvironmentVariableChanges);
        Assert.DoesNotContain(changes, change => change.SchemaName == SharedSchemaName);
        Assert.Equal(cacheBeforePreview, fileAccessor.Files[CachePath.ToString()]);
    }

    [Fact]
    public async Task GetRemoteChanges_EnvironmentVariableMetadataChanges_RetainsTheNewServerMetadata()
    {
        var previous = CreateEnvironmentVariable(SharedSchemaName, value: "previous").WithVersion(100);
        var updated = CreateEnvironmentVariable(SharedSchemaName, defaultValue: "updated", value: "updated").WithVersion(200);
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, _, _) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, previous);
        var snapshot = ReadCache(fileAccessor);
        var cacheBeforePreview = fileAccessor.Files[CachePath.ToString()];

        var (changeSet, _) = synchronizer.GetLocalChanges(snapshot.WithEnvironmentVariables(new[] { updated }), snapshot, fileAccessor, "token-2", isRemoteChange: true);

        Assert.True(updated.Equals(Assert.IsType<EnvironmentVariableUpdate>(Assert.Single(changeSet.EnvironmentVariableChanges)).EnvironmentVariable ?? throw new Xunit.Sdk.XunitException("Expected an environment variable."), NodeComparison.Structural));
        Assert.Equal(cacheBeforePreview, fileAccessor.Files[CachePath.ToString()]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PushLocalChanges_RetargetedSharedEnvironmentVariable_UsesLocalValuesAndTargetMetadata(bool targetHasIt, bool targetHasValue)
    {
        var target = TargetEnvironmentVariable(SharedSchemaName);
        target.Version = 200;
        target.Values = targetHasValue ? new[] { new EnvironmentVariableValueInfo { ValueId = Guid.NewGuid(), Version = 300 } } : Array.Empty<EnvironmentVariableValueInfo>();
        var (dataverse, lookups) = targetHasIt ? CreateDataverse(target) : CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, CreateEnvironmentVariable(SharedSchemaName, defaultValue: "source-default", value: "source-current"));
        ClearCloudCache(fileAccessor);
        lookups.Clear();
        var saves = CaptureSaves(island);

        await PushLocalChangesAsync(synchronizer, workspace, dataverse);

        Assert.Equal(new[] { SharedSchemaName }, Assert.Single(lookups));
        var save = Assert.Single(saves);
        Assert.Single(save.BotComponentChanges.OfType<BotComponentInsert>());
        var pushed = Assert.IsAssignableFrom<EnvironmentVariableUpsert>(Assert.Single(save.EnvironmentVariableChanges)).EnvironmentVariable ?? throw new Xunit.Sdk.XunitException("Expected an environment variable.");
        Assert.Equal("source-default", pushed.DefaultValue);
        Assert.Equal("source-current", pushed.ValueComponent?.Value);
        if (targetHasIt)
        {
            Assert.IsType<EnvironmentVariableUpdate>(Assert.Single(save.EnvironmentVariableChanges));
            Assert.Equal(target.DefinitionId, pushed.Id.Value);
            Assert.Equal(target.Version, pushed.Version);
            Assert.Equal(target.DefinitionId, pushed.ValueComponent?.DefinitionId.Value);
            Assert.Equal(targetHasValue ? target.Values[0].ValueId : Guid.Empty, pushed.ValueComponent?.Id.Value);
            Assert.Equal(targetHasValue ? 300 : 0, pushed.ValueComponent?.Version);
        }
        else
        {
            Assert.IsType<EnvironmentVariableInsert>(Assert.Single(save.EnvironmentVariableChanges));
        }
    }

    [Fact]
    public async Task PushLocalChanges_ReferencedEnvironmentVariablesMissingEverywhere_ThrowsBeforeSavingOrWritingCache()
    {
        var (dataverse, lookups) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity + " {Env.cr9f2_other}") });
        ClearCloudCache(fileAccessor);
        lookups.Clear();
        var cache = fileAccessor.Files[CachePath.ToString()];
        var changeToken = fileAccessor.Files[ChangeTokenPath.ToString()];
        var saves = CaptureSaves(island);

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(() => PushLocalChangesAsync(synchronizer, workspace, dataverse));

        Assert.StartsWith("1 workspace file uses environment variables that are missing in the target environment:", failure.Message, StringComparison.Ordinal);
        Assert.Collection(
            failure.Diagnostics.OrderBy(diagnostic => diagnostic.Message, StringComparer.Ordinal),
            diagnostic => AssertMissingDependency(diagnostic, FindTopicPath(fileAccessor), "Environment variable 'cr9f2_other' does not exist in the target environment."),
            diagnostic => AssertMissingDependency(diagnostic, FindTopicPath(fileAccessor), "Environment variable 'cr9f2_shared' does not exist in the target environment."));
        Assert.Equal(new[] { "cr9f2_other", SharedSchemaName }, Assert.Single(lookups).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Empty(saves);
        Assert.Equal(cache, fileAccessor.Files[CachePath.ToString()]);
        Assert.Equal(changeToken, fileAccessor.Files[ChangeTokenPath.ToString()]);
    }

    public static TheoryData<Func<EnvironmentVariableDefinition, EnvironmentVariableDefinition>> SharedEdits => new()
    {
        environmentVariable => environmentVariable.WithDefaultValue("edited"),
        environmentVariable => environmentVariable.WithDisplayName("Edited"),
        environmentVariable => environmentVariable.WithValueComponent(new EnvironmentVariableValue(value: "edited")),
        environmentVariable => environmentVariable.WithType(EnvironmentVariableType.Number),
    };

    [Theory]
    [MemberData(nameof(SharedEdits))]
    public async Task PushLocalChanges_EditedSharedEnvironmentVariable_IsPushedAndCacheKeepsOnlyTheServerCopy(Func<EnvironmentVariableDefinition, EnvironmentVariableDefinition> edit)
    {
        var shared = CreateEnvironmentVariable(SharedSchemaName, value: "current").WithVersion(200);
        var (dataverse, lookups) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, shared);
        lookups.Clear();
        var edited = edit(shared);
        WriteEnvironmentVariableFile(fileAccessor, edited);
        var serverCopy = shared.WithDefaultValue("server-confirmed").WithVersion(201);
        var cacheBeforePush = fileAccessor.Files[CachePath.ToString()];
        var saves = CaptureSaves(island, _ =>
        {
            Assert.Equal(cacheBeforePush, fileAccessor.Files[CachePath.ToString()]);
            return CreateChangeSet("token-2", environmentVariableChanges: new[] { new EnvironmentVariableUpdate(serverCopy) });
        });

        await PushLocalChangesAsync(synchronizer, workspace, dataverse);

        Assert.Equal(new[] { SharedSchemaName }, Assert.Single(lookups));
        var pushed = Assert.IsType<EnvironmentVariableUpdate>(Assert.Single(Assert.Single(saves).EnvironmentVariableChanges)).EnvironmentVariable ?? throw new Xunit.Sdk.XunitException("Expected an environment variable.");
        Assert.Equal(edited.DefaultValue, pushed.DefaultValue);
        Assert.Equal(edited.DisplayName, pushed.DisplayName);
        Assert.Equal(edited.Type, pushed.Type);
        Assert.Equal(edited.ValueComponent?.Value, pushed.ValueComponent?.Value);
        Assert.Equal(shared.Id, pushed.Id);
        Assert.Equal(shared.Version, pushed.Version);
        Assert.Equal(shared.ValueComponent?.Id, pushed.ValueComponent?.Id);
        Assert.Equal(shared.Id, pushed.ValueComponent?.DefinitionId);
        Assert.True(serverCopy.Equals(Assert.Single(ReadCache(fileAccessor).EnvironmentVariables), NodeComparison.Structural));
        Assert.True(serverCopy.Equals(ReadEnvironmentVariableFile(fileAccessor, SharedPath), NodeComparison.Structural));
        Assert.False(fileAccessor.Exists(RetargetUploadedEnvironmentVariablesPath));
    }

    [Fact]
    public async Task PushLocalChanges_SharedEnvironmentVariableUpdateWithoutServerConfirmation_DoesNotChangeCache()
    {
        var shared = CreateEnvironmentVariable(SharedSchemaName);
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, shared);
        WriteEnvironmentVariableFile(fileAccessor, shared.WithDefaultValue("edited"));
        var saves = CaptureSaves(island);

        await PushLocalChangesAsync(synchronizer, workspace, dataverse);

        Assert.IsType<EnvironmentVariableUpdate>(Assert.Single(Assert.Single(saves).EnvironmentVariableChanges));
        Assert.True(shared.Equals(Assert.Single(ReadCache(fileAccessor).EnvironmentVariables), NodeComparison.Structural));
    }

    [Theory]
    [InlineData(SharedSchemaName)]
    [InlineData(OwnedSchemaName)]
    public async Task PushLocalChanges_EnvironmentVariableSaveFails_DoesNotChangeCacheOrLocalEdits(string schemaName)
    {
        var environmentVariable = CreateEnvironmentVariable(schemaName, value: "current");
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", $"{{Env.{schemaName}}}") }, environmentVariable);
        WriteEnvironmentVariableFile(fileAccessor, environmentVariable.WithDefaultValue("edited"));
        var cacheBeforePush = fileAccessor.Files[CachePath.ToString()];
        var changeTokenBeforePush = fileAccessor.Files[ChangeTokenPath.ToString()];
        var localBeforePush = fileAccessor.Files[GetPath(schemaName).ToString()];
        island.Setup(service => service.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("save failed"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => PushLocalChangesAsync(synchronizer, workspace, dataverse));

        Assert.Equal("save failed", exception.Message);
        Assert.Equal(cacheBeforePush, fileAccessor.Files[CachePath.ToString()]);
        Assert.Equal(changeTokenBeforePush, fileAccessor.Files[ChangeTokenPath.ToString()]);
        Assert.Equal(localBeforePush, fileAccessor.Files[GetPath(schemaName).ToString()]);
    }

    [Fact]
    public async Task GetLocalChanges_UnchangedOrUnprojectedSharedEnvironmentVariables_AreNotChangedOrDeleted()
    {
        var shared = CreateEnvironmentVariable(SharedSchemaName, value: "current");
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, _, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, shared, CreateEnvironmentVariable("cr9f2_unrelated", value: "other"));

        var (unchanged, _) = await synchronizer.GetLocalChangesAsync(workspace, await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None), CancellationToken.None);
        fileAccessor.Delete(SharedPath);
        var (deletedFile, _) = await synchronizer.GetLocalChangesAsync(workspace, await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None), CancellationToken.None);

        Assert.Empty(unchanged.EnvironmentVariableChanges);
        Assert.Empty(deletedFile.EnvironmentVariableChanges);
    }

    [Fact]
    public async Task PushLocalChanges_TargetEnvironmentVariableWithoutIdentifier_ThrowsBeforeSavingOrWritingCache()
    {
        var target = TargetEnvironmentVariable(SharedSchemaName);
        target.DefinitionId = Guid.Empty;
        var (dataverse, _) = CreateDataverse(target);
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, CreateEnvironmentVariable(SharedSchemaName));
        ClearCloudCache(fileAccessor);
        var cacheBeforePush = fileAccessor.Files[CachePath.ToString()];
        var saves = CaptureSaves(island);

        var exception = await Assert.ThrowsAsync<WorkspaceValidationException>(() => PushLocalChangesAsync(synchronizer, workspace, dataverse));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal(SharedPath.ToString(), diagnostic.FilePath);
        Assert.Equal($"Existing environment variable '{SharedSchemaName}' has no cloud identifier.", diagnostic.Message);
        Assert.Empty(saves);
        Assert.Equal(cacheBeforePush, fileAccessor.Files[CachePath.ToString()]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RetargetThenPush_SharedEnvironmentVariableTheTargetHolds_OverwritesTheTargetOnlyWithALocalEdit(bool edited, bool legacyCloudCache)
    {
        var source = CreateEnvironmentVariable(SharedSchemaName, defaultValue: "source-default", value: "source-current");
        var target = CreateEnvironmentVariable(SharedSchemaName, defaultValue: "target-default", value: "target-current").WithVersion(200);
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(CreateDataverse().Dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, source);
        if (edited)
        {
            WriteEnvironmentVariableFile(fileAccessor, source.WithDefaultValue("local-default"));
        }

        if (legacyCloudCache)
        {
            WriteText(fileAccessor, new AgentFilePath(".mcs/botdefinition.yml"), YamlSerializer.Serialize(ReadCache(fileAccessor)));
            fileAccessor.Delete(CachePath);
        }

        synchronizer.PersistRetargetBackup(workspace, synchronizer.ResetRemoteBindingState(workspace));
        SetupRemoteChanges(island, CreateChangeSet("token-2", environmentVariableChanges: new[] { new EnvironmentVariableInsert(target) }, bot: CreateBotEntity()));
        var (dataverse, _) = CreateDataverse(TargetEnvironmentVariable(SharedSchemaName));
        await RetargetSyncAsync(synchronizer, workspace, dataverse, updateWorkspaceDirectory: false);
        Assert.Equal(edited, fileAccessor.Exists(SharedPath));
        var saves = CaptureSaves(island);

        await PushLocalChangesAsync(synchronizer, workspace, dataverse);

        var environmentVariableChanges = Assert.Single(saves).EnvironmentVariableChanges;
        if (edited)
        {
            var pushed = Assert.IsType<EnvironmentVariableUpdate>(Assert.Single(environmentVariableChanges)).EnvironmentVariable ?? throw new Xunit.Sdk.XunitException("Expected an environment variable.");
            Assert.Equal("local-default", pushed.DefaultValue);
            Assert.Equal("source-current", pushed.ValueComponent?.Value);
            Assert.Equal(target.Id, pushed.Id);
            Assert.Equal(target.Version, pushed.Version);
            Assert.Equal(target.ValueComponent?.Id, pushed.ValueComponent?.Id);
            Assert.Equal(target.Id, pushed.ValueComponent?.DefinitionId);
        }
        else
        {
            Assert.Empty(environmentVariableChanges);
        }
    }

    [Fact]
    public async Task RetargetRollback_AfterThePushConfirmsLocalContent_KeepsTheEditMadeDuringTheRetargetAndRestoresTheUneditedSourceCopies()
    {
        var target = CreateEnvironmentVariable(SharedSchemaName, defaultValue: "target").WithVersion(200);
        var ownedTarget = CreateEnvironmentVariable(OwnedSchemaName, defaultValue: "target").WithVersion(200);
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(CreateDataverse().Dataverse, new[] { CreateTopic("Greeting", SharedActivity + " {Env.cr9f2_other}") }, CreateEnvironmentVariable(SharedSchemaName, defaultValue: "source"), CreateEnvironmentVariable("cr9f2_other", defaultValue: "source"), CreateEnvironmentVariable(OwnedSchemaName, defaultValue: "source"));
        var otherSourceCopy = fileAccessor.Files[GetPath("cr9f2_other").ToString()];
        var ownedSourceCopy = fileAccessor.Files[GetPath(OwnedSchemaName).ToString()];
        synchronizer.PersistRetargetBackup(workspace, synchronizer.ResetRemoteBindingState(workspace));
        SetupRemoteChanges(island, CreateChangeSet("token-2", environmentVariableChanges: new[] { new EnvironmentVariableInsert(target), new EnvironmentVariableInsert(CreateEnvironmentVariable("cr9f2_other", defaultValue: "target")), new EnvironmentVariableInsert(ownedTarget) }, bot: CreateBotEntity()));
        var (dataverse, _) = CreateDataverse(() => WriteEnvironmentVariableFile(fileAccessor, ReadEnvironmentVariableFile(fileAccessor, SharedPath).WithDefaultValue("edited during the retarget")), TargetEnvironmentVariable(SharedSchemaName), TargetEnvironmentVariable("cr9f2_other"));
        await RetargetSyncAsync(synchronizer, workspace, dataverse, updateWorkspaceDirectory: false);
        await PullAsync(synchronizer, island, workspace, dataverse, CreateChangeSet("token-3"));
        Assert.Equal("target", ReadEnvironmentVariableFile(fileAccessor, GetPath("cr9f2_other")).DefaultValue);
        var serverCopy = target.WithDefaultValue("edited during the retarget").WithVersion(201);
        var ownedServerCopy = ownedTarget.WithDefaultValue("source").WithVersion(201);
        CaptureSaves(island, pushed => CreateChangeSet("token-4", ConfirmComponentChanges(pushed), new[] { new EnvironmentVariableUpdate(serverCopy), new EnvironmentVariableUpdate(ownedServerCopy) }));
        await PushLocalChangesAsync(synchronizer, workspace, dataverse);
        Assert.True(serverCopy.Equals(Assert.Single(ReadCache(fileAccessor).EnvironmentVariables, environmentVariable => environmentVariable.SchemaName.Value == SharedSchemaName), NodeComparison.Structural));
        Assert.True(serverCopy.Equals(ReadEnvironmentVariableFile(fileAccessor, SharedPath), NodeComparison.Structural));
        Assert.True(ownedServerCopy.Equals(ReadEnvironmentVariableFile(fileAccessor, GetPath(OwnedSchemaName)), NodeComparison.Structural));

        Assert.True(synchronizer.FinalizeRetarget(workspace, pushSucceeded: false));

        Assert.Equal("edited during the retarget", ReadEnvironmentVariableFile(fileAccessor, SharedPath).DefaultValue);
        Assert.Equal(otherSourceCopy, fileAccessor.Files[GetPath("cr9f2_other").ToString()]);
        Assert.Equal(ownedSourceCopy, fileAccessor.Files[GetPath(OwnedSchemaName).ToString()]);
        Assert.False(fileAccessor.Exists(RetargetUploadedEnvironmentVariablesPath));
    }

    [Fact]
    public async Task RetargetRollback_AfterEachOfTwoPushesConfirmsAnEditMadeDuringTheRetarget_KeepsBothEdits()
    {
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(CreateDataverse().Dataverse, new[] { CreateTopic("Greeting", SharedActivity + " {Env.cr9f2_other}") }, CreateEnvironmentVariable(SharedSchemaName, defaultValue: "source"), CreateEnvironmentVariable("cr9f2_other", defaultValue: "source"));
        synchronizer.PersistRetargetBackup(workspace, synchronizer.ResetRemoteBindingState(workspace));
        SetupRemoteChanges(island, CreateChangeSet("token-2", bot: CreateBotEntity()));
        var (dataverse, _) = CreateDataverse();
        await RetargetSyncAsync(synchronizer, workspace, dataverse, updateWorkspaceDirectory: false);
        CaptureSaves(island, pushed => CreateChangeSet("token-3", ConfirmComponentChanges(pushed), pushed.EnvironmentVariableChanges));
        foreach (var path in new[] { SharedPath, GetPath("cr9f2_other") })
        {
            WriteEnvironmentVariableFile(fileAccessor, ReadEnvironmentVariableFile(fileAccessor, path).WithDefaultValue("edited during the retarget"));
            await PushLocalChangesAsync(synchronizer, workspace, dataverse);
        }

        Assert.True(synchronizer.FinalizeRetarget(workspace, pushSucceeded: false));

        Assert.Equal("edited during the retarget", ReadEnvironmentVariableFile(fileAccessor, SharedPath).DefaultValue);
        Assert.Equal("edited during the retarget", ReadEnvironmentVariableFile(fileAccessor, GetPath("cr9f2_other")).DefaultValue);
    }

    [Fact]
    public async Task PushLocalChanges_CreatedSharedEnvironmentVariable_IsCachedAndProjectedFromTheServerResponse()
    {
        var shared = CreateEnvironmentVariable(SharedSchemaName);
        var serverCopy = shared.WithId(new EnvironmentVariableDefinitionId(Guid.NewGuid()));
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, shared);
        ClearCloudCache(fileAccessor);
        CaptureSaves(island, pushed => CreateChangeSet("token-2", ConfirmComponentChanges(pushed), new[] { new EnvironmentVariableInsert(serverCopy) }));

        await PushLocalChangesAsync(synchronizer, workspace, dataverse);

        Assert.Equal(serverCopy.Id, Assert.Single(ReadCache(fileAccessor).EnvironmentVariables).Id);
        Assert.Equal(serverCopy.Id, ReadEnvironmentVariableFile(fileAccessor, SharedPath).Id);
    }

    [Fact]
    public async Task PushLocalChanges_ExistingTargetSharedEnvironmentVariable_IsCachedFromTheServerResponse()
    {
        var source = CreateEnvironmentVariable(SharedSchemaName, defaultValue: "source");
        var target = TargetEnvironmentVariable(SharedSchemaName);
        var serverCopy = source.WithId(new EnvironmentVariableDefinitionId(target.DefinitionId)).WithDefaultValue("server-confirmed").WithVersion(201);
        var (dataverse, _) = CreateDataverse(target);
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, source);
        ClearCloudCache(fileAccessor);
        CaptureSaves(island, pushed => CreateChangeSet("token-2", ConfirmComponentChanges(pushed), new[] { new EnvironmentVariableUpdate(serverCopy) }));

        await PushLocalChangesAsync(synchronizer, workspace, dataverse);

        Assert.True(serverCopy.Equals(Assert.Single(ReadCache(fileAccessor).EnvironmentVariables), NodeComparison.Structural));
        Assert.True(serverCopy.Equals(ReadEnvironmentVariableFile(fileAccessor, SharedPath), NodeComparison.Structural));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PushChangeset_ReferencedSharedEnvironmentVariable_UsesItsFileForCreateOrUpdate(bool targetHasIt)
    {
        var shared = CreateEnvironmentVariable(SharedSchemaName);
        var topic = CreateTopic("Greeting", SharedActivity);
        var target = TargetEnvironmentVariable(SharedSchemaName);
        var (dataverse, lookups) = targetHasIt ? CreateDataverse(target) : CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { topic }, shared);
        ClearCloudCache(fileAccessor);
        lookups.Clear();
        var saves = CaptureSaves(island);

        await synchronizer.PushChangesetAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), CreateChangeSet("token-1", new[] { new BotComponentInsert(topic) }), dataverse.Object, Guid.NewGuid(), null, default, CancellationToken.None);

        Assert.Equal(new[] { SharedSchemaName }, Assert.Single(lookups));
        var pushedChange = Assert.Single(Assert.Single(saves).EnvironmentVariableChanges);
        var pushed = Assert.IsAssignableFrom<EnvironmentVariableUpsert>(pushedChange).EnvironmentVariable ?? throw new Xunit.Sdk.XunitException("Expected an environment variable.");
        Assert.Equal(shared.DefaultValue, pushed.DefaultValue);
        if (targetHasIt)
        {
            Assert.IsType<EnvironmentVariableUpdate>(pushedChange);
            Assert.Equal(target.DefinitionId, pushed.Id.Value);
        }
        else
        {
            Assert.IsType<EnvironmentVariableInsert>(pushedChange);
            Assert.True(shared.Equals(pushed, NodeComparison.Structural));
        }
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("edited")]
    [InlineData("unreadable")]
    public async Task PushChangeset_WhenThePushedTopicStopsReferencingASharedEnvironmentVariable_RemovesItsFileOnlyWhenItHoldsNoUnsentContent(string localFile)
    {
        var shared = CreateEnvironmentVariable(SharedSchemaName);
        var topic = CreateTopic("Greeting", SharedActivity);
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { topic }, shared);
        if (localFile == "edited")
        {
            WriteEnvironmentVariableFile(fileAccessor, shared.WithDefaultValue("edited"));
        }
        else if (localFile == "unreadable")
        {
            WriteText(fileAccessor, SharedPath, McsConflictMarkers.Build("defaultValue: local", "defaultValue: cloud"));
        }

        var expectedContent = localFile == "unchanged" ? null : fileAccessor.Files[SharedPath.ToString()];
        CaptureSaves(island);

        await synchronizer.PushChangesetAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), CreateChangeSet("token-1", new[] { new BotComponentUpdate(CreateTopic("Greeting", UnreferencedActivity).WithId(topic.Id)) }), dataverse.Object, Guid.NewGuid(), null, default, CancellationToken.None);

        Assert.Equal(expectedContent, fileAccessor.Files.TryGetValue(SharedPath.ToString(), out var content) ? content : null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PushChangeset_WithoutEnvironmentVariableChanges_KeepsAnUnsentOwnedEnvironmentVariableEditOrDelete(bool deleteFile)
    {
        var owned = CreateEnvironmentVariable(OwnedSchemaName, value: "current");
        var topic = CreateTopic("Greeting", UnreferencedActivity);
        var (dataverse, _) = CreateDataverse();
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { topic }, owned);
        if (deleteFile)
        {
            fileAccessor.Delete(GetPath(OwnedSchemaName));
        }
        else
        {
            WriteEnvironmentVariableFile(fileAccessor, owned.WithDefaultValue("edited"));
        }

        var expectedContent = fileAccessor.Files.TryGetValue(GetPath(OwnedSchemaName).ToString(), out var localContent) ? localContent : null;
        CaptureSaves(island);

        await synchronizer.PushChangesetAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), CreateChangeSet("token-1", new[] { new BotComponentUpdate(CreateTopic("Greeting", "Hello").WithId(topic.Id)) }), dataverse.Object, Guid.NewGuid(), null, default, CancellationToken.None);

        Assert.Equal(expectedContent, fileAccessor.Files.TryGetValue(GetPath(OwnedSchemaName).ToString(), out var pushedContent) ? pushedContent : null);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("insert")]
    [InlineData("changeset")]
    public async Task Push_SharedEnvironmentVariableManagedInTheTarget_ThrowsBeforeSaving(string push)
    {
        var shared = CreateEnvironmentVariable(SharedSchemaName);
        var topic = CreateTopic("Greeting", SharedActivity);
        var (dataverse, _) = CreateDataverse(TargetEnvironmentVariable(SharedSchemaName, isManaged: true));
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { topic }, shared);
        WriteEnvironmentVariableFile(fileAccessor, shared.WithDefaultValue("edited"));
        if (push != "update")
        {
            ClearCloudCache(fileAccessor);
        }

        var saves = CaptureSaves(island);

        var exception = await Assert.ThrowsAsync<WorkspaceValidationException>(() => push == "changeset" ? synchronizer.PushChangesetAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), CreateChangeSet("token-1", new[] { new BotComponentInsert(topic) }), dataverse.Object, Guid.NewGuid(), null, default, CancellationToken.None) : PushLocalChangesAsync(synchronizer, workspace, dataverse));

        AssertManagedDependency(Assert.Single(exception.Diagnostics), SharedSchemaName);
        Assert.Empty(saves);
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData("", false, false)]
    [InlineData("", true, false)]
    [InlineData("cr9f2_other", false, false)]
    [InlineData("CR9F2_SHARED", true, true)]
    public async Task PushLocalChanges_SharedEnvironmentVariableTheTargetHolds_IsOverwrittenOnlyWithoutAnApprovalListOrWhenApproved(string? approvedOverwrites, bool inCloudCache, bool overwrites)
    {
        var shared = CreateEnvironmentVariable(SharedSchemaName);
        var (dataverse, _) = CreateDataverse(TargetEnvironmentVariable(SharedSchemaName));
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, shared);
        WriteEnvironmentVariableFile(fileAccessor, shared.WithDefaultValue("edited"));
        if (!inCloudCache)
        {
            ClearCloudCache(fileAccessor);
        }

        var cacheBeforePush = fileAccessor.Files[CachePath.ToString()];
        var saves = CaptureSaves(island);
        var push = () => PushLocalChangesAsync(synchronizer, workspace, dataverse, approvedEnvironmentVariableOverwrites: approvedOverwrites?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToImmutableArray());

        if (overwrites)
        {
            await push();
            Assert.Equal("edited", Assert.IsType<EnvironmentVariableUpdate>(Assert.Single(Assert.Single(saves).EnvironmentVariableChanges)).EnvironmentVariable?.DefaultValue);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<WorkspaceValidationException>(push);
            Assert.StartsWith("1 workspace file changes an environment variable that is not approved for overwrite in the target environment:", exception.Message, StringComparison.Ordinal);
            var diagnostic = Assert.Single(exception.Diagnostics);
            Assert.Equal(WorkspaceDiagnosticKind.UnapprovedOverwrite, diagnostic.Kind);
            Assert.Equal(SharedPath.ToString(), diagnostic.FilePath);
            Assert.Equal($"Environment variable '{SharedSchemaName}' already exists in the target environment, and overwriting it wasn't approved. Reattach again to approve the overwrite, or discard the change to {SharedPath}.", diagnostic.Message);
            Assert.Empty(saves);
            Assert.Equal(cacheBeforePush, fileAccessor.Files[CachePath.ToString()]);
        }
    }

    [Fact]
    public async Task PushLocalChanges_NothingApprovedForOverwrite_StillPushesOwnedEnvironmentVariablesAndSharedOnesNewToTheTarget()
    {
        var owned = CreateEnvironmentVariable(OwnedSchemaName);
        var (dataverse, _) = CreateDataverse(TargetEnvironmentVariable(OwnedSchemaName));
        var (synchronizer, fileAccessor, island, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, owned);
        WriteEnvironmentVariableFile(fileAccessor, owned.WithDefaultValue("edited"));
        WriteEnvironmentVariableFile(fileAccessor, CreateEnvironmentVariable(SharedSchemaName));
        var saves = CaptureSaves(island);

        await PushLocalChangesAsync(synchronizer, workspace, dataverse, approvedEnvironmentVariableOverwrites: ImmutableArray<string>.Empty);

        var environmentVariableChanges = Assert.Single(saves).EnvironmentVariableChanges;
        Assert.Equal(OwnedSchemaName, Assert.Single(environmentVariableChanges.OfType<EnvironmentVariableUpdate>()).EnvironmentVariable?.SchemaName.Value);
        Assert.Equal(SharedSchemaName, Assert.Single(environmentVariableChanges.OfType<EnvironmentVariableInsert>()).EnvironmentVariable?.SchemaName.Value);
    }

    [Theory]
    [InlineData(true, "Environment variable 'cr9f2_shared' is not in the workspace. Run Get to include it")]
    [InlineData(false, "Environment variable 'cr9f2_shared' does not exist in the target environment.")]
    public async Task ValidateDependencies_ReferencedEnvironmentVariableMissingInTarget_ReportsTheReferencingFileWithoutWriting(bool inCloudCache, string expectedMessage)
    {
        var (dataverse, lookups) = CreateDataverse();
        var (synchronizer, fileAccessor, _, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, inCloudCache ? new[] { CreateEnvironmentVariable(SharedSchemaName) } : Array.Empty<EnvironmentVariableDefinition>());
        fileAccessor.Delete(SharedPath);
        var definition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        lookups.Clear();
        var files = SnapshotFiles(fileAccessor);

        var diagnostics = (await synchronizer.ValidateEnvironmentVariableDependenciesAsync(workspace, definition, isRetarget: true, dataverse.Object, CancellationToken.None)).Diagnostics;

        AssertMissingDependency(Assert.Single(diagnostics), FindTopicPath(fileAccessor), expectedMessage);
        Assert.Equal(new[] { SharedSchemaName }, Assert.Single(lookups));
        Assert.Equal(files, SnapshotFiles(fileAccessor));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidateDependencies_EnvironmentVariableProjectedOrInTarget_ReportsNothing(bool projected)
    {
        var (dataverse, lookups) = CreateDataverse(TargetEnvironmentVariable(SharedSchemaName));
        var (synchronizer, fileAccessor, _, workspace) = await CloneAsync(dataverse, new[] { CreateTopic("Greeting", SharedActivity) }, CreateEnvironmentVariable(SharedSchemaName));
        if (!projected)
        {
            fileAccessor.Delete(SharedPath);
        }

        var definition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        lookups.Clear();

        var check = await synchronizer.ValidateEnvironmentVariableDependenciesAsync(workspace, definition, isRetarget: true, dataverse.Object, CancellationToken.None);

        Assert.Empty(check.Diagnostics);
        Assert.Empty(check.OverwrittenSchemaNames);
        Assert.Equal(projected ? 0 : 1, lookups.Count);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ValidateDependencies_ChangedSharedEnvironmentVariablesTheTargetHolds_ReportManagedOnesAndListTheOthersAsOverwrites(bool hasCloudCache, bool isRetarget)
    {
        var (synchronizer, fileAccessor, _, workspace) = await CloneAsync(CreateDataverse().Dataverse, new[] { CreateTopic("Greeting", SharedActivity + " {Env.cr9f2_managed} {Env.cr9f2_absent} {Env.cr9f2_unedited}") }, CreateEnvironmentVariable(SharedSchemaName), CreateEnvironmentVariable("cr9f2_managed"), CreateEnvironmentVariable("cr9f2_absent"), CreateEnvironmentVariable("cr9f2_unedited"), CreateEnvironmentVariable(OwnedSchemaName));
        foreach (var schemaName in new[] { SharedSchemaName, "cr9f2_managed", "cr9f2_absent", "cr9f2_new", OwnedSchemaName })
        {
            WriteEnvironmentVariableFile(fileAccessor, CreateEnvironmentVariable(schemaName, defaultValue: "edited"));
        }

        var definition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        if (!hasCloudCache)
        {
            fileAccessor.Delete(CachePath);
        }

        var (dataverse, lookups) = CreateDataverse(TargetEnvironmentVariable(SharedSchemaName), TargetEnvironmentVariable("cr9f2_managed", isManaged: true), TargetEnvironmentVariable("cr9f2_unedited"), TargetEnvironmentVariable("cr9f2_new"), TargetEnvironmentVariable(OwnedSchemaName));
        var files = SnapshotFiles(fileAccessor);

        var check = await synchronizer.ValidateEnvironmentVariableDependenciesAsync(workspace, definition, isRetarget, dataverse.Object, CancellationToken.None);

        AssertManagedDependency(Assert.Single(check.Diagnostics), "cr9f2_managed");
        Assert.Equal(hasCloudCache && isRetarget ? new[] { "cr9f2_new", SharedSchemaName } : new[] { "cr9f2_new", SharedSchemaName, "cr9f2_unedited" }, check.OverwrittenSchemaNames);
        Assert.Equal(hasCloudCache && isRetarget ? new[] { "cr9f2_absent", "cr9f2_managed", "cr9f2_new", SharedSchemaName } : new[] { "cr9f2_absent", "cr9f2_managed", "cr9f2_new", SharedSchemaName, "cr9f2_unedited" }, Assert.Single(lookups).Order(StringComparer.Ordinal));
        Assert.Equal(files, SnapshotFiles(fileAccessor));
    }

    [Fact]
    public async Task ValidateDependencies_MissingReferencesAlongsideChangedSharedEnvironmentVariables_ReportOnlyTheMissingReferences()
    {
        var (synchronizer, fileAccessor, _, workspace) = await CloneAsync(CreateDataverse().Dataverse, new[] { CreateTopic("Greeting", SharedActivity + " {Env.cr9f2_managed} {Env.cr9f2_other}") }, CreateEnvironmentVariable(SharedSchemaName), CreateEnvironmentVariable("cr9f2_managed"));
        WriteEnvironmentVariableFile(fileAccessor, CreateEnvironmentVariable(SharedSchemaName, defaultValue: "edited"));
        WriteEnvironmentVariableFile(fileAccessor, CreateEnvironmentVariable("cr9f2_managed", defaultValue: "edited"));
        var definition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        var (dataverse, _) = CreateDataverse(TargetEnvironmentVariable(SharedSchemaName), TargetEnvironmentVariable("cr9f2_managed", isManaged: true));

        var check = await synchronizer.ValidateEnvironmentVariableDependenciesAsync(workspace, definition, isRetarget: true, dataverse.Object, CancellationToken.None);

        AssertMissingDependency(Assert.Single(check.Diagnostics), FindTopicPath(fileAccessor), "Environment variable 'cr9f2_other' does not exist in the target environment.");
        Assert.Empty(check.OverwrittenSchemaNames);
    }

    [Fact]
    public async Task ValidateDependencies_ComponentsFromReferencedCollections_AreLeftToTheCollectionPreflight()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var agentFolder = new DirectoryPath($"c:/test/environment-variables-{Guid.NewGuid():N}/Agent/");
        var collectionTopic = CreateTopic("Collected", "{Env.cr9f2_collected}");
        var collectionAccessor = fileAccessorFactory.Create(agentFolder.ResolveRelativeRef(new RelativeDirectoryPath("../Collection/")));
        var collection = CodeSerializer.Deserialize<BotComponentCollection>("schemaName: bot_componentcollection_cc\ndisplayName: CC") ?? throw new Xunit.Sdk.XunitException("Expected a component collection.");
        WorkspaceSynchronizer.WriteCloudCache(collectionAccessor, new BotComponentCollectionDefinition().WithComponentCollection(collection).WithComponents(new[] { collectionTopic }));
        WriteText(collectionAccessor, new AgentFilePath($"topics/{collectionTopic.SchemaNameString}.mcs.yml"), "mcs.metadata:\n  componentName: Collected\nkind: AdaptiveDialog\n");
        WriteText(fileAccessorFactory.Create(agentFolder), new AgentFilePath("references.mcs.yml"), "componentCollections:\n  - schemaName:\n    directory: ../Collection/\n");
        var collectionOwnedTopic = CreateTopic("Owned", "{Env.cr9f2_ownedbycollection}").ToBuilder();
        collectionOwnedTopic.ParentBotComponentCollectionId = new BotComponentCollectionId(Guid.NewGuid());
        var (dataverse, lookups) = CreateDataverse();

        var diagnostics = (await synchronizer.ValidateEnvironmentVariableDependenciesAsync(agentFolder, new BotDefinition().WithEntity(CreateBotEntity()).WithComponents(new[] { CreateTopic("Greeting", SharedActivity), collectionTopic, collectionOwnedTopic.Build() }), isRetarget: false, dataverse.Object, CancellationToken.None)).Diagnostics;

        Assert.Equal(new[] { SharedSchemaName }, Assert.Single(lookups));
        Assert.Contains($"'{SharedSchemaName}'", Assert.Single(diagnostics).Message, StringComparison.Ordinal);
    }

    private static async Task PushLocalChangesAsync(WorkspaceSynchronizer synchronizer, DirectoryPath workspace, Mock<ISyncDataverseClient> dataverse, DefinitionBase? workspaceDefinition = null, ImmutableArray<string>? approvedEnvironmentVariableOverwrites = null) => await synchronizer.PushLocalChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), workspaceDefinition ?? await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None), dataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, cloudFlowMetadata: null, aiPrompts: ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None, approvedEnvironmentVariableOverwrites: approvedEnvironmentVariableOverwrites);

    private static void AssertMissingDependency(WorkspaceDiagnostic diagnostic, AgentFilePath expectedPath, string expectedMessage)
    {
        Assert.Equal(WorkspaceDiagnosticKind.MissingDependency, diagnostic.Kind);
        Assert.Equal(expectedPath.ToString(), diagnostic.FilePath);
        Assert.StartsWith(expectedMessage, diagnostic.Message, StringComparison.Ordinal);
    }

    private static void AssertManagedDependency(WorkspaceDiagnostic diagnostic, string schemaName)
    {
        Assert.Equal(WorkspaceDiagnosticKind.ManagedDependency, diagnostic.Kind);
        Assert.Equal(GetPath(schemaName).ToString(), diagnostic.FilePath);
        Assert.Equal($"Environment variable '{schemaName}' is managed in the target environment, so it can't be changed. Discard the change to {GetPath(schemaName)}, then try again.", diagnostic.Message);
    }

    private static (string Path, string Content)[] SnapshotFiles(InMemoryFileAccessor fileAccessor) => fileAccessor.Files.OrderBy(file => file.Key, StringComparer.Ordinal).Select(file => (file.Key, Convert.ToBase64String(file.Value))).ToArray();
}
