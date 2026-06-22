import * as vscode from 'vscode';
import { getAccessTokenByAccountId } from '../clients/account';
import { getTokenScopeHostName } from '../clients/bapClient';
import { DefaultCoreServicesClusterCategory, LspMethods } from '../constants';
import { lspClient, buildLspRequestPayload } from '../services/lspClient';
import { CopilotStudioWorkspace } from '../sync/localWorkspaces';
import {
  AgentConnectionView,
  AgentSyncInfo,
  ApplyConnectionBindingsRequest,
  ApplyConnectionBindingsResponse,
  ConnectionBindingRequest,
  ConnectionReferenceUsage,
  ConnectorInfo,
  CreateConnectionReferenceRequest,
  CreateConnectionReferenceResponse,
  DeclareConnectionReferencesRequest,
  DeclareConnectionReferencesResponse,
  ListAgentConnectionsRequest,
  ListAgentConnectionsResponse,
  ListConnectorsRequest,
  ListConnectorsResponse,
  ListWorkflowStatusRequest,
  ListWorkflowStatusResponse,
  RemoveConnectionReferenceRequest,
  RemoveConnectionReferenceResponse,
  SetWorkflowStatesRequest,
  SetWorkflowStatesResponse,
  WorkflowStateChange,
  WorkflowStatusView
} from '../types';

export const acquireConnectionsAccessToken = async (clusterCategory: number, accountId: string | undefined, accountHint: string | undefined): Promise<string | undefined> => {
  try {
    const resource = vscode.Uri.from({
      scheme: 'https',
      authority: getTokenScopeHostName(clusterCategory)
    });
    const tokenInfo = await getAccessTokenByAccountId(resource, accountId, accountHint);
    return tokenInfo.accessToken;
  } catch {
    return undefined;
  }
};

const acquireWorkspaceConnectionsToken = (syncInfo: AgentSyncInfo): Promise<string | undefined> =>
  acquireConnectionsAccessToken(
    syncInfo.accountInfo.clusterCategory ?? DefaultCoreServicesClusterCategory,
    syncInfo.accountInfo.accountId,
    syncInfo.accountInfo.accountEmail
  );

const baseConnectionRequest = async (syncInfo: AgentSyncInfo, workspaceUri: string) => ({
  ...await buildLspRequestPayload(syncInfo),
  workspaceUri,
  connectionsAccessToken: await acquireWorkspaceConnectionsToken(syncInfo)
});

export const listAgentConnections = async (workspace: CopilotStudioWorkspace): Promise<AgentConnectionView[]> => {
  const { syncInfo, workspaceUri } = workspace;
  if (!syncInfo) {
    return [];
  }

  const request: ListAgentConnectionsRequest = {
    ...await baseConnectionRequest(syncInfo, workspaceUri)
  };

  const response = await lspClient.sendRequest<ListAgentConnectionsResponse>(
    LspMethods.LIST_AGENT_CONNECTIONS,
    request
  );
  if (response.code !== 200) {
    throw new Error(response.message ?? 'Failed to list agent connections.');
  }
  return response.agentConnections ?? [];
};

export const applyConnectionBindings = async (workspace: CopilotStudioWorkspace, bindings: ConnectionBindingRequest[]): Promise<AgentConnectionView[]> => {
  const { syncInfo, workspaceUri } = workspace;
  if (!syncInfo) {
    return [];
  }

  const request: ApplyConnectionBindingsRequest = {
    ...await baseConnectionRequest(syncInfo, workspaceUri),
    bindings
  };

  const response = await lspClient.sendRequest<ApplyConnectionBindingsResponse>(
    LspMethods.APPLY_CONNECTION_BINDINGS,
    request
  );
  if (response.code !== 200) {
    throw new Error(response.message ?? 'Failed to apply connection bindings.');
  }
  return response.agentConnections ?? [];
};

export const listWorkflowStatus = async (workspace: CopilotStudioWorkspace): Promise<WorkflowStatusView[]> => {
  const { syncInfo, workspaceUri } = workspace;
  if (!syncInfo) {
    return [];
  }

  const request: ListWorkflowStatusRequest = {
    ...await baseConnectionRequest(syncInfo, workspaceUri)
  };

  const response = await lspClient.sendRequest<ListWorkflowStatusResponse>(
    LspMethods.LIST_WORKFLOW_STATUS,
    request
  );
  if (response.code !== 200) {
    throw new Error(response.message ?? 'Failed to list workflow status.');
  }
  return response.workflows ?? [];
};

export const setWorkflowStates = async (workspace: CopilotStudioWorkspace, changes: WorkflowStateChange[]): Promise<{ succeeded: boolean; workflows: WorkflowStatusView[]; message?: string }> => {
  const { syncInfo, workspaceUri } = workspace;
  if (!syncInfo || changes.length === 0) {
    return { succeeded: true, workflows: [] };
  }

  const request: SetWorkflowStatesRequest = {
    ...await baseConnectionRequest(syncInfo, workspaceUri),
    changes
  };

  const response = await lspClient.sendRequest<SetWorkflowStatesResponse>(
    LspMethods.SET_WORKFLOW_STATES,
    request
  );
  if (response.code !== 200) {
    return { succeeded: false, workflows: response.workflows ?? [], message: response.message };
  }
  return { succeeded: response.succeeded, workflows: response.workflows ?? [], message: response.message };
};

export const declareConnectionReferences = async (workspace: CopilotStudioWorkspace, logicalNames: string[]): Promise<{ views: AgentConnectionView[]; invalid: string[] }> => {
  const { syncInfo, workspaceUri } = workspace;
  if (!syncInfo) {
    return { views: [], invalid: [] };
  }

  const request: DeclareConnectionReferencesRequest = {
    ...await baseConnectionRequest(syncInfo, workspaceUri),
    logicalNames
  };

  const response = await lspClient.sendRequest<DeclareConnectionReferencesResponse>(
    LspMethods.DECLARE_CONNECTION_REFERENCES,
    request
  );
  if (response.code !== 200) {
    throw new Error(response.message ?? 'Failed to declare connection references.');
  }
  return { views: response.agentConnections ?? [], invalid: response.invalidLogicalNames ?? [] };
};

export const removeConnectionReference = async (workspace: CopilotStudioWorkspace, logicalName: string, confirmed: boolean): Promise<{ removed: boolean; usages: ConnectionReferenceUsage[]; message?: string }> => {
  const { syncInfo, workspaceUri } = workspace;
  if (!syncInfo) {
    return { removed: false, usages: [] };
  }

  const basePayload = await buildLspRequestPayload(syncInfo);

  const request: RemoveConnectionReferenceRequest = {
    ...basePayload,
    workspaceUri,
    logicalName,
    confirmed
  };

  const response = await lspClient.sendRequest<RemoveConnectionReferenceResponse>(
    LspMethods.REMOVE_CONNECTION_REFERENCE,
    request
  );
  if (response.code !== 200) {
    return { removed: false, usages: response.usages ?? [], message: response.message };
  }
  return { removed: response.removed, usages: response.usages ?? [], message: response.message };
};

export const listConnectors = async (workspace: CopilotStudioWorkspace): Promise<ConnectorInfo[]> => {
  const { syncInfo, workspaceUri } = workspace;
  if (!syncInfo) {
    return [];
  }

  const request: ListConnectorsRequest = {
    ...await baseConnectionRequest(syncInfo, workspaceUri)
  };

  const response = await lspClient.sendRequest<ListConnectorsResponse>(
    LspMethods.LIST_CONNECTORS,
    request
  );
  if (response.code !== 200) {
    throw new Error(response.message ?? 'Failed to list connectors.');
  }
  return response.connectors ?? [];
};

export const createConnectionReference = async (workspace: CopilotStudioWorkspace, connectorInternalId: string): Promise<{ logicalName: string; views: AgentConnectionView[] }> => {
  const { syncInfo, workspaceUri } = workspace;
  if (!syncInfo) {
    return { logicalName: '', views: [] };
  }

  const request: CreateConnectionReferenceRequest = {
    ...await baseConnectionRequest(syncInfo, workspaceUri),
    connectorInternalId
  };

  const response = await lspClient.sendRequest<CreateConnectionReferenceResponse>(
    LspMethods.CREATE_CONNECTION_REFERENCE,
    request
  );
  if (response.code !== 200) {
    throw new Error(response.message ?? 'Failed to create the connection reference.');
  }
  return { logicalName: response.logicalName, views: response.agentConnections ?? [] };
};
