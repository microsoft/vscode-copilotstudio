import * as vscode from 'vscode';
import { resetAccount } from '../clients/account';
import { SyncRequest, SyncResponse, WorkflowResponse, AIPromptResponse, ConnectionBinding, EnvironmentInfo, PreparePushRequest, PreparePushResponse } from '../types';
import { CopilotStudioWorkspace, tryRepairAgentManagementEndpoint } from './localWorkspaces';
import { uploadKnowledgeFiles } from '../knowledgeFiles/uploadKnowledgeFiles';
import { virtualKnowledgeFileSystemProvider } from '../knowledgeFiles/virtualKnowledgeFile';
import { knowledgeTreeDataProvider } from '../knowledgeFiles/knowledgeFileTree';
import { DefaultCoreServicesClusterCategory, LspMethods, TelemetryEventsKeys } from '../constants';
import { lspClient, buildLspRequestPayload } from '../services/lspClient';
import { createAgentConnections } from '../connections/connectionRepair';
import logger from '../services/logger';

let treeDataProvider: knowledgeTreeDataProvider | undefined;

const map = new Map<string, WorkspaceSynchronizer>();

export enum SyncState {
  Idle,
  Fetching,
  Pulling,
  Pushing,
}

/** Global event fired whenever sync activity changes. Used to refresh UI. */
const _onAnySyncStateChanged = new vscode.EventEmitter<void>();
export const onAnySyncStateChanged = _onAnySyncStateChanged.event;

/** workspaceUri of the workspace currently inside `withSyncCommandBusy`. */
let _activeSyncUri: string | undefined;

/** Returns the workspaceUri of the workspace currently being synced, or undefined. */
export function getActiveSyncUri(): string | undefined {
  return _activeSyncUri;
}

/** Returns the synchronizer's current SyncState for `workspaceUri`, or Idle if none. */
export function getSyncStateFor(workspaceUri: string): SyncState {
  return map.get(workspaceUri)?.syncState ?? SyncState.Idle;
}

/**
 * Wrap a sync command's body so the busy state is held for the full duration
 * across multiple fetch/pull/push steps. Surfaces a native progress bar at
 * the top of the Agent Changes tree view and disables sync buttons via the
 * `mcs.isSyncing` context key.
 *
 * Reentrancy is not supported: if a sync is already running, this throws.
 * UI entry points are gated by `!mcs.isSyncing`, so re-entry can only happen
 * from extension code calling `commands.executeCommand` for a sync command
 * while another sync is in flight -- which is always a bug.
 */
export async function withSyncCommandBusy<T>(workspaceUri: string, body: () => Promise<T>): Promise<T> {
  if (_activeSyncUri !== undefined) {
    throw new Error(`A sync is already in progress for ${_activeSyncUri}; cannot start another for ${workspaceUri}.`);
  }
  _activeSyncUri = workspaceUri;
  _onAnySyncStateChanged.fire();
  try {
    return await vscode.window.withProgress(
      { location: { viewId: 'agent-changes' } },
      body
    );
  } finally {
    _activeSyncUri = undefined;
    _onAnySyncStateChanged.fire();
  }
}

export interface WorkspaceSynchronizer {
    workspace: CopilotStudioWorkspace;
    syncState: SyncState;
    push: (suppressErrorNotification?: boolean, connectionBindings?: ConnectionBinding[]) => Promise<SyncResponse | undefined>;
    pull: (virtualProvider: virtualKnowledgeFileSystemProvider) => Promise<SyncResponse | undefined >;
    fetch: () => Promise<void>;
    subscribe: (listener: SyncStateListener) => () => void;
}

interface SyncStateListener {
  (state: SyncState): void;
}

export function getOrAddSynchronizer(ws: CopilotStudioWorkspace): WorkspaceSynchronizer {
  const uri = ws.workspaceUri.toString();
  if (map.has(uri)) {
    return map.get(uri)!;
  }

  const synchronizer = getSynchronizer(ws);
  map.set(uri, synchronizer);
  return synchronizer;
}

function getSynchronizer(ws: CopilotStudioWorkspace): WorkspaceSynchronizer {
  let currentState = SyncState.Idle;
  const listeners: SyncStateListener[] = [];

  function updateSyncState(newState: SyncState) {
    currentState = newState;
    listeners.forEach(listener => listener(newState));
    _onAnySyncStateChanged.fire();
  }

  async function executeSyncOperation<T>(operation: () => Promise<T>, newState: SyncState): Promise<T> {
    // Prevent concurrent operations
    if (currentState !== SyncState.Idle) {
      throw new Error('Another sync operation is in progress');
    }

    try {
      updateSyncState(newState);
      const result = await operation();
      return result;
    } finally {
      updateSyncState(SyncState.Idle);
    }
  }

  return {
    workspace: ws,
    get syncState() { return currentState; },
    push: async (suppressErrorNotification = false, connectionBindings: ConnectionBinding[] = []): Promise<SyncResponse> => {
      return await executeSyncOperation(async () => {
        await uploadKnowledgeFiles(ws);
        return await sync(ws, 'applying changes', LspMethods.SYNC_PUSH, false, suppressErrorNotification, connectionBindings);
      }, SyncState.Pushing);
    },
    pull: async (virtualProvider: virtualKnowledgeFileSystemProvider): Promise<SyncResponse> => {
      return await executeSyncOperation(async () => {
        // Get virtual knowledge files
        if (virtualProvider) {
          await virtualProvider.refresh();
          if (!treeDataProvider) {
            treeDataProvider = new knowledgeTreeDataProvider(virtualProvider);
            vscode.window.registerTreeDataProvider('virtual-knowledge-files', treeDataProvider);
          }
          treeDataProvider.refresh();
        }
        return await sync(ws, "getting changes", LspMethods.SYNC_PULL, false);
      }, SyncState.Pulling);
    },
    fetch: async () => {
      await executeSyncOperation(
        async () => {
          await sync(ws, "previewing changes", LspMethods.GET_REMOTE_CHANGES, true);
        },
        SyncState.Fetching
      );
    },
    subscribe: (listener: SyncStateListener): () => void => {
      listeners.push(listener);
      return () => {
        const index = listeners.indexOf(listener);
        if (index !== -1) {
          listeners.splice(index, 1);
        }
      };
    }
  };
}

export async function sync(workspace: CopilotStudioWorkspace, displayText: string, methodName: string, silent: boolean, suppressErrorNotification = false, connectionBindings: ConnectionBinding[] = []): Promise<SyncResponse> {
  const { syncInfo, workspaceUri } = workspace;
  if (!syncInfo) {
    throw new Error(`${displayText} failed. Connection file .mcs::conn.json is missing, please clone again.`);
  }

  // On-demand repair: resolve missing agentManagementEndpoint from BAP single-environment lookup.
  // PAC-cloned workspaces may have null endpoint when user lacks PP admin role.
  if (!syncInfo.agentManagementEndpoint) {
    await tryRepairAgentManagementEndpoint(syncInfo, workspaceUri);
  }

  const { accountInfo, agentManagementEndpoint, dataverseEndpoint, environmentId } = syncInfo;
  if (!dataverseEndpoint || !environmentId || !agentManagementEndpoint) {
    throw new Error(`${displayText} failed. Connection settings in .mcs::conn.json are incomplete or invalid, please clone again.`);
  }

  const request: SyncRequest = {
    ...await buildLspRequestPayload(syncInfo),
    workspaceUri,
    connectionBindings,
  };

  try {
    const result = silent
      ? await lspClient.sendRequest<SyncResponse>(methodName, request)
      : await vscode.window.withProgress({ location: vscode.ProgressLocation.SourceControl }, async () => {
        return await lspClient.sendRequest<SyncResponse>(methodName, request);
      });
    logger.logInfo(TelemetryEventsKeys.SyncWorkspaceSuccess, `Successfully completed ${displayText}`);
    logWorkflowIssues(result.workflowResponse);
    logAIPromptIssues(result.aiPromptResponse);
    return result;
  } catch (error) {
    if ((error as Error).message?.includes("UserNotMemberOfOrg")) {
      logger.logError(TelemetryEventsKeys.SyncWorkspaceError, `Your current account does not have permission. Please sign in with the account <pii>(${accountInfo.accountEmail ?? accountInfo.accountId})</pii> to perform this operation.`);
      try {
        resetAccount();
        return await sync(workspace, displayText, methodName, silent, suppressErrorNotification, connectionBindings);
      } catch (error) {
        logger.logError(TelemetryEventsKeys.SyncWorkspaceError, `Re-authentication failed: ${(error as Error).message}`);
        throw error;
      }
    } else if (suppressErrorNotification) {
      logger.logError(TelemetryEventsKeys.SyncWorkspaceError, undefined, { message: `Error ${displayText}: <pii>${(error as Error).message}</pii>` });
      throw error;
    } else {
      logger.logError(TelemetryEventsKeys.SyncWorkspaceError, `Error ${displayText}: ${(error as Error).message}`);
      throw error;
    }
  }
}

export type PreparePushConnectionsResult =
  | { status: 'ready'; bindings: ConnectionBinding[] }
  | { status: 'failed' }
  | { status: 'incomplete'; bindings: ConnectionBinding[]; unfinished: string[] };

export async function preparePushConnections(ws: CopilotStudioWorkspace): Promise<PreparePushConnectionsResult> {
  const { syncInfo, workspaceUri } = ws;
  if (!syncInfo) {
    return { status: 'ready', bindings: [] };
  }

  const request: PreparePushRequest = {
    ...await buildLspRequestPayload(syncInfo),
    workspaceUri,
  };

  const prepareResult = await lspClient.sendRequest<PreparePushResponse>(LspMethods.PREPARE_PUSH, request);
  if (prepareResult.code !== 200) {
    logger.logError(TelemetryEventsKeys.ConnectionCreationError, `Prepare push for connections failed: ${prepareResult.message ?? 'Unknown error'}`);
    return { status: 'failed' };
  }

  if (!prepareResult.agentConnections || prepareResult.agentConnections.length === 0) {
    return { status: 'ready', bindings: [] };
  }

  const environmentInfo: EnvironmentInfo = {
    environmentId: syncInfo.environmentId,
    dataverseUrl: syncInfo.dataverseEndpoint,
    agentManagementUrl: syncInfo.agentManagementEndpoint,
    displayName: ''
  };
  const account = {
    accountId: syncInfo.accountInfo.accountId,
    accountEmail: syncInfo.accountInfo.accountEmail
  };

  let repair;
  try {
    repair = await createAgentConnections(
      prepareResult.agentConnections,
      environmentInfo,
      syncInfo.accountInfo.clusterCategory ?? DefaultCoreServicesClusterCategory,
      account
    );
  } catch (error) {
    logger.logError(TelemetryEventsKeys.ConnectionCreationError, `Error creating agent connections: ${(error as Error).message}`);
    return { status: 'failed' };
  }

  if (repair.unfinished.length > 0) {
    return { status: 'incomplete', bindings: repair.bindings, unfinished: repair.unfinished };
  }

  return { status: 'ready', bindings: repair.bindings };
}

export function logWorkflowIssues(workflows: WorkflowResponse[] | undefined) {
  if (!workflows?.length) {
    return;
  }

  const disabledWorkflows: string[] = [];
  const failedWorkflows: string[] = [];

  for (const w of workflows) {
    if (w.isDisabled) {
      disabledWorkflows.push(w.workflowName);
    }
    else if (w.errorMessage) {
      failedWorkflows.push(`${w.workflowName}: ${w.errorMessage}`);
    }
  }

  if (disabledWorkflows.length > 0) {
    logger.logWarning(TelemetryEventsKeys.SyncWorkspaceError, `These workflows need reestablish connection and need to be enabled in MCS portal: ${disabledWorkflows.join(", ")}`);
  } else if (failedWorkflows.length > 0) {
    logger.logError(TelemetryEventsKeys.SyncWorkspaceError, `Workflow errors: ${failedWorkflows.join(", ")}`);
  }
}

export function logAIPromptIssues(prompts: AIPromptResponse[] | undefined) {
  if (!prompts?.length) {
    return;
  }

  const failed: string[] = [];
  for (const p of prompts) {
    if (p.errorMessage) {
      failed.push(`${p.promptName}: ${p.errorMessage}`);
    }
  }

  if (failed.length > 0) {
    logger.logError(
      TelemetryEventsKeys.SyncWorkspaceError,
      `Failed to push AI Builder prompt(s): ${failed.join('; ')}`
    );
  }
}