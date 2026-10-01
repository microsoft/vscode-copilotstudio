import * as path from 'path';
import * as fs from 'fs';
import { ExtensionContext, IconPath, MarkdownString, ThemeIcon, Uri, window, workspace } from "vscode";
import { Disposable } from "vscode-languageclient";
import { lspClient } from '../services/lspClient';
import { AccountInfo, AgentSyncInfo } from "../types";
import { getIcon } from "../icon";
import { getClusterCategory, isChildUri, blankToUndefined } from '../utils/genericUtils';
import { DEFAULT_ENVIRONMENT_LOOKUPS, EnvironmentLookup } from '../clients/bapClient';
import { AuthError, onAccountChange, findAccountsByTenant, getStoredAccountSummaries, hasUsableTenantId, extractTenantId, resolveAccountIdentity, selectAccountCandidates, StoredAccountSummary, listStoredAccounts, buildAccountTenantIndex } from '../clients/account';
import { whoAmIAsync } from '../clients/dataverseClient';
import { pickAccount } from '../services/accountEnvPicker';
import { LspMethods } from '../constants';
import logger from '../services/logger';
import { TelemetryEventsKeys } from '../constants';

export type  WorkspaceIcon = IconPath | ThemeIcon;

export enum WorkspaceType {
  Unknown = -1,
  Agent = 0,
  ComponentCollection = 1,
}

export interface CopilotStudioWorkspace {
  workspaceUri: string;
  displayName: string;
  description: string;
  icon: WorkspaceIcon;
  type: WorkspaceType
  syncInfo?: AgentSyncInfo
  schemaName?: string
}

export const getWorkspaceKindLabel = (workspace: CopilotStudioWorkspace): string => workspace.type === WorkspaceType.ComponentCollection ? 'component collection' : 'agent';

interface ListWorkspacesResponse {
  workspaceUris: string[];
}

interface WorkspaceDetailsResponse extends CopilotStudioWorkspace {
  IconFilePath?: string;
}

type WorkspaceChangeCallback = (workspaces: CopilotStudioWorkspace[]) => void;

const callbacks: WorkspaceChangeCallback[] = [];
let workspaceCache: CopilotStudioWorkspace[] = [];
let refreshPending = false;
let refreshInProgress = false;
const MAX_REFRESH_ITERATIONS = 3;
let iterations = 0;

// Per-session cache of workspaces where endpoint repair was attempted and failed.
// Prevents repeated BAP calls on every workspace refresh.
// Cleared on auth/session changes so a sign-in can retry.
const repairAttempted = new Set<string>();

const accountRepairAttempted = new Set<string>();
const autoAccountRepairAttempted = new Set<string>();
const autoAccountRepairInProgress = new Set<string>();
const MAX_CONCURRENT_ACCOUNT_REPAIRS = 3;
let activeAccountRepairs = 0;
let accountRepairGeneration = 0;
const accountRepairWaiters: Array<() => void> = [];

const acquireAccountRepairSlot = async (): Promise<void> => {
  if (activeAccountRepairs < MAX_CONCURRENT_ACCOUNT_REPAIRS) {
    activeAccountRepairs++;
    return;
  }

  await new Promise<void>(resolve => accountRepairWaiters.push(resolve));
  activeAccountRepairs++;
};

const releaseAccountRepairSlot = (): void => {
  activeAccountRepairs--;
  accountRepairWaiters.shift()?.();
};

export const runWithAccountRepairLimit = async <T>(operation: () => Promise<T>): Promise<T> => {
  await acquireAccountRepairSlot();
  try {
    return await operation();
  } finally {
    releaseAccountRepairSlot();
  }
};

const clearRepairCaches = (workspaceUri?: string): void => {
  if (!workspaceUri) {
    accountRepairGeneration++;
    repairAttempted.clear();
    accountRepairAttempted.clear();
    autoAccountRepairAttempted.clear();
    return;
  }

  const targetPath = path.resolve(Uri.parse(workspaceUri).fsPath);
  const pathsMatch = (candidateUri: string): boolean => {
    const candidatePath = path.resolve(Uri.parse(candidateUri).fsPath);
    return process.platform === 'win32'
      ? candidatePath.toLowerCase() === targetPath.toLowerCase()
      : candidatePath === targetPath;
  };
  for (const cache of [repairAttempted, accountRepairAttempted, autoAccountRepairAttempted]) {
    for (const candidateUri of cache) {
      if (pathsMatch(candidateUri)) {
        cache.delete(candidateUri);
      }
    }
  }
};

const updateConnectionFile = (workspaceUri: string, mutate: (connectionData: Record<string, unknown>) => void): boolean => {
  const connFilePath = path.join(Uri.parse(workspaceUri).fsPath, '.mcs', 'conn.json');
  if (!fs.existsSync(connFilePath)) {
    return false;
  }

  try {
    const connectionData = JSON.parse(fs.readFileSync(connFilePath, 'utf-8')) as Record<string, unknown>;
    mutate(connectionData);
    fs.writeFileSync(connFilePath, JSON.stringify(connectionData), 'utf-8');
    return true;
  } catch {
    return false;
  }
};

const hasPersistedAccountIdentity = (workspaceUri: string): boolean => {
  const connFilePath = path.join(Uri.parse(workspaceUri).fsPath, '.mcs', 'conn.json');
  if (!fs.existsSync(connFilePath)) {
    return false;
  }

  try {
    const connectionData = JSON.parse(fs.readFileSync(connFilePath, 'utf-8')) as {
      AccountInfo?: { AccountId?: string; AccountEmail?: string };
    };
    return !!(blankToUndefined(connectionData.AccountInfo?.AccountId) || blankToUndefined(connectionData.AccountInfo?.AccountEmail));
  } catch {
    return false;
  }
};

export const getAllWorkspaces = (): CopilotStudioWorkspace[] => workspaceCache;

export const getDuplicateDisplayNames = (workspaces: CopilotStudioWorkspace[] = workspaceCache): Set<string> => {
  const counts = new Map<string, number>();
  for (const ws of workspaces) {
    const key = ws.displayName.toLowerCase();
    counts.set(key, (counts.get(key) ?? 0) + 1);
  }
  const duplicates = new Set<string>();
  for (const [key, count] of counts) {
    if (count > 1) {
      duplicates.add(key);
    }
  }
  return duplicates;
};

export const buildAgentIdentityTooltip = (ws: CopilotStudioWorkspace, connectedOverride?: boolean, statusDetail?: string): MarkdownString => {
  const connected = connectedOverride ?? hasConnectionFileInWorkspace(ws.workspaceUri);
  const environmentId = ws.syncInfo?.environmentId;
  const environmentDisplayName = ws.syncInfo?.environmentDisplayName;
  const tenantId = ws.syncInfo?.accountInfo?.tenantId;
  const tooltip = new MarkdownString();
  tooltip.appendMarkdown(`**${ws.displayName}**\n\n`);
  tooltip.appendMarkdown(`SchemaName: \`${ws.schemaName ?? '—'}\`\n\n`);
  const environmentText = environmentDisplayName ? `${environmentDisplayName} (\`${environmentId ?? '—'}\`)` : `\`${environmentId ?? '—'}\``;
  tooltip.appendMarkdown(`Environment: ${environmentText}\n\n`);
  if (tenantId) {
    tooltip.appendMarkdown(`Tenant: \`${tenantId}\`\n\n`);
  }
  tooltip.appendMarkdown(`Account: ${ws.syncInfo?.accountInfo?.accountEmail ?? '—'}\n\n`);
  tooltip.appendMarkdown(`Status: ${connected ? 'Connected' : 'Not connected'}`);
  if (!connected && statusDetail) {
    tooltip.appendMarkdown(` — ${statusDetail}`);
  }
  return tooltip;
};

// Finds the workspace whose URI most specifically contains the given URI (longest match wins so a
// nested agent folder is preferred over its parent; isChildUri's boundary check rejects siblings).
const findLongestMatchingWorkspace = (uriString: string): CopilotStudioWorkspace | undefined => {
  let best: CopilotStudioWorkspace | undefined;
  for (const workspace of workspaceCache) {
    if (isChildUri(uriString, workspace.workspaceUri) && (best === undefined || workspace.workspaceUri.length > best.workspaceUri.length)) {
      best = workspace;
    }
  }
  return best;
};

// Finds the matching workspace by checking if the uri is a child of any workspace URI.
export const findWorkspaceForUri = (uri: string): CopilotStudioWorkspace | undefined => findLongestMatchingWorkspace(uri);

// Finds a workspace that contains the specified URI.
export const getWorkspaceByUri = (uri: Uri): CopilotStudioWorkspace | undefined => {
  const uriString = uri.scheme === 'mcs' ? decodeURIComponent(uri.query) : uri.toString(true);
  return findLongestMatchingWorkspace(uriString);
};

export const getActiveAgentAccount = (): AccountInfo | undefined => {
  const activeUri = window.activeTextEditor?.document.uri;
  if (activeUri) {
    const ws = getWorkspaceByUri(activeUri);
    if (ws?.syncInfo?.accountInfo) {
      return ws.syncInfo.accountInfo;
    }
  }

  const accounts = workspaceCache
    .map(w => w.syncInfo?.accountInfo)
    .filter((a): a is AccountInfo => !!a);
  if (accounts.length === 0) {
    return undefined;
  }

  const uniqueKey = (a: AccountInfo) => (a.accountEmail || a.accountId || '').toLowerCase();
  const first = accounts[0];
  const allSame = accounts.every(a => uniqueKey(a) === uniqueKey(first));
  return allSame ? first : undefined;
};

export const getAllProjectAccounts = (): AccountInfo[] => {
  const seen = new Set<string>();
  const result: AccountInfo[] = [];
  for (const w of workspaceCache) {
    const a = w.syncInfo?.accountInfo;
    if (!a) {
      continue;
    }
    const key = (a.accountEmail || a.accountId || '').toLowerCase();
    if (!key || seen.has(key)) {
      continue;
    }
    seen.add(key);
    result.push(a);
  }
  return result;
};

// Checks if .mcs/conn.json exists in the workspace
export const hasConnectionFileInWorkspace = (workspaceUri: string): boolean => {
  const workspaceFolder = Uri.parse(workspaceUri).fsPath;
  const connFilePath = path.join(workspaceFolder, '.mcs', 'conn.json');
  return fs.existsSync(connFilePath);
};

export async function initializeLocalWorkspaces(context: ExtensionContext) {
  // Clear endpoint repair negative cache on auth changes so sign-in can retry.
  context.subscriptions.push(await onAccountChange(() => {
    clearRepairCaches();
    refreshAndNotify();
  }));

  const allFileWatcher = workspace.createFileSystemWatcher('**/*.*');
  allFileWatcher.onDidChange(async (uri) => {
    const loweredPath = uri.path.toLowerCase();
    if (loweredPath.endsWith('.mcs/conn.json')
      || loweredPath.endsWith('.mcs/botdefinition.json')
      || loweredPath.endsWith('icon.png')
      || loweredPath.endsWith('settings.mcs.yml')
      || loweredPath.endsWith('collection.mcs.yml')) {
      if (loweredPath.endsWith('.mcs/conn.json')) {
        const agentDirectory = path.dirname(path.dirname(uri.fsPath));
        clearRepairCaches(Uri.file(agentDirectory).toString());
      }
      refreshAndNotify();
    }
  });

  context.subscriptions.push(workspace.onDidOpenTextDocument(e => {
    e.uri.scheme === 'file' && refreshAndNotify();
  }));

  context.subscriptions.push(
    workspace.onDidChangeWorkspaceFolders(() => {
      refreshAndNotify();
    })
  );

  refreshAndNotify();
}

export function addWorkspaceChangeSubscription(callback: WorkspaceChangeCallback): Disposable {
  callbacks.push(callback);
  const disposable = Disposable.create(() => { callbacks.splice(callbacks.indexOf(callback), 1); });
  return disposable;
}

export async function updateWorkspaceCache(ws: CopilotStudioWorkspace): Promise<CopilotStudioWorkspace[]> {
  const existingIndex = workspaceCache.findIndex(w => w.workspaceUri === ws.workspaceUri);
  if (existingIndex !== -1) {
    workspaceCache[existingIndex] = ws;
  } else {
    workspaceCache.push(ws);
  }

  await refreshAndNotify();
  return workspaceCache;
}

async function refreshAndNotify() {
  refreshPending = true;

  if (refreshInProgress) {
    // Refresh already in progress, will refresh again when done
    return;
  }

  refreshInProgress = true;

  try {
    while (refreshPending) {
      if (iterations++ >= MAX_REFRESH_ITERATIONS) {
        logger.logDebug('Workspace', `Refresh loop capped at ${MAX_REFRESH_ITERATIONS} iterations`);
        break;
      }
      refreshPending = false;
      workspaceCache = await listWorkspaces();

      // Notify subscribers (they might call refreshAndNotify again; that just sets refreshPending=true).
      for (const callback of callbacks) {
        callback(workspaceCache);
      }
    }
  } finally {
    refreshInProgress = false;
    iterations = 0;
  }
}

async function listWorkspaces(): Promise<CopilotStudioWorkspace[]> {
  try {
    const workspaces: CopilotStudioWorkspace[] = [];
    const pendingAccountRepairs: { syncInfo: AgentSyncInfo; workspaceUri: string }[] = [];
    const response = await lspClient.sendRequest<ListWorkspacesResponse>(LspMethods.LIST_WORKSPACES);
    for (const workspaceUri of response.workspaceUris) {
      const data = await lspClient.sendRequest<WorkspaceDetailsResponse>(LspMethods.GET_WORKSPACE_DETAILS, { workspaceUri });
      if (data.syncInfo) {
        const accountInfo = data.syncInfo.accountInfo;
        if (accountInfo && (blankToUndefined(accountInfo.accountId) || blankToUndefined(accountInfo.accountEmail))) {
          await tryRepairAccountInfo(data.syncInfo, workspaceUri, undefined, false);
        } else if (accountInfo && !autoAccountRepairAttempted.has(workspaceUri)) {
          pendingAccountRepairs.push({ syncInfo: data.syncInfo, workspaceUri });
        }
      }
      data.icon = getWorkspaceIcon(data.type, data.IconFilePath);
      workspaces.push(data);
    }

    setTimeout(() => scheduleAutomaticAccountRepairs(pendingAccountRepairs), 0);

    return workspaces;
  } catch (error) {
    return [];
  }
}

const scheduleAutomaticAccountRepairs = (repairs: { syncInfo: AgentSyncInfo; workspaceUri: string }[]): void => {
  const pendingRepairs = repairs.filter(repair => {
    if (autoAccountRepairAttempted.has(repair.workspaceUri) || autoAccountRepairInProgress.has(repair.workspaceUri)) {
      return false;
    }
    autoAccountRepairInProgress.add(repair.workspaceUri);
    return true;
  });
  if (pendingRepairs.length === 0) {
    return;
  }

  const generation = accountRepairGeneration;
  void (async () => {
    try {
      const storedAccounts = await listStoredAccounts();
      const accountsByTenant = buildAccountTenantIndex(storedAccounts);
      const repairDeps: Partial<AccountRepairDeps> = {
        findAccounts: tenantId => {
          const normalizedTenantId = blankToUndefined(tenantId)?.toLowerCase();
          return normalizedTenantId ? accountsByTenant.get(normalizedTenantId) ?? [] : [];
        },
        listAllAccounts: () => storedAccounts,
        canPersist: () => generation === accountRepairGeneration,
      };

      await Promise.all(pendingRepairs.map(pendingRepair => runWithAccountRepairLimit(
        () => tryRepairAccountInfo(pendingRepair.syncInfo, pendingRepair.workspaceUri, repairDeps, false))));
    } catch {
      logger.logWarning(TelemetryEventsKeys.SyncWorkspaceError, 'Could not inspect signed-in accounts while resolving workspace account information.');
    } finally {
      for (const pendingRepair of pendingRepairs) {
        autoAccountRepairInProgress.delete(pendingRepair.workspaceUri);
      }
      if (generation !== accountRepairGeneration) {
        refreshAndNotify();
      }
    }
  })();
};

/**
 * Attempts to resolve a missing agentManagementEndpoint from the BAP single-environment API.
 * PAC-cloned workspaces may have a null endpoint when the user lacks PP admin role for the
 * admin-scoped BAP list. The single-environment lookup (GET /environments/{id}) may succeed
 * where the admin list fails.
 *
 * If resolved, updates syncInfo in place and rewrites conn.json to disk.
 * Returns true if the endpoint was repaired.
 */
export async function tryRepairAgentManagementEndpoint(syncInfo: AgentSyncInfo, workspaceUri: string, lookups?: EnvironmentLookup[]): Promise<boolean> {
  if (syncInfo.agentManagementEndpoint) {
    return true;
  }

  if (!syncInfo.environmentId || !syncInfo.accountInfo || repairAttempted.has(workspaceUri)) {
    return false;
  }

  const agentManagementUrl = await resolveAgentManagementUrl(syncInfo.environmentId, syncInfo.accountInfo, lookups ?? DEFAULT_ENVIRONMENT_LOOKUPS);
  if (agentManagementUrl) {
    syncInfo.agentManagementEndpoint = agentManagementUrl;
    if (!updateConnectionFile(workspaceUri, connectionData => { connectionData.AgentManagementEndpoint = agentManagementUrl; })) {
      logger.logWarning(TelemetryEventsKeys.SyncWorkspaceError, 'Resolved the agent management endpoint but could not save it to .mcs/conn.json. It will be resolved again next time.');
    }
    return true;
  }

  repairAttempted.add(workspaceUri);
  return false;
}

const resolveAgentManagementUrl = async (environmentId: string, accountInfo: AccountInfo, lookups: EnvironmentLookup[]): Promise<string | undefined> => {
  const clusterCategory = getClusterCategory(accountInfo);
  const resolvedIdentity = resolveAccountIdentity(accountInfo);
  for (const lookupEnvironment of lookups) {
    try {
      const envInfo = await lookupEnvironment(clusterCategory, environmentId, null, resolvedIdentity.accountId ?? null, resolvedIdentity.accountEmail);
      if (envInfo?.agentManagementUrl) {
        return envInfo.agentManagementUrl;
      }
    } catch {
      continue;
    }
  }

  return undefined;
};

export interface AccountRepairDeps {
  findAccounts: (tenantId?: string) => StoredAccountSummary[];
  listAllAccounts: () => StoredAccountSummary[];
  promptForAccount: (candidates: StoredAccountSummary[]) => Promise<StoredAccountSummary | undefined>;
  validateAccount: (candidate: StoredAccountSummary) => Promise<boolean>;
  canPersist: () => boolean;
}

type AccountValidationFailure = 'conclusive' | 'transient';

const validateSelectedAccountTenant = (tenantId: string | undefined, candidate: StoredAccountSummary): boolean => {
  const expectedTenantId = blankToUndefined(tenantId);
  if (!expectedTenantId || !hasUsableTenantId(expectedTenantId)) {
    return true;
  }

  const candidateTenantId = extractTenantId(candidate.accountId);
  return candidateTenantId?.toLowerCase() === expectedTenantId.toLowerCase();
};

const validateAccountForEnvironment = async (syncInfo: AgentSyncInfo, candidate: StoredAccountSummary, interactive: boolean = false): Promise<boolean> => {
  const { dataverseEndpoint } = syncInfo;
  if (!syncInfo.agentId && syncInfo.componentCollectionId) {
    return true;
  }
  if (!dataverseEndpoint) {
    return false;
  }

  await whoAmIAsync(
    Uri.parse(dataverseEndpoint),
    null,
    candidate.accountId,
    candidate.accountEmail,
    interactive);
  return true;
};

const classifyAccountValidationFailure = (error: unknown): AccountValidationFailure => {
  if (error instanceof AuthError) {
    return error.classification === 'terminal' ? 'conclusive' : 'transient';
  }

  const message = error instanceof Error ? error.message : String(error);
  return /\b403\b|not a member|previously failed:.*(?:access denied|not a member)/i.test(message)
    ? 'conclusive'
    : 'transient';
};

const tryRepairTenantId = (accountInfo: AgentSyncInfo['accountInfo'], workspaceUri: string): boolean => {
  if (hasUsableTenantId(accountInfo.tenantId)) {
    return false;
  }

  const derivedTenantId = extractTenantId(accountInfo.accountId);
  if (!derivedTenantId) {
    return false;
  }

  accountInfo.tenantId = derivedTenantId;
  const persisted = updateConnectionFile(workspaceUri, connectionData => {
    const persistedAccountInfo = connectionData.AccountInfo as Record<string, unknown> | undefined;
    if (persistedAccountInfo) {
      persistedAccountInfo.TenantId = derivedTenantId;
    }
  });

  if (!persisted) {
    logger.logWarning(TelemetryEventsKeys.SyncWorkspaceError, 'Resolved the tenant but could not save it to .mcs/conn.json. It will be resolved again next time.');
  }

  return true;
};

export async function tryRepairAccountInfo(
  syncInfo: AgentSyncInfo,
  workspaceUri: string,
  deps?: Partial<AccountRepairDeps>,
  interactive: boolean = true,
  includeAllAccounts: boolean = false): Promise<boolean> {
  const accountInfo = syncInfo.accountInfo;
  if (!accountInfo) {
    return false;
  }

  if (blankToUndefined(accountInfo.accountId) || blankToUndefined(accountInfo.accountEmail)) {
    tryRepairTenantId(accountInfo, workspaceUri);
    return true;
  }

  const attemptedRepairs = interactive ? accountRepairAttempted : autoAccountRepairAttempted;
  if (attemptedRepairs.has(workspaceUri)) {
    return false;
  }

  const candidates = includeAllAccounts
    ? (deps?.listAllAccounts ?? getStoredAccountSummaries)()
    : selectAccountCandidates(
      (deps?.findAccounts ?? findAccountsByTenant)(accountInfo.tenantId),
      accountInfo.tenantId,
      deps?.listAllAccounts ?? getStoredAccountSummaries);

  if (candidates.length === 0 || (!interactive && candidates.length !== 1)) {
    attemptedRepairs.add(workspaceUri);
    return false;
  }

  const selectedAccount = candidates.length === 1
    ? candidates[0]
    : await (deps?.promptForAccount ?? promptForAccountSelection)(candidates);
  if (!selectedAccount) {
    return false;
  }

  let canAccessEnvironment = false;
  let validationFailure: AccountValidationFailure | undefined;
  if (!validateSelectedAccountTenant(accountInfo.tenantId, selectedAccount)) {
    validationFailure = 'conclusive';
  } else {
    try {
      canAccessEnvironment = await (deps?.validateAccount ?? (candidate => validateAccountForEnvironment(syncInfo, candidate, interactive)))(selectedAccount);
      if (!canAccessEnvironment) {
        validationFailure = 'conclusive';
      }
    } catch (error) {
      validationFailure = classifyAccountValidationFailure(error);
      const message = error instanceof Error ? error.message : String(error);
      logger.logDebug('Workspace', `Dataverse environment validation failed: <pii>${message}</pii>`);
    }
  }
  if (!canAccessEnvironment) {
    if (validationFailure === 'conclusive') {
      attemptedRepairs.add(workspaceUri);
    }
    if (interactive) {
      const accountLabel = selectedAccount.accountEmail ?? selectedAccount.accountId;
      logger.logWarning(
        TelemetryEventsKeys.SyncWorkspaceError,
        `The selected account '<pii>${accountLabel}</pii>' could not access the Dataverse environment for <pii>${workspaceUri}</pii>. Choose a matching account and try again.`);
    } else {
      logger.logDebug('Workspace', `Skipped automatic account repair because the candidate could not access the Dataverse environment for <pii>${workspaceUri}</pii>`);
    }
    return false;
  }

  if (deps?.canPersist && !deps.canPersist()) {
    return false;
  }
  if (!interactive && hasPersistedAccountIdentity(workspaceUri)) {
    return false;
  }

  const derivedTenantId = extractTenantId(selectedAccount.accountId);
  const shouldUpdateTenantId = !!derivedTenantId && accountInfo.tenantId?.toLowerCase() !== derivedTenantId.toLowerCase();
  accountInfo.accountId = selectedAccount.accountId;
  accountInfo.accountEmail = selectedAccount.accountEmail;
  if (shouldUpdateTenantId) {
    accountInfo.tenantId = derivedTenantId;
  }

  const persisted = updateConnectionFile(workspaceUri, connectionData => {
    const persistedAccountInfo = connectionData.AccountInfo as Record<string, unknown> | undefined;
    if (persistedAccountInfo) {
      persistedAccountInfo.AccountId = selectedAccount.accountId;
      persistedAccountInfo.AccountEmail = selectedAccount.accountEmail ?? null;
      if (shouldUpdateTenantId) {
        persistedAccountInfo.TenantId = derivedTenantId;
      }
    }
  });

  if (!persisted) {
    logger.logWarning(TelemetryEventsKeys.SyncWorkspaceError, 'Resolved the bound account but could not save it to .mcs/conn.json. It will be resolved again next time.');
  }

  logger.logDebug('Workspace', `Resolved the bound account from the tenant id for <pii>${workspaceUri}</pii>`);
  return true;
}

export async function chooseAccountForWorkspace(syncInfo: AgentSyncInfo, workspaceUri: string, deps?: Partial<AccountRepairDeps>): Promise<boolean> {
  accountRepairAttempted.delete(workspaceUri);
  return tryRepairAccountInfo(syncInfo, workspaceUri, deps, true, true);
}

const promptForAccountSelection = async (candidates: StoredAccountSummary[]): Promise<StoredAccountSummary | undefined> => {
  const picked = await pickAccount('Choose the account used for this agent', { listAccounts: async () => candidates });
  return picked && picked !== 'cancelled' && picked.accountId ? { accountId: picked.accountId, accountEmail: blankToUndefined(picked.accountEmail) } : undefined;
};

function getWorkspaceIcon(workspaceType: WorkspaceType, iconFilePath?: string): WorkspaceIcon {
  if (iconFilePath) {
    const iconFileUri = Uri.file(iconFilePath);
    return { light: iconFileUri, dark: iconFileUri };
  }
  switch (workspaceType) {
    case WorkspaceType.Agent:
      return getIcon();
    case WorkspaceType.ComponentCollection:
      return new ThemeIcon("package");
    default:
      return new ThemeIcon("folder");
  }
}
