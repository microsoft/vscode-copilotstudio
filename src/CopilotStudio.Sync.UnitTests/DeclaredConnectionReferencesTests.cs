// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Xunit;
using ProductionFileAccessor = Microsoft.CopilotStudio.McsCore.InMemoryFileAccessor;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public sealed class DeclaredConnectionReferencesTests : IDisposable
{
    private const string ClassicPath = "connectionreferences.mcs.yml";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mcs-declared-refs-" + Guid.NewGuid().ToString("N"));

    private readonly FakeDataverseCloud _cloud = new();

    private readonly WorkspaceSynchronizer _synchronizer = ComponentWriterDefensiveTests.CreateSyncInfrastructure().sync;

    private IFileAccessor _workspace = new ProductionFileAccessor();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private void UseDisk(bool onDisk)
    {
        if (onDisk)
        {
            Directory.CreateDirectory(_directory);
            _workspace = WorkflowWorkspace.OpenWorkspace(_directory);
        }
    }

    private void Write(string relativePath, string content)
    {
        using var stream = _workspace.OpenWrite(new AgentFilePath(relativePath));
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string Declaration(string logicalName, string? connectorInternalId) =>
        connectorInternalId is null
            ? $"connectionReferences:\n  - connectionReferenceLogicalName: {logicalName}\n"
            : $"connectionReferences:\n  - connectionReferenceLogicalName: {logicalName}\n    connectorId: /providers/Microsoft.PowerApps/apis/{connectorInternalId}\n";

    private void WriteClassic(string logicalName, string? connectorInternalId) => Write(ClassicPath, Declaration(logicalName, connectorInternalId));

    private void WriteCli(string logicalName, string? connectorInternalId) => Write($"infrastructure/connections/{logicalName}.sync.yaml", Declaration(logicalName, connectorInternalId));

    private static ConnectionReference Reference(string logicalName, string? connectionId = null) => new ConnectionReference.Builder
    {
        ConnectionReferenceLogicalName = new ConnectionReferenceLogicalName(logicalName),
        ConnectionId = connectionId ?? string.Empty,
    }.Build();

    private static IReadOnlyList<string> Names(ImmutableArray<ConnectionReference> references) => references.Select(reference => reference.ConnectionReferenceLogicalName.Value).ToArray();

    private static IReadOnlyList<string> Sorted(IEnumerable<string> names) => names.OrderBy(name => name, StringComparer.Ordinal).ToArray();

    private Task<ImmutableArray<ConnectionReference>> ProvisionAsync(IReadOnlyDictionary<string, Guid>? pushedConnectorIds = null, CancellationToken cancellationToken = default) =>
        DeclaredConnectionReferences.ProvisionAsync(_workspace, _synchronizer, _cloud.Client.Object, pushedConnectorIds ?? new Dictionary<string, Guid>(), cancellationToken);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Declared_ReadsTheCliLayout(bool onDisk)
    {
        UseDisk(onDisk);
        WriteCli("new_sharedteams_777c895a", "shared_teams");

        Assert.Equal(["new_sharedteams_777c895a"], Names(DeclaredConnectionReferences.Read(_workspace)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Declared_ReadsTheClassicLayout(bool onDisk)
    {
        UseDisk(onDisk);
        WriteClassic("new_sharedmsnweather_08889", "shared_msnweather");

        Assert.Equal(["new_sharedmsnweather_08889"], Names(DeclaredConnectionReferences.Read(_workspace)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Declared_ReadsBothLayoutsClassicFirstAndTakesAReferenceDeclaredTwiceOnce(bool onDisk)
    {
        UseDisk(onDisk);
        WriteClassic("new_sharedteams_777c895a", "shared_teams");
        WriteCli("new_sharedteams_777c895a", "shared_teams");
        WriteCli("crf9a_nb1.cr.shared_sharepointonline.a972", "shared_sharepointonline");

        Assert.Equal(["new_sharedteams_777c895a", "crf9a_nb1.cr.shared_sharepointonline.a972"], Names(DeclaredConnectionReferences.Read(_workspace)));
    }

    [Fact]
    public void Declared_TakesAReferenceDeclaredTwiceForTheSameConnectorInEitherForm()
    {
        Write(ClassicPath, "connectionReferences:\n  - connectionReferenceLogicalName: new_sharedteams_777c895a\n    connectorId: shared_teams\n");
        WriteCli("new_sharedteams_777c895a", "shared_teams");

        Assert.Equal(["new_sharedteams_777c895a"], Names(DeclaredConnectionReferences.Read(_workspace)));
    }

    [Fact]
    public void Declared_ADeclarationNamingNoConnectorDefersToOneThatDoes()
    {
        WriteClassic("new_sharedteams_777c895a", "shared_teams");
        WriteCli("new_sharedteams_777c895a", null);

        Assert.Equal("/providers/Microsoft.PowerApps/apis/shared_teams", Assert.Single(DeclaredConnectionReferences.Read(_workspace)).ConnectorId.Value);
    }

    [Fact]
    public void Declared_AConnectorNamedOnlyByTheSecondDeclarationIsTheOneKept()
    {
        WriteClassic("new_sharedteams_777c895a", null);
        WriteCli("new_sharedteams_777c895a", "shared_teams");

        Assert.Equal("/providers/Microsoft.PowerApps/apis/shared_teams", Assert.Single(DeclaredConnectionReferences.Read(_workspace)).ConnectorId.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Declared_RefusesANameDeclaredForTwoConnectorsRatherThanLettingFileOrderDecide(bool onDisk)
    {
        UseDisk(onDisk);
        WriteClassic("new_sharedteams_777c895a", "shared_teams");
        WriteCli("new_sharedteams_777c895a", "shared_msnweather");

        var failure = Assert.Throws<DeclaredConnectionReferenceException>(() => DeclaredConnectionReferences.Read(_workspace));

        Assert.Equal(DeclaredConnectionReferenceFailure.Conflict, failure.Failure);
        Assert.Equal("new_sharedteams_777c895a", failure.LogicalName);
        Assert.Equal(ClassicPath, failure.FirstPath);
        Assert.Equal("shared_teams", failure.FirstConnectorId);
        Assert.Equal("infrastructure/connections/new_sharedteams_777c895a.sync.yaml", failure.Path);
        Assert.Equal("shared_msnweather", failure.SecondConnectorId);
        Assert.Contains("new_sharedteams_777c895a", failure.Message, StringComparison.Ordinal);
        Assert.Contains("shared_teams", failure.Message, StringComparison.Ordinal);
        Assert.Contains("shared_msnweather", failure.Message, StringComparison.Ordinal);
        Assert.Contains(ClassicPath, failure.Message, StringComparison.Ordinal);
        Assert.Contains("new_sharedteams_777c895a.sync.yaml", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Declared_RefusesTwoCliFilesThatDisagreeAboutOneReference()
    {
        Write("infrastructure/connections/a.sync.yaml", Declaration("cr_shared", "shared_teams"));
        Write("infrastructure/connections/b.sync.yaml", Declaration("cr_shared", "shared_msnweather"));

        var failure = Assert.Throws<DeclaredConnectionReferenceException>(() => DeclaredConnectionReferences.Read(_workspace));

        Assert.Equal(DeclaredConnectionReferenceFailure.Conflict, failure.Failure);
        Assert.Equal("infrastructure/connections/a.sync.yaml", failure.FirstPath);
        Assert.Equal("infrastructure/connections/b.sync.yaml", failure.Path);
    }

    [Fact]
    public void Declared_RefusesOneFileThatDeclaresANameForTwoConnectors()
    {
        Write(ClassicPath, "connectionReferences:\n  - connectionReferenceLogicalName: cr_shared\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_teams\n  - connectionReferenceLogicalName: cr_shared\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_msnweather\n");

        var failure = Assert.Throws<DeclaredConnectionReferenceException>(() => DeclaredConnectionReferences.Read(_workspace));

        Assert.Equal(DeclaredConnectionReferenceFailure.Conflict, failure.Failure);
        Assert.Contains("shared_teams", failure.Message, StringComparison.Ordinal);
        Assert.Contains("shared_msnweather", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Declared_ComparesConnectorsByTheirInternalIdWithoutCase()
    {
        WriteClassic("cr_shared", "shared_teams");
        Write("infrastructure/connections/cr_shared.sync.yaml", "connectionReferences:\n  - connectionReferenceLogicalName: CR_SHARED\n    connectorId: ' /providers/Microsoft.PowerApps/apis/SHARED_TEAMS/ '\n");

        Assert.Equal(["cr_shared"], Names(DeclaredConnectionReferences.Read(_workspace)));
    }

    [Fact]
    public void Declared_ReadsOnlyTheFilesDirectlyInTheConnectionsFolder()
    {
        WriteCli("cr_top", "shared_teams");
        Write("infrastructure/connections/nested/cr_nested.sync.yaml", Declaration("cr_nested", "shared_teams"));
        Write("cr_root.sync.yaml", Declaration("cr_root", "shared_teams"));

        Assert.Equal(["cr_top"], Names(DeclaredConnectionReferences.Read(_workspace)));
    }

    [Fact]
    public void Declared_ADeclarationThatCannotBeOpenedIsTypedAsUnreadable()
    {
        WriteClassic("cr_shared", "shared_teams");
        _workspace = new UnreadableAccessor(_workspace);

        var failure = Assert.Throws<DeclaredConnectionReferenceException>(() => DeclaredConnectionReferences.Read(_workspace));

        Assert.Equal(DeclaredConnectionReferenceFailure.Unreadable, failure.Failure);
        Assert.Equal(ClassicPath, failure.Path);
        Assert.Equal("locked", failure.Reason);
        Assert.IsType<IOException>(failure.InnerException);
    }

    [Fact]
    public async Task Provision_DoesNotCreateAnythingWhenTwoDeclarationsDisagree()
    {
        WriteClassic("new_sharedteams_777c895a", "shared_teams");
        WriteCli("new_sharedteams_777c895a", "shared_msnweather");

        await Assert.ThrowsAsync<DeclaredConnectionReferenceException>(() => ProvisionAsync());

        Assert.Empty(_cloud.EnsuredLogicalNames);
    }

    [Fact]
    public void Declared_IsEmptyWhenTheWorkspaceDeclaresNone()
    {
        Assert.Empty(DeclaredConnectionReferences.Read(_workspace));
    }

    [Fact]
    public void Merge_AddsTheReferencesTheWorkflowUpsertDidNotReport()
    {
        Assert.Equal(["new_sharedteams_777c895a", "crf9a_nb1.cr.shared_sharepointonline.a972"], Names(DeclaredConnectionReferences.Merge([Reference("new_sharedteams_777c895a")], [Reference("crf9a_nb1.cr.shared_sharepointonline.a972")])));
    }

    [Fact]
    public void Merge_KeepsTheFirstRecordOfASharedName()
    {
        Assert.Equal("preferred", Assert.Single(DeclaredConnectionReferences.Merge([Reference("new_sharedteams_777c895a", "preferred")], [Reference("NEW_SHAREDTEAMS_777C895A", "additional")])).ConnectionId);
    }

    [Fact]
    public void Merge_WithDefaultArrays_DoesNotThrow()
    {
        Assert.Empty(DeclaredConnectionReferences.Merge(default, default));
        Assert.Equal(["new_sharedteams_777c895a"], Names(DeclaredConnectionReferences.Merge(default, [Reference("new_sharedteams_777c895a")])));
        Assert.Equal(["new_sharedteams_777c895a"], Names(DeclaredConnectionReferences.Merge([Reference("new_sharedteams_777c895a")], default)));
    }

    [Fact]
    public void WithProvisioned_AddsTheConfirmedReferencesAndKeepsTheReportedWorkflows()
    {
        var reported = new CloudFlowMetadata
        {
            Workflows = [new CloudFlowDefinition.Builder { DisplayName = "flow-1" }.Build()],
            ConnectionReferences = [Reference("new_sharedteams_777c895a")],
            Succeeded = false,
        };

        var seeded = DeclaredConnectionReferences.WithProvisioned(reported, [Reference("cli.cr.sharepoint")]);

        Assert.Equal(reported.Workflows, seeded.Workflows);
        Assert.True(seeded.WorkflowsReported);
        Assert.False(seeded.Succeeded);
        Assert.Equal(["cli.cr.sharepoint", "new_sharedteams_777c895a"], Names(seeded.ConnectionReferences));
    }

    [Fact]
    public void WithProvisioned_WithNothingConfirmed_LeavesTheReportedMetadataAlone()
    {
        var reported = new CloudFlowMetadata { ConnectionReferences = [Reference("new_sharedteams_777c895a")] };

        Assert.Same(reported, DeclaredConnectionReferences.WithProvisioned(reported, default));
        Assert.Same(reported, DeclaredConnectionReferences.WithProvisioned(reported, ImmutableArray<ConnectionReference>.Empty));
        Assert.Null(DeclaredConnectionReferences.WithProvisioned(null, []));
    }

    [Fact]
    public void WithProvisioned_WithNoUpsertReport_CarriesTheConfirmedReferencesAndReportsNoWorkflows()
    {
        var seeded = DeclaredConnectionReferences.WithProvisioned(null, [Reference("cli.cr.sharepoint")]);

        Assert.NotNull(seeded);
        Assert.False(seeded.WorkflowsReported);
        Assert.True(seeded.Succeeded);
        Assert.Equal(["cli.cr.sharepoint"], Names(seeded.ConnectionReferences));
    }

    [Fact]
    public async Task ANullWorkspaceOrCollaboratorIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => DeclaredConnectionReferences.Read(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => DeclaredConnectionReferences.ProvisionAsync(_workspace, null!, _cloud.Client.Object, new Dictionary<string, Guid>(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => DeclaredConnectionReferences.ProvisionAsync(_workspace, _synchronizer, null!, new Dictionary<string, Guid>(), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provision_CachelessClassicCreatesItsDeclaredReferences(bool onDisk)
    {
        UseDisk(onDisk);
        WriteClassic("classic.shared_teams", "shared_teams");

        var confirmed = await ProvisionAsync();

        Assert.False(_workspace.Exists(new AgentFilePath(".mcs/botdefinition.json")));
        Assert.Equal(["classic.shared_teams"], _cloud.EnsuredLogicalNames);
        var reference = Assert.Single(confirmed);
        Assert.Equal(_cloud.ConnectionReferences["classic.shared_teams"].ConnectionReferenceId, reference.Id.Value);
        Assert.Equal("/providers/Microsoft.PowerApps/apis/shared_teams", reference.ConnectorId.Value);
    }

    [Fact]
    public async Task Provision_CliConfirmsAllFiveReferencesIncludingThoseNotUsedByWorkflows()
    {
        WriteCli("new_approvals", "shared_advancedapprovals");
        WriteCli("new_weather", "shared_msnweather");
        WriteCli("new_teams", "shared_teams");
        WriteCli("cli.cr.sharepoint", "shared_sharepointonline");
        WriteCli("cli.cr.teams", "shared_teams");

        var confirmed = await ProvisionAsync();
        var repeated = await ProvisionAsync();

        Assert.Equal(5, _cloud.ConnectionReferences.Count);
        Assert.Equal(5, confirmed.Length);
        Assert.Equal(confirmed.Select(reference => reference.Id.Value), repeated.Select(reference => reference.Id.Value));
        Assert.All(confirmed, reference => Assert.NotEqual(Guid.Empty, reference.Id.Value));
    }

    [Fact]
    public async Task Provision_RecordsEachReferenceExactlyAsAFreshReadOfTheTargetDescribesIt()
    {
        WriteClassic("classic.shared_teams", "shared_teams");
        var row = _cloud.AddConnectionReference("classic.shared_teams", "/providers/Microsoft.PowerApps/apis/shared_teams", "target-connection");

        var confirmed = Assert.Single(await ProvisionAsync());
        var fresh = WorkspaceSynchronizer.ToConnectionReference(row);

        Assert.Equal(fresh.Id, confirmed.Id);
        Assert.Equal(fresh.Version, confirmed.Version);
        Assert.Equal(fresh.ConnectionReferenceLogicalName, confirmed.ConnectionReferenceLogicalName);
        Assert.Equal(fresh.ConnectorId, confirmed.ConnectorId);
        Assert.Equal(fresh.ConnectionId, confirmed.ConnectionId);
        Assert.Equal(row.Version, confirmed.Version);
    }

    [Fact]
    public async Task Provision_PreservesTheExistingTargetsConnectionAndRowId()
    {
        WriteClassic("classic.shared_teams", "shared_teams");
        var row = _cloud.AddConnectionReference("classic.shared_teams", "/providers/Microsoft.PowerApps/apis/shared_teams", "target-connection");

        var reference = Assert.Single(await ProvisionAsync());

        Assert.Equal(row.ConnectionReferenceId, reference.Id.Value);
        Assert.Equal("target-connection", reference.ConnectionId);
    }

    [Fact]
    public async Task Provision_ForwardsThePushedConnectorRowIdToTheSdk()
    {
        WriteClassic("classic.shared_teams", "shared_teams");
        var rowId = Guid.NewGuid();

        await ProvisionAsync(new Dictionary<string, Guid> { ["shared_teams"] = rowId });

        Assert.Equal(rowId, Assert.Single(_cloud.EnsuredConnectionReferences).CustomConnectorRowId);
    }

    [Fact]
    public async Task Provision_TouchesNothingWhenTheWorkspaceDeclaresNoReferences()
    {
        _cloud.QueryThrows = new InvalidOperationException("must not be read");

        Assert.Empty(await ProvisionAsync());
        Assert.Empty(_cloud.Client.Invocations);
    }

    [Fact]
    public async Task Provision_DoesNotRecreateAReferenceTheWorkspaceNoLongerDeclares()
    {
        WriteClassic("classic.shared_teams", "shared_teams");
        _cloud.AddConnectionReference("retired.shared_msnweather", "/providers/Microsoft.PowerApps/apis/shared_msnweather");

        var confirmed = await ProvisionAsync();

        Assert.Equal(["classic.shared_teams"], _cloud.EnsuredLogicalNames);
        Assert.Equal(["classic.shared_teams"], Names(confirmed));
    }

    [Fact]
    public async Task Provision_RequestsTheConnectorTheWorkspaceDeclaresRatherThanTheOneTheTargetHolds()
    {
        WriteClassic("classic.connector", "shared_teams");

        await ProvisionAsync();

        Assert.Equal("/providers/Microsoft.PowerApps/apis/shared_teams", Assert.Single(_cloud.EnsuredConnectionReferences).ConnectorId);
    }

    [Fact]
    public async Task Provision_RejectsARowStillPointingAtTheConnectorTheDeclarationReplaced()
    {
        WriteClassic("classic.connector", "shared_teams");
        _cloud.AddConnectionReference("classic.connector", "/providers/Microsoft.PowerApps/apis/shared_msnweather");

        var failure = await Assert.ThrowsAsync<DeclaredConnectionReferenceException>(() => ProvisionAsync());

        Assert.Equal(DeclaredConnectionReferenceFailure.NotConfirmed, failure.Failure);
        Assert.Contains("classic.connector", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provision_RejectsARowWithoutAnId()
    {
        WriteClassic("classic.shared_teams", "shared_teams");
        _cloud.AddConnectionReference("classic.shared_teams", "/providers/Microsoft.PowerApps/apis/shared_teams").ConnectionReferenceId = Guid.Empty;

        Assert.Equal(DeclaredConnectionReferenceFailure.NotConfirmed, (await Assert.ThrowsAsync<DeclaredConnectionReferenceException>(() => ProvisionAsync())).Failure);
    }

    [Theory]
    [InlineData("shared_contoso-5f0123456789abcdef", "/providers/Microsoft.PowerApps/apis/shared_contoso-5ffedcba9876543210", true)]
    [InlineData("shared_contoso-5f0123456789abcdef", "/providers/Microsoft.PowerApps/apis/shared_fabrikam-5ffedcba9876543210", false)]
    [InlineData("shared_contoso-5f0123456789abcdeg", "/providers/Microsoft.PowerApps/apis/shared_contoso-5ffedcba9876543210", false)]
    [InlineData("shared_contoso-5f0123", "/providers/Microsoft.PowerApps/apis/shared_contoso-5f0124", false)]
    public async Task Provision_AcceptsOnlyACustomConnectorTheTargetReidentifiedForItsOwnEnvironment(string declaredConnector, string servedConnector, bool accepted)
    {
        WriteClassic("classic.custom", declaredConnector);
        _cloud.AddConnectionReference("classic.custom", servedConnector);

        if (accepted)
        {
            Assert.Equal(["classic.custom"], Names(await ProvisionAsync()));
        }
        else
        {
            Assert.Equal(DeclaredConnectionReferenceFailure.NotConfirmed, (await Assert.ThrowsAsync<DeclaredConnectionReferenceException>(() => ProvisionAsync())).Failure);
        }
    }

    [Fact]
    public async Task Provision_RejectsADeclarationThatNamesNoConnector()
    {
        WriteClassic("classic.unnamed", null);
        _cloud.AddConnectionReference("classic.unnamed", "/providers/Microsoft.PowerApps/apis/shared_teams");

        Assert.Equal(DeclaredConnectionReferenceFailure.NotConfirmed, (await Assert.ThrowsAsync<DeclaredConnectionReferenceException>(() => ProvisionAsync())).Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provision_RejectsUnconfirmedReferencesEvenWhenTheSdkReturnsSuccess(bool failed)
    {
        WriteClassic("classic.shared_teams", "shared_teams");
        _cloud.ProvisioningThrows = failed ? new InvalidOperationException("denied") : null;
        _cloud.OmitProvisionedReferences = !failed;

        var failure = await Assert.ThrowsAsync<DeclaredConnectionReferenceException>(() => ProvisionAsync());

        Assert.Equal(DeclaredConnectionReferenceFailure.NotConfirmed, failure.Failure);
        Assert.Contains("classic.shared_teams", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provision_DoesNotConvertAReadFailureToAnEmptyBaseline()
    {
        WriteClassic("classic.shared_teams", "shared_teams");
        var expected = new HttpRequestException("lookup failed");
        _cloud.QueryThrows = expected;

        Assert.Same(expected, await Assert.ThrowsAsync<HttpRequestException>(() => ProvisionAsync()));
    }

    [Fact]
    public async Task Provision_HonorsCancellationBeforeCreatingReferences()
    {
        WriteClassic("classic.shared_teams", "shared_teams");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProvisionAsync(cancellationToken: cancellation.Token));

        Assert.Empty(_cloud.EnsuredLogicalNames);
    }

    internal sealed class UnreadableAccessor(IFileAccessor inner, Exception? failure = null) : IFileAccessor
    {
        public bool Exists(AgentFilePath path) => inner.Exists(path);

        public void CreateHiddenDirectory(AgentFilePath path) => inner.CreateHiddenDirectory(path);

        public Stream OpenWrite(AgentFilePath path) => inner.OpenWrite(path);

        public Stream OpenRead(AgentFilePath path) => throw failure ?? new IOException("locked");

        public void Delete(AgentFilePath path) => inner.Delete(path);

        public void DeleteDirectory(AgentFilePath path) => inner.DeleteDirectory(path);

        public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath) => inner.Replace(sourcePath, targetPath);

        public IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*") => inner.ListFiles(relativeFolder, filePattern);
    }
}
