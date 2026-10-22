import { lspClient, buildLspRequestPayload } from '../services/lspClient';
import { LspMethods } from '../constants';
import { Change, DiffRequest, SyncRequest, SyncResponse } from '../types';
import { CopilotStudioWorkspace } from './localWorkspaces';

export const fetchChanges = async <TInput>(requestType: string, input: TInput): Promise<Change[]> => {
  const result = await lspClient.sendRequest<SyncResponse>(requestType, input);
  return result.localChanges;
};

export const readLocalChanges = async (workspace: CopilotStudioWorkspace): Promise<Change[]> =>
  fetchChanges<DiffRequest>(LspMethods.GET_LOCAL_CHANGES, { workspaceUri: workspace.workspaceUri });

export const readRemoteChanges = async (workspace: CopilotStudioWorkspace): Promise<Change[]> => {
  if (!workspace.syncInfo) {
    return [];
  }

  const request: SyncRequest = {
    ...(await buildLspRequestPayload(workspace.syncInfo)),
    workspaceUri: workspace.workspaceUri,
  };

  return fetchChanges<SyncRequest>(LspMethods.GET_REMOTE_CHANGES, request);
};
