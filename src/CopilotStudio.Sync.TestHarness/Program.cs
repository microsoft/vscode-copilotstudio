// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.TestHarness;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;

var rootCommand = new RootCommand("CopilotStudio.Sync test harness — exercises the shared library against a live tenant");

// ── clone ──────────────────────────────────────────────────────────────
var cloneCommand = new Command("clone", "Clone an agent from a live tenant");
var environmentOption = new Option<string>("--environment", "Dataverse org URL (e.g., https://org.crm.dynamics.com)") { IsRequired = true };
var environmentIdOption = new Option<string?>("--environment-id", () => Environment.GetEnvironmentVariable("COPILOT_TEST_ENVIRONMENT_ID"), "Power Platform environment ID (falls back to COPILOT_TEST_ENVIRONMENT_ID env var)");
var agentSchemaNameOption = new Option<string>("--agent-schema-name", "Schema name of the agent to clone") { IsRequired = true };
var outputOption = new Option<string>("--output", () => "./workspace", "Output workspace directory");

cloneCommand.AddOption(environmentOption);
cloneCommand.AddOption(environmentIdOption);
cloneCommand.AddOption(agentSchemaNameOption);
cloneCommand.AddOption(outputOption);

cloneCommand.SetHandler(async (string environment, string? environmentId, string agentSchemaName, string output) =>
{
    if (string.IsNullOrEmpty(environmentId))
    {
        Console.Error.WriteLine("Error: --environment-id is required (or set COPILOT_TEST_ENVIRONMENT_ID env var).");
        Environment.ExitCode = 1;
        return;
    }

    var environmentUrl = new Uri(environment.TrimEnd('/') + "/");

    await using var services = await HostServices.BuildAsync(environmentUrl);

    var dataverseClient = services.GetRequiredService<ISyncDataverseClient>();
    dataverseClient.SetDataverseUrl(environmentUrl.ToString());

    Console.WriteLine($"Looking up agent '{agentSchemaName}'...");
    var agentId = await dataverseClient.GetAgentIdBySchemaNameAsync(agentSchemaName, CancellationToken.None);
    Console.WriteLine($"Agent ID: {agentId}");

    var agentInfo = await dataverseClient.GetAgentInfoAsync(agentId, CancellationToken.None);
    Console.WriteLine($"Agent: {agentInfo.DisplayName} (schema: {agentInfo.SchemaName})");

    Console.WriteLine("Fetching solution versions...");
    var solutionVersions = await dataverseClient.GetSolutionVersionsAsync(CancellationToken.None);

    var tenantId = Guid.Parse(GetRequiredEnvVar("COPILOT_TEST_TENANT_ID"));

    var syncInfo = new AgentSyncInfo
    {
        AgentId = agentId,
        DataverseEndpoint = environmentUrl,
        EnvironmentId = environmentId,
        SolutionVersions = solutionVersions,
        AccountInfo = new AccountInfo
        {
            TenantId = tenantId,
        },
        // AgentManagementEndpoint intentionally omitted — not needed while Island
        // cross-validation is disabled. Each host derives it when needed.
    };

    var operationContextProvider = services.GetRequiredService<IOperationContextProvider>();
    var operationContext = await operationContextProvider.GetAsync(syncInfo);

    var workspaceFolder = new DirectoryPath(Path.GetFullPath(output).Replace('\\', '/'));
    Directory.CreateDirectory(workspaceFolder.ToString().TrimEnd('/'));

    var synchronizer = services.GetRequiredService<IWorkspaceSynchronizer>();

    Console.WriteLine($"Saving sync info to {workspaceFolder}...");
    await synchronizer.SaveSyncInfoAsync(workspaceFolder, syncInfo);

    Console.WriteLine("Cloning agent...");
    var referenceTracker = new ReferenceTracker();
    await synchronizer.CloneChangesAsync(workspaceFolder, referenceTracker, operationContext, dataverseClient, agentId, CancellationToken.None);

    Console.WriteLine("Syncing workspace metadata...");
    await synchronizer.SyncWorkspaceAsync(workspaceFolder, operationContext, null, true, dataverseClient, agentId, null, CancellationToken.None);

    await synchronizer.ApplyTouchupsAsync(workspaceFolder, referenceTracker, CancellationToken.None);

    Console.WriteLine();
    Console.WriteLine($"Clone complete. Workspace: {workspaceFolder}");
    PrintWorkspaceFiles(workspaceFolder);

}, environmentOption, environmentIdOption, agentSchemaNameOption, outputOption);

// ── push ───────────────────────────────────────────────────────────────
var pushCommand = new Command("push", "Push local workspace changes to the cloud");
var pushWorkspaceOption = new Option<string>("--workspace", "Workspace directory (must contain .mcs/conn.json)") { IsRequired = true };
pushCommand.AddOption(pushWorkspaceOption);

pushCommand.SetHandler(async (string workspace) =>
{
    var workspaceFolder = new DirectoryPath(Path.GetFullPath(workspace).Replace('\\', '/'));

    // We need the environment URL from conn.json before building the container.
    // Read it via a temporary synchronizer to get sync info, then build the full container.
    var syncInfoJson = Path.Combine(workspaceFolder.ToString().TrimEnd('/'), ".mcs", "conn.json");
    if (!File.Exists(syncInfoJson))
    {
        Console.Error.WriteLine($"Error: No .mcs/conn.json found in {workspace}. Run 'clone' first.");
        Environment.ExitCode = 1;
        return;
    }

    // Parse environment URL from conn.json to build the DI container
    var connJson = await File.ReadAllTextAsync(syncInfoJson);
    var syncInfoPreview = System.Text.Json.JsonSerializer.Deserialize<AgentSyncInfo>(connJson)
        ?? throw new InvalidOperationException("Failed to parse .mcs/conn.json");

    await using var services = await HostServices.BuildAsync(syncInfoPreview.DataverseEndpoint);

    var synchronizer = services.GetRequiredService<IWorkspaceSynchronizer>();
    var dataverseClient = services.GetRequiredService<ISyncDataverseClient>();
    var operationContextProvider = services.GetRequiredService<IOperationContextProvider>();

    var syncInfo = await synchronizer.GetSyncInfoAsync(workspaceFolder);
    dataverseClient.SetDataverseUrl(syncInfo.DataverseEndpoint.ToString());
    SetIslandContextIfAvailable(services, syncInfo);

    var operationContext = await operationContextProvider.GetAsync(syncInfo);

    Console.WriteLine("Reading workspace definition...");
    var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspaceFolder, CancellationToken.None, checkKnowledgeFiles: true);

    Console.WriteLine("Detecting local changes...");
    var (localChangeset, localChanges) = await synchronizer.GetLocalChangesAsync(workspaceFolder, localDefinition, dataverseClient, syncInfo.AgentId, CancellationToken.None);

    if (localChanges.IsEmpty)
    {
        Console.WriteLine("No local changes detected.");
        return;
    }

    Console.WriteLine($"Found {localChanges.Length} change(s):");
    foreach (var change in localChanges)
    {
        Console.WriteLine($"  {change.ChangeType}: {change.Name} ({change.ChangeKind})");
    }

    CloudFlowMetadata? cloudFlowMetadata = null;
    if (syncInfo.AgentId.HasValue)
    {
        (_, cloudFlowMetadata) = await synchronizer.UpsertWorkflowForAgentAsync(
            workspaceFolder, dataverseClient, syncInfo.AgentId, CancellationToken.None);
    }

    await synchronizer.ProvisionConnectionReferencesAsync(localDefinition, dataverseClient, CancellationToken.None);

    Console.WriteLine("Pushing changes...");
    var uploadedFiles = await synchronizer.PushChangesetAsync(
        workspaceFolder, operationContext, localChangeset, dataverseClient,
        syncInfo.AgentId, cloudFlowMetadata, CancellationToken.None);

    Console.WriteLine("Syncing workspace metadata...");
    await synchronizer.SyncWorkspaceAsync(
        workspaceFolder, operationContext, null, true, dataverseClient,
        syncInfo.AgentId, cloudFlowMetadata, CancellationToken.None);

    Console.WriteLine($"Push complete. {localChanges.Length} change(s) pushed, {uploadedFiles} file(s) uploaded.");

    // Verify push: re-clone from server and diff against pushed state
    Console.WriteLine("Verifying push...");
    var verification = await synchronizer.VerifyPushAsync(
        workspaceFolder, operationContext, dataverseClient, syncInfo.AgentId, CancellationToken.None);

    foreach (var entityType in verification.EntityTypes)
    {
        var status = entityType.Accepted ? "OK" : "REJECTED";
        Console.WriteLine($"  [{status}] {entityType.ChangeKind}: {entityType.VerifiedCount}/{entityType.PushedCount} verified");
    }

    if (!verification.IsFullyAccepted)
    {
        Console.Error.WriteLine("Push verification FAILED — server rejected some changes.");
        Environment.ExitCode = 1;
    }
    else
    {
        Console.WriteLine("Push verification passed.");
    }

}, pushWorkspaceOption);

// ── pull ───────────────────────────────────────────────────────────────
var pullCommand = new Command("pull", "Pull remote changes to workspace");
var pullWorkspaceOption = new Option<string>("--workspace", "Workspace directory (must contain .mcs/conn.json)") { IsRequired = true };
pullCommand.AddOption(pullWorkspaceOption);

pullCommand.SetHandler(async (string workspace) =>
{
    var workspaceFolder = new DirectoryPath(Path.GetFullPath(workspace).Replace('\\', '/'));

    var syncInfoJson = Path.Combine(workspaceFolder.ToString().TrimEnd('/'), ".mcs", "conn.json");
    if (!File.Exists(syncInfoJson))
    {
        Console.Error.WriteLine($"Error: No .mcs/conn.json found in {workspace}. Run 'clone' first.");
        Environment.ExitCode = 1;
        return;
    }

    var connJson = await File.ReadAllTextAsync(syncInfoJson);
    var syncInfoPreview = System.Text.Json.JsonSerializer.Deserialize<AgentSyncInfo>(connJson)
        ?? throw new InvalidOperationException("Failed to parse .mcs/conn.json");

    await using var services = await HostServices.BuildAsync(syncInfoPreview.DataverseEndpoint);

    var synchronizer = services.GetRequiredService<IWorkspaceSynchronizer>();
    var dataverseClient = services.GetRequiredService<ISyncDataverseClient>();
    var operationContextProvider = services.GetRequiredService<IOperationContextProvider>();

    var syncInfo = await synchronizer.GetSyncInfoAsync(workspaceFolder);
    dataverseClient.SetDataverseUrl(syncInfo.DataverseEndpoint.ToString());
    SetIslandContextIfAvailable(services, syncInfo);

    var operationContext = await operationContextProvider.GetAsync(syncInfo);

    Console.WriteLine("Reading workspace definition...");
    var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspaceFolder, CancellationToken.None);

    Console.WriteLine("Pulling remote changes...");
    var updatedDefinition = await synchronizer.PullExistingChangesAsync(
        workspaceFolder, operationContext, localDefinition, dataverseClient,
        syncInfo.AgentId, CancellationToken.None);

    Console.WriteLine("Syncing workspace metadata...");
    await synchronizer.SyncWorkspaceAsync(
        workspaceFolder, operationContext, null, true, dataverseClient,
        syncInfo.AgentId, null, CancellationToken.None);

    Console.WriteLine("Pull complete.");
    PrintWorkspaceFiles(workspaceFolder);

}, pullWorkspaceOption);

// ── root ───────────────────────────────────────────────────────────────
rootCommand.AddCommand(cloneCommand);
rootCommand.AddCommand(pushCommand);
rootCommand.AddCommand(pullCommand);

return await rootCommand.InvokeAsync(args);

// ── helpers ────────────────────────────────────────────────────────────

static void SetIslandContextIfAvailable(IServiceProvider services, AgentSyncInfo syncInfo)
{
    if (syncInfo.AgentManagementEndpoint != null)
    {
        var islandService = services.GetRequiredService<IIslandControlPlaneService>();
        islandService.SetConnectionContext(
            syncInfo.AgentManagementEndpoint.ToString(),
            syncInfo.AccountInfo?.ClusterCategory ?? CoreServicesClusterCategory.Prod);
    }
}

static string GetRequiredEnvVar(string name)
{
    return Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set.");
}

static void PrintWorkspaceFiles(DirectoryPath workspaceFolder)
{
    var root = workspaceFolder.ToString().TrimEnd('/');
    if (!Directory.Exists(root))
    {
        return;
    }

    Console.WriteLine("Workspace contents:");
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        Console.WriteLine($"  {relative}");
    }
}
