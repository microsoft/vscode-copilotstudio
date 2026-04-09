import * as vscode from 'vscode';
import { resetAccount } from '../clients/account';
import { SyncRequest, SyncResponse, WorkflowResponse } from '../types';
import { CopilotStudioWorkspace, tryRepairAgentManagementEndpoint } from './localWorkspaces';
import { uploadKnowledgeFiles } from '../knowledgeFiles/uploadKnowledgeFiles';
import { virtualKnowledgeFileSystemProvider } from '../knowledgeFiles/virtualKnowledgeFile';
import { knowledgeTreeDataProvider } from '../knowledgeFiles/knowledgeFileTree';
import { LspMethods, TelemetryEventsKeys } from '../constants';
import { lspClient, buildLspRequestPayload } from '../services/lspClient';
import logger from '../services/logger';

let treeDataProvider: knowledgeTreeDataProvider | undefined;

const map = new Map<string, WorkspaceSynchronizer>();

export enum SyncState {
  Idle,
  Fetching,
  Pulling,
  Pushing,
}

export interface WorkspaceSynchronizer {
    workspace: CopilotStudioWorkspace;
    syncState: SyncState;
    push: () => Promise<SyncResponse | undefined>;
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
    // Notify all listeners about state change
    listeners.forEach(listener => listener(newState));
  }

  async function executeSyncOperation<T>(operation: () => Promise<T>, newState: SyncState): Promise<T> {
    // prevComponentent concurrent operations
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
    push: async (): Promise<SyncResponse> => {
      return await executeSyncOperation(async () => {
        await uploadKnowledgeFiles(ws);
        return await sync(ws, 'applying changes', LspMethods.SYNC_PUSH, false);
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

export async function sync(workspace: CopilotStudioWorkspace, displayText: string, methodName: string, silent: boolean): Promise<SyncResponse> {
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
  };

  try {
    const result = silent
      ? await lspClient.sendRequest<SyncResponse>(methodName, request)
      : await vscode.window.withProgress({ location: vscode.ProgressLocation.SourceControl }, async () => {
        return await lspClient.sendRequest<SyncResponse>(methodName, request);
      });
    logger.logInfo(TelemetryEventsKeys.SyncWorkspaceSuccess, `Successfully completed ${displayText}`);
    logWorkflowIssues(result.workflowResponse);
    return result;
  } catch (error) {
    if ((error as Error).message?.includes("UserNotMemberOfOrg")) {
      logger.logError(TelemetryEventsKeys.SyncWorkspaceError, `Your current account does not have permission. Please sign in with the account <pii>(${accountInfo.accountEmail ?? accountInfo.accountId})</pii> to perform this operation.`);
      try {
        resetAccount();
        return await sync(workspace, displayText, methodName, silent); // Retry sync with new log in
      } catch (error) {
        logger.logError(TelemetryEventsKeys.SyncWorkspaceError, `Re-authentication failed: ${(error as Error).message}`);
        throw error;
      }
    } else {
      logger.logError(TelemetryEventsKeys.SyncWorkspaceError, `Error ${displayText}: ${(error as Error).message}`);
      throw error;
    }
  }
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