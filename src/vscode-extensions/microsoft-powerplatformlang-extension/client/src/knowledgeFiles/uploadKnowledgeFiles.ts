import * as vscode from 'vscode';
import { CopilotStudioWorkspace, tryRepairAgentManagementEndpoint } from '../sync/localWorkspaces';
import { lspClient, buildLspRequestPayload } from '../services/lspClient';
import { LspMethods, TelemetryEventsKeys } from '../constants';
import logger from '../services/logger';
import { UploadKnowledgeFilesRequest, UploadKnowledgeFilesResponse } from '../types';

export async function uploadKnowledgeFiles(ws: CopilotStudioWorkspace): Promise<void> {
  const { syncInfo, workspaceUri } = ws;
  if (!syncInfo || !syncInfo.dataverseEndpoint || !syncInfo.agentId) {
    return;
  }

  if (!syncInfo.agentManagementEndpoint) {
    await tryRepairAgentManagementEndpoint(syncInfo, workspaceUri);
  }

  await vscode.window.withProgress({
    location: vscode.ProgressLocation.Notification,
    title: 'Uploading knowledge files…',
    cancellable: true
  }, async (_progress, cancellationToken) => {
    const request: UploadKnowledgeFilesRequest = {
      ...(await buildLspRequestPayload(syncInfo)),
      workspaceUri
    };
    const result = await lspClient.sendRequest<UploadKnowledgeFilesResponse>(LspMethods.UPLOAD_KNOWLEDGE_FILES, request, cancellationToken);
    if (result.uploaded.length) {
      logger.info('KnowledgeFiles', `Uploaded ${result.uploaded.length} knowledge file(s)`);
      logger.logInfo(TelemetryEventsKeys.UploadKnowledgeFileSuccess, `Uploaded (${result.uploaded.length}): <pii>${result.uploaded.join(', ')}</pii>`);
    }
  });
}

