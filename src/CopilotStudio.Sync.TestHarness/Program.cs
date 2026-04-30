// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.TestHarness;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using System.Diagnostics;

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
    var sw = Stopwatch.StartNew();
    try
    {
        if (string.IsNullOrEmpty(environmentId))
        {
            Console.Error.WriteLine("Error: --environment-id is required (or set COPILOT_TEST_ENVIRONMENT_ID env var).");
            Environment.ExitCode = 1;
            return;
        }

        var environmentUrl = new Uri(environment.TrimEnd('/') + "/");
        Console.WriteLine($"Environment: {environmentUrl}");

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
        await synchronizer.CloneChangesAsync(workspaceFolder, referenceTracker, operationContext, dataverseClient, syncInfo, CancellationToken.None);

        Console.WriteLine("Syncing workspace metadata...");
        await synchronizer.SyncWorkspaceAsync(workspaceFolder, operationContext, null, true, dataverseClient, syncInfo, null, CancellationToken.None);

        await synchronizer.ApplyTouchupsAsync(workspaceFolder, referenceTracker, CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine($"Clone complete. Workspace: {workspaceFolder}");
        PrintWorkspaceEntitySummary(workspaceFolder);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Environment.ExitCode = 1;
    }
    finally
    {
        Console.WriteLine($"Elapsed: {sw.Elapsed.TotalSeconds:F1}s");
    }

}, environmentOption, environmentIdOption, agentSchemaNameOption, outputOption);

// ── push ───────────────────────────────────────────────────────────────
var pushCommand = new Command("push", "Push local workspace changes to the cloud");
var pushWorkspaceOption = new Option<string>("--workspace", "Workspace directory (must contain .mcs/conn.json)") { IsRequired = true };
pushCommand.AddOption(pushWorkspaceOption);

pushCommand.SetHandler(async (string workspace) =>
{
    var sw = Stopwatch.StartNew();
    try
    {
        var workspaceFolder = ResolveWorkspace(workspace);
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
        Console.WriteLine($"Environment: {syncInfoPreview.DataverseEndpoint}");

        var synchronizer = services.GetRequiredService<IWorkspaceSynchronizer>();
        var dataverseClient = services.GetRequiredService<ISyncDataverseClient>();
        var operationContextProvider = services.GetRequiredService<IOperationContextProvider>();

        var syncInfo = await synchronizer.GetSyncInfoAsync(workspaceFolder);
        dataverseClient.SetDataverseUrl(syncInfo.DataverseEndpoint.ToString());
        SetIslandContextIfAvailable(services, syncInfo);

        Console.WriteLine($"Agent ID: {syncInfo.AgentId}");

        var operationContext = await operationContextProvider.GetAsync(syncInfo);

        Console.WriteLine("Reading workspace definition...");
        var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspaceFolder, CancellationToken.None, checkKnowledgeFiles: true);

        Console.WriteLine("Detecting local changes...");
        var (localChangeset, localChanges) = await synchronizer.GetLocalChangesAsync(workspaceFolder, localDefinition, dataverseClient, syncInfo, CancellationToken.None);

        if (localChanges.IsEmpty)
        {
            Console.WriteLine("No local changes detected.");
            return;
        }

        PrintChangeSummary(localChanges);

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
            syncInfo, cloudFlowMetadata, CancellationToken.None);

        Console.WriteLine($"Push complete. {localChanges.Length} change(s) pushed, {uploadedFiles} file(s) uploaded.");

        Console.WriteLine("Verifying push...");
        var verification = await synchronizer.VerifyPushAsync(
            workspaceFolder, operationContext, dataverseClient, syncInfo, CancellationToken.None);

        PrintVerificationResult(verification);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Environment.ExitCode = 1;
    }
    finally
    {
        Console.WriteLine($"Elapsed: {sw.Elapsed.TotalSeconds:F1}s");
    }

}, pushWorkspaceOption);

// ── pull ───────────────────────────────────────────────────────────────
var pullCommand = new Command("pull", "Pull remote changes to workspace");
var pullWorkspaceOption = new Option<string>("--workspace", "Workspace directory (must contain .mcs/conn.json)") { IsRequired = true };
pullCommand.AddOption(pullWorkspaceOption);

pullCommand.SetHandler(async (string workspace) =>
{
    var sw = Stopwatch.StartNew();
    try
    {
        var workspaceFolder = ResolveWorkspace(workspace);
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
        Console.WriteLine($"Environment: {syncInfoPreview.DataverseEndpoint}");

        var synchronizer = services.GetRequiredService<IWorkspaceSynchronizer>();
        var dataverseClient = services.GetRequiredService<ISyncDataverseClient>();
        var operationContextProvider = services.GetRequiredService<IOperationContextProvider>();

        var syncInfo = await synchronizer.GetSyncInfoAsync(workspaceFolder);
        dataverseClient.SetDataverseUrl(syncInfo.DataverseEndpoint.ToString());
        SetIslandContextIfAvailable(services, syncInfo);

        Console.WriteLine($"Agent ID: {syncInfo.AgentId}");

        var operationContext = await operationContextProvider.GetAsync(syncInfo);

        Console.WriteLine("Reading workspace definition...");
        var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspaceFolder, CancellationToken.None);

        Console.WriteLine("Detecting remote changes...");
        var (_, remoteChanges) = await synchronizer.GetRemoteChangesAsync(
            workspaceFolder, operationContext, dataverseClient, syncInfo, CancellationToken.None);

        if (remoteChanges.IsEmpty)
        {
            Console.WriteLine("No remote changes detected.");
        }
        else
        {
            PrintChangeSummary(remoteChanges);
        }

        Console.WriteLine("Pulling remote changes...");
        var updatedDefinition = await synchronizer.PullExistingChangesAsync(
            workspaceFolder, operationContext, localDefinition, dataverseClient,
            syncInfo, CancellationToken.None);

        Console.WriteLine("Syncing workspace metadata...");
        await synchronizer.SyncWorkspaceAsync(
            workspaceFolder, operationContext, null, true, dataverseClient,
            syncInfo, null, CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine("Pull complete.");
        PrintWorkspaceEntitySummary(workspaceFolder);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Environment.ExitCode = 1;
    }
    finally
    {
        Console.WriteLine($"Elapsed: {sw.Elapsed.TotalSeconds:F1}s");
    }

}, pullWorkspaceOption);

// ── verify ────────────────────────────────────────────────────────────
var verifyCommand = new Command("verify", "Verify workspace matches server state (re-clone and diff)");
var verifyWorkspaceOption = new Option<string>("--workspace", "Workspace directory (must contain .mcs/conn.json)") { IsRequired = true };
verifyCommand.AddOption(verifyWorkspaceOption);

verifyCommand.SetHandler(async (string workspace) =>
{
    var sw = Stopwatch.StartNew();
    try
    {
        var workspaceFolder = ResolveWorkspace(workspace);
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
        Console.WriteLine($"Environment: {syncInfoPreview.DataverseEndpoint}");

        var synchronizer = services.GetRequiredService<IWorkspaceSynchronizer>();
        var dataverseClient = services.GetRequiredService<ISyncDataverseClient>();
        var operationContextProvider = services.GetRequiredService<IOperationContextProvider>();

        var syncInfo = await synchronizer.GetSyncInfoAsync(workspaceFolder);
        dataverseClient.SetDataverseUrl(syncInfo.DataverseEndpoint.ToString());
        SetIslandContextIfAvailable(services, syncInfo);

        Console.WriteLine($"Agent ID: {syncInfo.AgentId}");

        var operationContext = await operationContextProvider.GetAsync(syncInfo);

        Console.WriteLine("Verifying workspace against server state...");
        var verification = await synchronizer.VerifyPushAsync(
            workspaceFolder, operationContext, dataverseClient, syncInfo, CancellationToken.None);

        PrintVerificationResult(verification);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Environment.ExitCode = 1;
    }
    finally
    {
        Console.WriteLine($"Elapsed: {sw.Elapsed.TotalSeconds:F1}s");
    }

}, verifyWorkspaceOption);

// ── list-agents ───────────────────────────────────────────────────────
var listAgentsCommand = new Command("list-agents", "List agents in an environment (for discovering schema names)");
var listEnvOption = new Option<string>("--environment", "Dataverse org URL") { IsRequired = true };
var listFilterOption = new Option<string?>("--filter", "Optional display name substring filter");
listAgentsCommand.AddOption(listEnvOption);
listAgentsCommand.AddOption(listFilterOption);

listAgentsCommand.SetHandler(async (string environment, string? filter) =>
{
    try
    {
        var environmentUrl = new Uri(environment.TrimEnd('/') + "/");
        await using var services = await HostServices.BuildAsync(environmentUrl);

        var authProvider = services.GetRequiredService<ISyncAuthProvider>();
        var token = await authProvider.AcquireTokenAsync(environmentUrl, CancellationToken.None);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var query = $"{environmentUrl}api/data/v9.2/bots?$select=name,schemaname,botid&$orderby=name";
        var response = await http.GetStringAsync(query);
        var doc = System.Text.Json.JsonDocument.Parse(response);

        Console.WriteLine($"{"Schema Name",-40} {"Display Name",-30} {"Bot ID"}");
        Console.WriteLine(new string('-', 110));

        foreach (var bot in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var name = bot.GetProperty("name").GetString() ?? "";
            var schema = bot.GetProperty("schemaname").GetString() ?? "";
            var id = bot.GetProperty("botid").GetString() ?? "";

            if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Console.WriteLine($"{schema,-40} {name,-30} {id}");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Environment.ExitCode = 1;
    }
}, listEnvOption, listFilterOption);

// ── clone-via-bridge ──────────────────────────────────────────────────
// Exercises the extension's actual bridge types (TokenManager, LspSyncAuthProvider,
// LspDataverseHttpClientAccessor) with real tokens against a live tenant.
// Used by E2E tests to compare extension-path clone output against direct CLI clone.
var cloneViaBridgeCommand = new Command("clone-via-bridge", "Clone via extension bridge types (F3 validation)");
var bridgeEnvOption = new Option<string>("--environment", "Dataverse org URL") { IsRequired = true };
var bridgeEnvIdOption = new Option<string?>("--environment-id", () => Environment.GetEnvironmentVariable("COPILOT_TEST_ENVIRONMENT_ID"), "Power Platform environment ID");
var bridgeAgentOption = new Option<string>("--agent-schema-name", "Schema name of the agent") { IsRequired = true };
var bridgeOutputOption = new Option<string>("--output", () => "./workspace-bridge", "Output workspace directory");

cloneViaBridgeCommand.AddOption(bridgeEnvOption);
cloneViaBridgeCommand.AddOption(bridgeEnvIdOption);
cloneViaBridgeCommand.AddOption(bridgeAgentOption);
cloneViaBridgeCommand.AddOption(bridgeOutputOption);

cloneViaBridgeCommand.SetHandler(async (string environment, string? environmentId, string agentSchemaName, string output) =>
{
    var sw = Stopwatch.StartNew();
    try
    {
        if (string.IsNullOrEmpty(environmentId))
        {
            Console.Error.WriteLine("Error: --environment-id is required (or set COPILOT_TEST_ENVIRONMENT_ID env var).");
            Environment.ExitCode = 1;
            return;
        }

        var environmentUrl = new Uri(environment.TrimEnd('/') + "/");
        Console.WriteLine($"Environment: {environmentUrl}");
        Console.WriteLine("[bridge] Using extension bridge types (TokenManager → LspSyncAuthProvider → LspDataverseHttpClientAccessor)");

        // 1. Acquire Dataverse token using the same interactive auth the test harness uses
        var directAuthProvider = await Microsoft.CopilotStudio.Sync.TestHarness.AuthProvider.CreateAsync();
        var dvToken = await directAuthProvider.AcquireTokenAsync(environmentUrl, CancellationToken.None);

        // 2. Build a service provider using the extension's actual bridge types.
        //    Island control plane is disabled (isIslandPreauthorized: false) — PAC
        //    doesn't use it either. All data flows through Dataverse.
        await using var services = BridgeHostServices.BuildWithBridgeTypes(environmentUrl, dvToken);

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
        };

        var operationContextProvider = services.GetRequiredService<IOperationContextProvider>();
        var operationContext = await operationContextProvider.GetAsync(syncInfo);

        var workspaceFolder = new DirectoryPath(Path.GetFullPath(output).Replace('\\', '/'));
        Directory.CreateDirectory(workspaceFolder.ToString().TrimEnd('/'));

        var synchronizer = services.GetRequiredService<IWorkspaceSynchronizer>();

        Console.WriteLine($"Saving sync info to {workspaceFolder}...");
        await synchronizer.SaveSyncInfoAsync(workspaceFolder, syncInfo);

        Console.WriteLine("[bridge] Cloning agent via extension bridge stack...");
        var referenceTracker = new ReferenceTracker();
        await synchronizer.CloneChangesAsync(workspaceFolder, referenceTracker, operationContext, dataverseClient, new AgentSyncInfo { AgentId = agentId }, CancellationToken.None);

        Console.WriteLine("[bridge] Syncing workspace metadata...");
        await synchronizer.SyncWorkspaceAsync(workspaceFolder, operationContext, null, true, dataverseClient, new AgentSyncInfo { AgentId = agentId }, null, CancellationToken.None);

        await synchronizer.ApplyTouchupsAsync(workspaceFolder, referenceTracker, CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine($"[bridge] Clone complete. Workspace: {workspaceFolder}");
        PrintWorkspaceEntitySummary(workspaceFolder);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Environment.ExitCode = 1;
    }
    finally
    {
        Console.WriteLine($"Elapsed: {sw.Elapsed.TotalSeconds:F1}s");
    }

}, bridgeEnvOption, bridgeEnvIdOption, bridgeAgentOption, bridgeOutputOption);

// ── root ───────────────────────────────────────────────────────────────
rootCommand.AddCommand(cloneCommand);
rootCommand.AddCommand(pushCommand);
rootCommand.AddCommand(pullCommand);
rootCommand.AddCommand(verifyCommand);
rootCommand.AddCommand(listAgentsCommand);
rootCommand.AddCommand(cloneViaBridgeCommand);

var result = await rootCommand.InvokeAsync(args);
// InvokeAsync returns 0 on successful dispatch regardless of Environment.ExitCode.
// Since top-level Main returns int, the return value overrides Environment.ExitCode.
// Propagate the handler's exit code if it signaled failure.
return Environment.ExitCode != 0 ? Environment.ExitCode : result;

// ── helpers ────────────────────────────────────────────────────────────

static DirectoryPath ResolveWorkspace(string workspace)
{
    return new DirectoryPath(Path.GetFullPath(workspace).Replace('\\', '/'));
}

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

// Prints a per-entity-type summary of workspace contents by scanning directories.
// Maps workspace directories to SYNC-SEMANTICS.md entity type names.
static void PrintWorkspaceEntitySummary(DirectoryPath workspaceFolder)
{
    var root = workspaceFolder.ToString().TrimEnd('/');
    if (!Directory.Exists(root))
    {
        return;
    }

    // Directory → SYNC-SEMANTICS entity type mapping
    var entityDirectories = new (string Directory, string EntityType)[]
    {
        ("topics", "AdaptiveDialog (topics)"),
        ("actions", "TaskDialog (actions)"),
        ("agents", "AgentDialog (sub-agents)"),
        ("knowledge", "KnowledgeSource"),
        ("knowledge/files", "FileAttachment"),
        ("variables", "GlobalVariable"),
        ("settings", "BotSettings"),
        ("entities", "CustomEntity"),
        ("skills", "BotFrameworkSkill"),
        ("trigger", "ExternalTrigger"),
        ("translations", "Translation"),
        ("environmentvariables", "EnvironmentVariable"),
        ("workflows", "CloudFlow (workflow)"),
    };

    Console.WriteLine();
    Console.WriteLine("Entity type inventory:");

    // Root-level files (agent.mcs.yml, settings.mcs.yml, icon.png, etc.)
    var rootFiles = Directory.GetFiles(root, "*.mcs.yml");
    foreach (var file in rootFiles)
    {
        var name = Path.GetFileName(file);
        Console.WriteLine($"  {name,-40} [root]");
    }

    var iconPath = Path.Combine(root, "icon.png");
    if (File.Exists(iconPath))
    {
        Console.WriteLine($"  {"icon.png",-40} [AgentIcon]");
    }

    // Entity directories
    foreach (var (dir, entityType) in entityDirectories)
    {
        var fullPath = Path.Combine(root, dir.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(fullPath))
        {
            continue;
        }

        int count;
        if (dir == "workflows")
        {
            // Workflows are subdirectories, not flat files
            count = Directory.GetDirectories(fullPath).Length;
        }
        else if (dir == "knowledge/files")
        {
            count = Directory.GetFiles(fullPath, "*.mcs.yml").Length;
        }
        else
        {
            count = Directory.GetFiles(fullPath, "*.mcs.yml").Length;
        }

        Console.WriteLine($"  {dir + "/",-40} {count,3} item(s)  [{entityType}]");
    }

    // Hidden state
    var mcsDir = Path.Combine(root, ".mcs");
    if (Directory.Exists(mcsDir))
    {
        var mcsFiles = Directory.GetFiles(mcsDir).Length;
        Console.WriteLine($"  {".mcs/",-40} {mcsFiles,3} file(s)  [hidden state]");
    }
}

// Prints a change summary grouped by entity type (ChangeKind).
static void PrintChangeSummary(System.Collections.Immutable.ImmutableArray<Change> changes)
{
    Console.WriteLine($"Found {changes.Length} change(s):");

    // Group by ChangeKind for summary
    var groups = changes.GroupBy(c => c.ChangeKind).OrderBy(g => g.Key);
    foreach (var group in groups)
    {
        var creates = group.Count(c => c.ChangeType == ChangeType.Create);
        var updates = group.Count(c => c.ChangeType == ChangeType.Update);
        var deletes = group.Count(c => c.ChangeType == ChangeType.Delete);

        var parts = new List<string>();
        if (creates > 0) parts.Add($"{creates} create");
        if (updates > 0) parts.Add($"{updates} update");
        if (deletes > 0) parts.Add($"{deletes} delete");

        Console.WriteLine($"  {group.Key,-30} {string.Join(", ", parts)}");
    }

    // Per-change detail
    Console.WriteLine();
    foreach (var change in changes)
    {
        Console.WriteLine($"  {change.ChangeType}: {change.Name} ({change.ChangeKind})");
    }
}

// Prints per-entity-type push verification results and sets exit code on failure.
static void PrintVerificationResult(PushVerificationResult verification)
{
    Console.WriteLine();
    Console.WriteLine("Verification results:");
    foreach (var entityType in verification.EntityTypes)
    {
        var status = entityType.Accepted ? "OK" : "REJECTED";
        Console.WriteLine($"  [{status,-8}] {entityType.ChangeKind,-30} {entityType.VerifiedCount}/{entityType.PushedCount} verified");
    }

    Console.WriteLine();
    if (!verification.IsFullyAccepted)
    {
        Console.Error.WriteLine("Push verification FAILED — server rejected some changes.");
        Environment.ExitCode = 1;
    }
    else
    {
        Console.WriteLine("Push verification passed — all entity types accepted.");
    }
}
