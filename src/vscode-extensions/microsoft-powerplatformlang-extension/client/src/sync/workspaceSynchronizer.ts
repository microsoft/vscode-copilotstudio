import * as vscode from 'vscode';
import { resetAccount } from '../clients/account';
import { SyncRequest, SyncResponse, WorkflowResponse, AIPromptResponse } from '../types';
import { CopilotStudioWorkspace, tryRepairAgentManagementEndpoint } from './localWorkspaces';
import { uploadKnowledgeFiles } from '../knowledgeFiles/uploadKnowledgeFiles';
import { virtualKnowledgeFileSystemProvider } from '../knowledgeFiles/virtualKnowledgeFile';
import { knowledgeTreeDataProvider } from '../knowledgeFiles/knowledgeFileTree';
import { LspMethods, TelemetryEventsKeys } from '../constants';
import { lspClient, buildLspRequestPayload } from '../services/lspClient';
import logger, { formatPii, PiiRedactionType, sanitizeErrorDetails } from '../services/logger';

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

export interface PushOptions {
    suppressErrorNotification?: boolean;
    suppressDisabledWorkflowWarnings?: boolean;
    draftConnectionReferenceWorkflows?: boolean;
}

export interface WorkspaceSynchronizer {
    workspace: CopilotStudioWorkspace;
    syncState: SyncState;
    push: (options?: PushOptions) => Promise<SyncResponse | undefined>;
    pull: (virtualProvider: virtualKnowledgeFileSystemProvider) => Promise<SyncResponse | undefined >;
    fetch: () => Promise<void>;
    subscribe: (listener: SyncStateListener) => () => void;
}

interface SyncStateListener {
  (state: SyncState): void | Promise<void>;
}

export function formatReauthenticationError(error: unknown, agentName?: string): string {
  const errorMessage = error instanceof Error ? error.message : String(error);
  return `Re-authentication failed: ${sanitizeErrorDetails(errorMessage, agentName ? [agentName] : [])}`;
}

export function createSyncSuccessLog(agentName: string, operation: string, durationMs: number): {
  message: string;
  data: { agent: string; operation: string; durationMs: number };
} {
  const protectedAgentName = formatPii(agentName, PiiRedactionType.AgentName);
  return {
    message: `Completed ${operation} for ${protectedAgentName} in ${durationMs}ms`,
    data: {
      agent: protectedAgentName,
      operation,
      durationMs,
    },
  };
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

export function removeSynchronizer(workspaceUri: string): void {
  map.delete(workspaceUri);
}

function getSynchronizer(ws: CopilotStudioWorkspace): WorkspaceSynchronizer {
  let currentState = SyncState.Idle;
  const listeners: SyncStateListener[] = [];

  async function updateSyncState(newState: SyncState) {
    currentState = newState;
    const results = await Promise.allSettled(listeners.map(listener => listener(newState)));
    for (const result of results) {
      if (result.status === 'rejected') {
        logger.logError(TelemetryEventsKeys.SyncWorkspaceError, undefined, {
          message: `syncStateListenerError: ${sanitizeErrorDetails(result.reason instanceof Error ? result.reason.message : String(result.reason), [ws.displayName])}`
        });
      }
    }
    _onAnySyncStateChanged.fire();
  }

  async function executeSyncOperation<T>(operation: () => Promise<T>, newState: SyncState): Promise<T> {
    // Prevent concurrent operations
    if (currentState !== SyncState.Idle) {
      throw new Error('Another sync operation is in progress');
    }

    try {
      await updateSyncState(newState);
      const result = await operation();
      return result;
    } finally {
      await updateSyncState(SyncState.Idle);
    }
  }

  return {
    workspace: ws,
    get syncState() { return currentState; },
    push: async (options: PushOptions = {}): Promise<SyncResponse> => {
      const { suppressErrorNotification = false, suppressDisabledWorkflowWarnings = false, draftConnectionReferenceWorkflows = false } = options;
      return await executeSyncOperation(async () => {
        const response = await sync(ws, 'applying changes', LspMethods.SYNC_PUSH, false, suppressErrorNotification, suppressDisabledWorkflowWarnings, draftConnectionReferenceWorkflows);
        await uploadKnowledgeFiles(ws);
        return response;
      }, SyncState.Pushing);
    },
    pull: async (virtualProvider: virtualKnowledgeFileSystemProvider): Promise<SyncResponse> => {
      return await executeSyncOperation(async () => {
        const response = await sync(ws, "getting changes", LspMethods.SYNC_PULL, false);

        if (virtualProvider) {
          await virtualProvider.refresh();
          if (!treeDataProvider) {
            treeDataProvider = new knowledgeTreeDataProvider(virtualProvider);
            vscode.window.registerTreeDataProvider('virtual-knowledge-files', treeDataProvider);
          }
          treeDataProvider.refresh();
        }
        return response;
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

export async function sync(workspace: CopilotStudioWorkspace, displayText: string, methodName: string, silent: boolean, suppressErrorNotification = false, suppressDisabledWorkflowWarnings = false, draftConnectionReferenceWorkflows = false, retryOnUserNotMember = true): Promise<SyncResponse> {
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
    ...await buildLspRequestPayload(syncInfo, undefined, undefined, true),
    workspaceUri,
    draftConnectionReferenceWorkflows,
  };

  try {
    const startTime = Date.now();
    const result = silent
      ? await lspClient.sendRequest<SyncResponse>(methodName, request)
      : await vscode.window.withProgress({ location: vscode.ProgressLocation.SourceControl }, async () => {
        return await lspClient.sendRequest<SyncResponse>(methodName, request);
      });
    const durationMs = Date.now() - startTime;
    const workflowErrorsFound = logWorkflowIssues(result.workflowResponse, suppressDisabledWorkflowWarnings);
    if (!workflowErrorsFound) {
      const successLog = createSyncSuccessLog(workspace.displayName, displayText, durationMs);
      logger.logInfo(TelemetryEventsKeys.SyncWorkspaceSuccess, successLog.message, successLog.data);
    }
    logAIPromptIssues(result.aiPromptResponse);
    return result;
  } catch (error) {
    const errorMessage = error instanceof Error ? error.message : String(error);
    if (retryOnUserNotMember && errorMessage.includes("UserNotMemberOfOrg")) {
      const accountIdentifier = `(${accountInfo.accountEmail ?? accountInfo.accountId})`;
      logger.logError(
        TelemetryEventsKeys.SyncWorkspaceError,
        `Your current account does not have permission. Please sign in with the account ${formatPii(accountIdentifier, PiiRedactionType.AccountIdentifier)} to perform this operation.`,
      );
      try {
        resetAccount();
        return await sync(workspace, displayText, methodName, silent, suppressErrorNotification, suppressDisabledWorkflowWarnings, draftConnectionReferenceWorkflows, false);
      } catch (error) {
        logger.logError(TelemetryEventsKeys.SyncWorkspaceError, formatReauthenticationError(error, workspace.displayName));
        throw error;
      }
    } else if (suppressErrorNotification) {
      logger.logError(TelemetryEventsKeys.SyncWorkspaceError, undefined, {
        message: `Error ${displayText}: ${sanitizeErrorDetails(errorMessage, [workspace.displayName])}`,
      });
      throw error;
    } else {
      logger.logError(
        TelemetryEventsKeys.SyncWorkspaceError,
        `Error ${displayText}: ${sanitizeErrorDetails(errorMessage, [workspace.displayName])}`,
      );
      throw error;
    }
  }
}

export function logWorkflowIssues(workflows: WorkflowResponse[] | undefined, suppressDisabledWarnings = false): boolean {
  if (!workflows?.length) {
    return false;
  }

  const disabledWorkflows: string[] = [];
  const failedWorkflows: string[] = [];

  for (const w of workflows) {
    if (w.errorMessage) {
      failedWorkflows.push(`${w.workflowName}: ${w.errorMessage}`);
    }
    else if (w.isDisabled) {
      disabledWorkflows.push(w.workflowName);
    }
  }

  if (!suppressDisabledWarnings && disabledWorkflows.length > 0) {
    logger.logWarning(
      TelemetryEventsKeys.SyncWorkspaceError,
      `These workflows are disabled. Bind their connections, then enable them from the connection manager: ${formatPii(disabledWorkflows.join(", "), PiiRedactionType.WorkflowNames)}`,
    );
  }

  if (failedWorkflows.length > 0) {
    logger.logError(
      TelemetryEventsKeys.SyncWorkspaceError,
      `Workflow errors: ${formatPii(failedWorkflows.join(", "), PiiRedactionType.WorkflowErrorDetails)}`,
    );
    return true;
  }

  return false;
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
      `Failed to push AI Builder prompt(s): ${formatPii(failed.join('; '), PiiRedactionType.AiBuilderPromptDetails)}`
    );
  }
}