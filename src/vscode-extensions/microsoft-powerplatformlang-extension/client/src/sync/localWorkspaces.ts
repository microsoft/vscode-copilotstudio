import * as path from 'path';
import * as fs from 'fs';
import { ExtensionContext, IconPath, MarkdownString, ThemeIcon, Uri, window, workspace } from "vscode";
import { Disposable } from "vscode-languageclient";
import { lspClient } from '../services/lspClient';
import { AccountInfo, AgentSyncInfo } from "../types";
import { getIcon } from "../icon";
import { getClusterCategory, isChildUri, blankToUndefined } from '../utils/genericUtils';
import { DEFAULT_ENVIRONMENT_LOOKUPS, EnvironmentLookup } from '../clients/bapClient';
import { AuthError, extractTenantId, getAuthAccountState, getStoredAccountSummaries, hasUsableTenantId, onAuthStateChanged, resolveAccountIdentity, selectAccountCandidates, StoredAccountSummary } from '../clients/account';
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

const MAX_CONCURRENT_ACCOUNT_REPAIRS = 3;
const TRANSIENT_ACCOUNT_REPAIR_RETRY_MS = 30_000;
const accountRepairQueue = new Map<string, string>();
const runningAccountRepairs = new Map<string, boolean>();
const accountRepairRetryTimers = new Map<string, ReturnType<typeof setTimeout>>();
const activeWorkspaceRepairKeys = new Set<string>();
let accountRepairVersion = 0;
let disposed = false;

interface ConnectionFile {
  contents: string;
  syncInfo: Partial<AgentSyncInfo>;
}

export interface AccountRepairInput {
  connectionContents: string;
  dataverseEndpoint: string;
  tenantId?: string;
  accountId?: string;
  accountEmail?: string;
}

export type AccountRepairOutcome =
  | 'repaired'
  | 'already-bound'
  | 'ambiguous'
  | 'inaccessible'
  | 'transient-failure'
  | 'stale'
  | 'failed';

export function getAccountRepairFailureMessage(displayText: string, outcome: AccountRepairOutcome): string {
  switch (outcome) {
    case 'ambiguous':
      return `${displayText} failed. Select the appropriate account, or add it if it is not listed.`;
    case 'inaccessible':
      return `${displayText} failed. This account cannot access the environment. Select an account with access, or add it if it is not listed.`;
    case 'transient-failure':
      return `${displayText} failed. Could not verify account access. Try again.`;
    case 'stale':
      return `${displayText} failed because the connection changed. Try again.`;
    default:
      return `${displayText} failed. Check .mcs::conn.json and try again.`;
  }
}

type ConnectionFileUpdateResult = 'updated' | 'stale' | 'failed';

const getWorkspaceRepairKey = (workspaceUri: string): string => {
  const workspacePath = path.resolve(Uri.parse(workspaceUri).fsPath);
  return process.platform === 'win32' ? workspacePath.toLowerCase() : workspacePath;
};

const readConnectionFile = (workspaceUri: string): ConnectionFile | undefined => {
  const connFilePath = path.join(Uri.parse(workspaceUri).fsPath, '.mcs', 'conn.json');
  try {
    const contents = fs.readFileSync(connFilePath, 'utf-8');
    const {
      AccountInfo,
      AgentId,
      AgentManagementEndpoint,
      ComponentCollectionId,
      DataverseEndpoint,
      EnvironmentDisplayName,
      EnvironmentId,
      SolutionVersions,
    } = JSON.parse(contents);
    const accountInfo: AccountInfo | undefined = AccountInfo
      ? {
        accountId: blankToUndefined(AccountInfo.AccountId ?? undefined),
        accountEmail: blankToUndefined(AccountInfo.AccountEmail ?? undefined),
        tenantId: blankToUndefined(AccountInfo.TenantId ?? undefined) ?? '',
        clusterCategory: AccountInfo.clusterCategory ?? undefined,
      }
      : undefined;
    return {
      contents,
      syncInfo: {
        dataverseEndpoint: blankToUndefined(DataverseEndpoint ?? undefined),
        agentManagementEndpoint: blankToUndefined(AgentManagementEndpoint ?? undefined),
        environmentId: blankToUndefined(EnvironmentId ?? undefined),
        environmentDisplayName: blankToUndefined(EnvironmentDisplayName ?? undefined),
        agentId: blankToUndefined(AgentId ?? undefined),
        componentCollectionId: blankToUndefined(ComponentCollectionId ?? undefined),
        accountInfo,
        solutionVersions: SolutionVersions
          ? {
            solutionVersions: SolutionVersions.SolutionVersions ?? {},
            copilotStudioSolutionVersion: blankToUndefined(SolutionVersions.CopilotStudioSolutionVersion ?? undefined) ?? '',
          }
          : undefined,
      },
    };
  } catch {
    return undefined;
  }
};

const readAccountRepairInput = (workspaceUri: string): AccountRepairInput | undefined => {
  const connection = readConnectionFile(workspaceUri);
  if (!connection) {
    return undefined;
  }

  const {
    accountInfo,
    dataverseEndpoint,
  } = connection.syncInfo;
  if (!accountInfo || !dataverseEndpoint) {
    return undefined;
  }

  try {
    const endpoint = Uri.parse(dataverseEndpoint, true);
    if (endpoint.scheme !== 'https' || !endpoint.authority) {
      return undefined;
    }
  } catch {
    return undefined;
  }

  return {
    connectionContents: connection.contents,
    dataverseEndpoint,
    tenantId: accountInfo.tenantId,
    accountId: accountInfo.accountId,
    accountEmail: accountInfo.accountEmail,
  };
};

const updateConnectionFileIfUnchanged = (
  workspaceUri: string,
  expectedContents: string,
  mutate: (connectionData: Record<string, unknown>) => boolean,
  canPersist: () => boolean = () => true
): ConnectionFileUpdateResult => {
  const connFilePath = path.join(Uri.parse(workspaceUri).fsPath, '.mcs', 'conn.json');
  try {
    const currentContents = fs.readFileSync(connFilePath, 'utf-8');
    if (!canPersist() || currentContents !== expectedContents) {
      return 'stale';
    }

    const connectionData = JSON.parse(currentContents) as Record<string, unknown> | null;
    if (!connectionData || typeof connectionData !== 'object' || Array.isArray(connectionData)
      || !mutate(connectionData)) {
      return 'failed';
    }

    fs.writeFileSync(connFilePath, JSON.stringify(connectionData), 'utf-8');
    return 'updated';
  } catch {
    return 'failed';
  }
};

export const persistWorkspaceAccountBinding = (
  syncInfo: AgentSyncInfo,
  workspaceUri: string,
  accountInfo: AccountInfo
): void => {
  const connection = readConnectionFile(workspaceUri);
  if (!connection) {
    throw new Error('Could not save the selected account because .mcs::conn.json is missing or invalid.');
  }

  const persisted = updateConnectionFileIfUnchanged(workspaceUri, connection.contents, connectionData => {
    const persistedAccountInfo = connectionData.AccountInfo;
    if (!persistedAccountInfo || typeof persistedAccountInfo !== 'object' || Array.isArray(persistedAccountInfo)) {
      return false;
    }

    const accountData = persistedAccountInfo as Record<string, unknown>;
    accountData.AccountId = accountInfo.accountId;
    accountData.AccountEmail = accountInfo.accountEmail ?? null;
    accountData.TenantId = accountInfo.tenantId;
    if (accountInfo.clusterCategory !== undefined) {
      accountData.clusterCategory = accountInfo.clusterCategory;
    }
    return true;
  });
  if (persisted !== 'updated') {
    throw new Error('Could not save the selected account because .mcs::conn.json changed during Refresh.');
  }

  syncInfo.accountInfo = { ...accountInfo };
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
  disposed = false;
  accountRepairVersion++;
  context.subscriptions.push(onAuthStateChanged(() => {
    if (disposed) {
      return;
    }
    accountRepairVersion++;
    for (const timer of accountRepairRetryTimers.values()) {
      clearTimeout(timer);
    }
    accountRepairRetryTimers.clear();
    for (const localWorkspace of workspaceCache) {
      enqueueAutomaticAccountRepair(localWorkspace.workspaceUri, true);
    }
  }));
  context.subscriptions.push(Disposable.create(() => {
    disposed = true;
    accountRepairVersion++;
    for (const timer of accountRepairRetryTimers.values()) {
      clearTimeout(timer);
    }
    accountRepairRetryTimers.clear();
    accountRepairQueue.clear();
    activeWorkspaceRepairKeys.clear();
    refreshPending = false;
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
        const workspaceUri = Uri.file(path.dirname(path.dirname(uri.fsPath))).toString();
        enqueueAutomaticAccountRepair(workspaceUri, true);
      }
      refreshAndNotify();
    }
  });
  context.subscriptions.push(allFileWatcher);

  context.subscriptions.push(workspace.onDidOpenTextDocument(e => {
    e.uri.scheme === 'file' && refreshAndNotify();
  }));

  context.subscriptions.push(
    workspace.onDidChangeWorkspaceFolders(event => {
      for (const removed of event.removed) {
        const removedUri = removed.uri.toString();
        for (const localWorkspace of workspaceCache) {
          if (isChildUri(localWorkspace.workspaceUri, removedUri)) {
            cancelAutomaticAccountRepair(localWorkspace.workspaceUri);
          }
        }
      }
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
  if (disposed) {
    return workspaceCache;
  }
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
  if (disposed) {
    return;
  }
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
      const workspaces = await listWorkspaces();
      if (!workspaces || disposed) {
        return;
      }
      workspaceCache = workspaces;
      reconcileAutomaticAccountRepairs(workspaceCache);

      // Notify subscribers (they might call refreshAndNotify again; that just sets refreshPending=true).
      for (const callback of callbacks) {
        if (disposed) {
          return;
        }
        callback(workspaceCache);
      }
    }
  } finally {
    refreshInProgress = false;
    iterations = 0;
  }
}

async function listWorkspaces(): Promise<CopilotStudioWorkspace[] | undefined> {
  try {
    const workspaces: CopilotStudioWorkspace[] = [];
    const response = await lspClient.sendRequest<ListWorkspacesResponse>(LspMethods.LIST_WORKSPACES);
    if (disposed) {
      return undefined;
    }
    for (const workspaceUri of response.workspaceUris) {
      if (!isInCurrentWorkspaceFolders(workspaceUri)) {
        continue;
      }
      const data = await lspClient.sendRequest<WorkspaceDetailsResponse>(LspMethods.GET_WORKSPACE_DETAILS, { workspaceUri });
      if (disposed) {
        return undefined;
      }
      if (!isInCurrentWorkspaceFolders(workspaceUri)) {
        continue;
      }
      data.icon = getWorkspaceIcon(data.type, data.IconFilePath);
      workspaces.push(data);
    }
    return workspaces;
  } catch (error) {
    return [];
  }
}

const isInCurrentWorkspaceFolders = (workspaceUri: string): boolean =>
  workspace.workspaceFolders?.some(folder => isChildUri(workspaceUri, folder.uri.toString())) ?? false;

const cancelAutomaticAccountRepair = (workspaceUri: string): void => {
  const key = getWorkspaceRepairKey(workspaceUri);
  cancelAutomaticAccountRepairByKey(key);
};

const cancelAutomaticAccountRepairByKey = (key: string): void => {
  activeWorkspaceRepairKeys.delete(key);
  const retryTimer = accountRepairRetryTimers.get(key);
  if (retryTimer) {
    clearTimeout(retryTimer);
    accountRepairRetryTimers.delete(key);
  }
  accountRepairQueue.delete(key);
};

const reconcileAutomaticAccountRepairs = (workspaces: CopilotStudioWorkspace[]): void => {
  if (disposed) {
    return;
  }
  const currentKeys = new Set(workspaces.map(candidate => getWorkspaceRepairKey(candidate.workspaceUri)));
  for (const key of [...activeWorkspaceRepairKeys]) {
    if (!currentKeys.has(key)) {
      cancelAutomaticAccountRepairByKey(key);
    }
  }
  for (const localWorkspace of workspaces) {
    activeWorkspaceRepairKeys.add(getWorkspaceRepairKey(localWorkspace.workspaceUri));
    enqueueAutomaticAccountRepair(localWorkspace.workspaceUri);
  }
};

export function refreshSyncInfoFromConnection(
  syncInfo: AgentSyncInfo,
  workspaceUri: string
): AgentSyncInfo | undefined {
  const connection = readConnectionFile(workspaceUri)?.syncInfo;
  if (!connection?.accountInfo || !connection.solutionVersions) {
    return undefined;
  }

  const {
    accountInfo,
    agentId,
    agentManagementEndpoint,
    componentCollectionId,
    dataverseEndpoint,
    environmentDisplayName,
    environmentId,
    solutionVersions,
  } = connection;
  return {
    ...syncInfo,
    dataverseEndpoint: dataverseEndpoint ?? '',
    agentManagementEndpoint: agentManagementEndpoint ?? '',
    environmentId: environmentId ?? '',
    environmentDisplayName,
    agentId,
    componentCollectionId,
    accountInfo,
    solutionVersions,
  };
}

const enqueueAutomaticAccountRepair = (workspaceUri: string, force: boolean = false): void => {
  const key = getWorkspaceRepairKey(workspaceUri);
  if (disposed || !activeWorkspaceRepairKeys.has(key)) {
    return;
  }
  const retryTimer = accountRepairRetryTimers.get(key);
  if (retryTimer) {
    if (!force) {
      return;
    }
    clearTimeout(retryTimer);
    accountRepairRetryTimers.delete(key);
  }

  if (runningAccountRepairs.has(key)) {
    if (force) {
      runningAccountRepairs.set(key, true);
    }
    return;
  }

  if (accountRepairQueue.has(key)) {
    return;
  }

  accountRepairQueue.set(key, workspaceUri);
  drainAccountRepairQueue();
};

const scheduleAutomaticAccountRepairRetry = (workspaceUri: string): void => {
  const key = getWorkspaceRepairKey(workspaceUri);
  if (disposed || !activeWorkspaceRepairKeys.has(key) || accountRepairRetryTimers.has(key)) {
    return;
  }

  accountRepairRetryTimers.set(key, setTimeout(() => {
    accountRepairRetryTimers.delete(key);
    if (!disposed && activeWorkspaceRepairKeys.has(key)) {
      enqueueAutomaticAccountRepair(workspaceUri);
    }
  }, TRANSIENT_ACCOUNT_REPAIR_RETRY_MS));
};

const drainAccountRepairQueue = (): void => {
  while (!disposed && runningAccountRepairs.size < MAX_CONCURRENT_ACCOUNT_REPAIRS && accountRepairQueue.size > 0) {
    const [key, workspaceUri] = accountRepairQueue.entries().next().value!;
    accountRepairQueue.delete(key);
    if (!activeWorkspaceRepairKeys.has(key)) {
      continue;
    }
    runningAccountRepairs.set(key, false);
    const repairVersion = accountRepairVersion;

    void (async () => {
      let outcome: AccountRepairOutcome = 'failed';
      try {
        const syncInfo = workspaceCache.find(candidate => getWorkspaceRepairKey(candidate.workspaceUri) === key)?.syncInfo;
        outcome = await repairAccountInfo(
          syncInfo,
          workspaceUri,
          undefined,
          false,
          false,
          () => !disposed
            && repairVersion === accountRepairVersion
            && activeWorkspaceRepairKeys.has(key));
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        logger.logDebug('Workspace', `Automatic account repair failed: <pii>${message}</pii>`);
        outcome = 'transient-failure';
      }

      const rerunRequested = runningAccountRepairs.get(key);
      runningAccountRepairs.delete(key);
      if (rerunRequested) {
        enqueueAutomaticAccountRepair(workspaceUri, true);
      } else if (outcome === 'repaired') {
        void refreshAndNotify();
      } else if (outcome === 'stale') {
        enqueueAutomaticAccountRepair(workspaceUri, true);
      } else if (outcome === 'transient-failure') {
        scheduleAutomaticAccountRepairRetry(workspaceUri);
      }
      drainAccountRepairQueue();
    })();
  }
};

/**
 * Attempts to resolve a missing agentManagementEndpoint from the BAP single-environment API.
 * PAC-cloned workspaces may have a null endpoint when the user lacks PP admin role for the
 * admin-scoped BAP list. The single-environment lookup may succeed where the admin list fails.
 */
export async function tryRepairAgentManagementEndpoint(syncInfo: AgentSyncInfo, workspaceUri: string, lookups?: EnvironmentLookup[]): Promise<boolean> {
  const connection = readConnectionFile(workspaceUri);
  if (!connection) {
    return false;
  }

  const {
    accountInfo,
    agentManagementEndpoint,
    environmentId,
  } = connection.syncInfo;
  if (!accountInfo) {
    return false;
  }

  if (agentManagementEndpoint) {
    syncInfo.agentManagementEndpoint = agentManagementEndpoint;
    return true;
  }

  if (!environmentId) {
    return false;
  }

  const authVersion = accountRepairVersion;
  const agentManagementUrl = await resolveAgentManagementUrl(environmentId, accountInfo, lookups ?? DEFAULT_ENVIRONMENT_LOOKUPS);
  if (!agentManagementUrl || authVersion !== accountRepairVersion) {
    return false;
  }

  const persisted = updateConnectionFileIfUnchanged(workspaceUri, connection.contents, connectionData => {
    connectionData.AgentManagementEndpoint = agentManagementUrl;
    return true;
  });
  if (persisted !== 'updated') {
    if (persisted === 'failed') {
      logger.logDebug('Workspace', 'Resolved the agent management endpoint but could not save it to .mcs/conn.json. It will be resolved again next time.');
    }
    return false;
  }

  syncInfo.agentManagementEndpoint = agentManagementUrl;
  return true;
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
  validateAccount: (candidate: StoredAccountSummary, input: AccountRepairInput) => Promise<boolean>;
}

type AccountValidationFailure = 'inaccessible' | 'transient';

const classifyAccountValidationFailure = (error: unknown): AccountValidationFailure => {
  if (error instanceof AuthError) {
    return error.classification === 'terminal' ? 'inaccessible' : 'transient';
  }

  const message = error instanceof Error ? error.message : String(error);
  return /\b403\b|not a member|previously failed:.*(?:access denied|not a member)/i.test(message)
    ? 'inaccessible'
    : 'transient';
};

const validateSelectedAccountTenant = (tenantId: string | undefined, candidate: StoredAccountSummary): boolean => {
  if (!hasUsableTenantId(tenantId)) {
    return true;
  }
  return extractTenantId(candidate.accountId) === tenantId?.toLowerCase();
};

const validateAccountForEnvironment = async (
  input: AccountRepairInput,
  candidate: StoredAccountSummary,
  interactive: boolean
): Promise<boolean> => {
  await whoAmIAsync(
    Uri.parse(input.dataverseEndpoint),
    null,
    candidate.accountId,
    candidate.accountEmail,
    interactive);
  return true;
};

const getAccountCandidatesForRepair = (
  input: AccountRepairInput,
  accounts: StoredAccountSummary[],
  tenantMatches: StoredAccountSummary[],
  interactive: boolean,
  includeAllAccounts: boolean
): StoredAccountSummary[] => {
  const availableAccounts = interactive
    ? accounts
    : accounts.filter(account => getAuthAccountState(account.accountId, account.accountEmail) !== 'terminal');
  if (includeAllAccounts) {
    return availableAccounts;
  }

  const availableTenantMatches = interactive
    ? tenantMatches
    : tenantMatches.filter(account => getAuthAccountState(account.accountId, account.accountEmail) !== 'terminal');
  return selectAccountCandidates(availableTenantMatches, input.tenantId, () => availableAccounts);
};

const persistTenantId = (
  syncInfo: AgentSyncInfo | undefined,
  workspaceUri: string,
  input: AccountRepairInput,
  tenantId: string,
  canPersist: () => boolean
): AccountRepairOutcome => {
  const persisted = updateConnectionFileIfUnchanged(workspaceUri, input.connectionContents, connectionData => {
    const accountInfo = connectionData.AccountInfo;
    if (!accountInfo || typeof accountInfo !== 'object' || Array.isArray(accountInfo)) {
      return false;
    }
    (accountInfo as Record<string, unknown>).TenantId = tenantId;
    return true;
  }, canPersist);
  if (persisted === 'stale') {
    return 'stale';
  }
  if (persisted === 'failed') {
    logger.logDebug('Workspace', 'Resolved the tenant but could not save it to .mcs/conn.json. It will be resolved again next time.');
    return 'failed';
  }

  if (syncInfo?.accountInfo) {
    syncInfo.accountInfo.tenantId = tenantId;
  }
  return 'repaired';
};

export async function repairAccountInfo(
  syncInfo: AgentSyncInfo | undefined,
  workspaceUri: string,
  deps?: Partial<AccountRepairDeps>,
  interactive: boolean = true,
  includeAllAccounts: boolean = false,
  canPersist: () => boolean = () => true
): Promise<AccountRepairOutcome> {
  if (!canPersist()) {
    return 'stale';
  }
  const input = readAccountRepairInput(workspaceUri);
  if (!input) {
    return 'failed';
  }

  const authVersion = accountRepairVersion;
  if (input.accountId || input.accountEmail) {
    if (hasUsableTenantId(input.tenantId)) {
      return 'already-bound';
    }

    const derivedTenantId = extractTenantId(input.accountId);
    if (!derivedTenantId) {
      return 'failed';
    }
    if (authVersion !== accountRepairVersion) {
      return 'stale';
    }
    return persistTenantId(syncInfo, workspaceUri, input, derivedTenantId, canPersist);
  }

  const tenantMatches = hasUsableTenantId(input.tenantId)
    ? (deps?.findAccounts?.(input.tenantId)
      ?? getStoredAccountSummaries().filter(account => extractTenantId(account.accountId) === input.tenantId?.toLowerCase()))
    : [];
  const accounts = includeAllAccounts || !hasUsableTenantId(input.tenantId)
    ? (deps?.listAllAccounts ?? getStoredAccountSummaries)()
    : tenantMatches;
  const candidates = getAccountCandidatesForRepair(input, accounts, tenantMatches, interactive, includeAllAccounts);
  if (candidates.length === 0 || (!interactive && candidates.length !== 1)) {
    return 'ambiguous';
  }

  const selectedAccount = candidates.length === 1
    ? candidates[0]
    : await (deps?.promptForAccount ?? promptForAccountSelection)(candidates);
  if (!selectedAccount) {
    return 'ambiguous';
  }
  if (!validateSelectedAccountTenant(input.tenantId, selectedAccount)) {
    return 'inaccessible';
  }
  if (!canPersist()) {
    return 'stale';
  }

  try {
    const canAccessEnvironment = deps?.validateAccount
      ? await deps.validateAccount(selectedAccount, input)
      : deps
        ? true
        : await validateAccountForEnvironment(input, selectedAccount, interactive);
    if (!canAccessEnvironment) {
      return 'inaccessible';
    }
  } catch (error) {
    const failure = classifyAccountValidationFailure(error);
    const message = error instanceof Error ? error.message : String(error);
    logger.logDebug('Workspace', `Dataverse environment validation failed: <pii>${message}</pii>`);
    return failure === 'inaccessible' ? 'inaccessible' : 'transient-failure';
  }

  if (!canPersist() || authVersion !== accountRepairVersion) {
    return 'stale';
  }

  const derivedTenantId = extractTenantId(selectedAccount.accountId);
  const persisted = updateConnectionFileIfUnchanged(workspaceUri, input.connectionContents, connectionData => {
    const accountInfo = connectionData.AccountInfo;
    if (!accountInfo || typeof accountInfo !== 'object' || Array.isArray(accountInfo)) {
      return false;
    }

    const persistedAccountInfo = accountInfo as Record<string, unknown>;
    persistedAccountInfo.AccountId = selectedAccount.accountId;
    persistedAccountInfo.AccountEmail = selectedAccount.accountEmail ?? null;
    if (derivedTenantId) {
      persistedAccountInfo.TenantId = derivedTenantId;
    }
    return true;
  }, canPersist);
  if (persisted === 'stale') {
    return 'stale';
  }
  if (persisted === 'failed') {
    logger.logDebug('Workspace', 'Resolved the bound account but could not save it to .mcs/conn.json. It will be resolved again next time.');
    return 'failed';
  }

  if (syncInfo?.accountInfo) {
    syncInfo.accountInfo.accountId = selectedAccount.accountId;
    syncInfo.accountInfo.accountEmail = selectedAccount.accountEmail;
    if (derivedTenantId) {
      syncInfo.accountInfo.tenantId = derivedTenantId;
    }
  }
  logger.logDebug('Workspace', `Resolved the bound account from the current connection for <pii>${workspaceUri}</pii>`);
  return 'repaired';
}

export async function tryRepairAccountInfo(
  syncInfo: AgentSyncInfo,
  workspaceUri: string,
  deps?: Partial<AccountRepairDeps>,
  interactive: boolean = true,
  includeAllAccounts: boolean = false
): Promise<boolean> {
  const outcome = await repairAccountInfo(syncInfo, workspaceUri, deps, interactive, includeAllAccounts);
  return outcome === 'repaired' || outcome === 'already-bound';
}

export async function chooseAccountForWorkspace(syncInfo: AgentSyncInfo, workspaceUri: string, deps?: Partial<AccountRepairDeps>): Promise<boolean> {
  const outcome = await repairAccountInfo(syncInfo, workspaceUri, deps, true, true);
  if (outcome === 'repaired' || outcome === 'already-bound') {
    return true;
  }
  if (outcome !== 'ambiguous') {
    logger.logError(TelemetryEventsKeys.SyncWorkspaceError, getAccountRepairFailureMessage('Selecting the account', outcome));
  }
  return false;
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
