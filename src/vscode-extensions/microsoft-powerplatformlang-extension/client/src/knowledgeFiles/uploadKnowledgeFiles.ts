import * as vscode from 'vscode';
import { CopilotStudioWorkspace, tryRepairAgentManagementEndpoint } from '../sync/localWorkspaces';
import { lspClient, buildLspRequestPayload } from '../services/lspClient';
import { LspMethods, TelemetryEventsKeys } from '../constants';
import logger from '../services/logger';
import { UploadKnowledgeFilesRequest, UploadKnowledgeFilesResponse } from '../types';

const PROGRESS_NOTIFICATION_DELAY_MS = 600;

export async function uploadKnowledgeFiles(ws: CopilotStudioWorkspace): Promise<void> {
  const { syncInfo, workspaceUri } = ws;
  if (!syncInfo || !syncInfo.dataverseEndpoint || !syncInfo.agentId) {
    return;
  }

  if (!syncInfo.agentManagementEndpoint) {
    await tryRepairAgentManagementEndpoint(syncInfo, workspaceUri);
  }

  const request: UploadKnowledgeFilesRequest = {
    ...(await buildLspRequestPayload(syncInfo)),
    workspaceUri
  };

  const cancellationSource = new vscode.CancellationTokenSource();
  const uploadPromise = lspClient.sendRequest<UploadKnowledgeFilesResponse>(LspMethods.UPLOAD_KNOWLEDGE_FILES, request, cancellationSource.token);
  const completion = uploadPromise.then(() => undefined, () => undefined);

  try {
    let timer: NodeJS.Timeout | undefined;
    const delay = new Promise<boolean>(resolve => {
      timer = setTimeout(() => resolve(true), PROGRESS_NOTIFICATION_DELAY_MS);
    });
    const showProgress = await Promise.race([completion.then(() => false), delay]);
    if (timer) {
      clearTimeout(timer);
    }

    const result = showProgress
      ? await vscode.window.withProgress({
          location: vscode.ProgressLocation.Notification,
          title: 'Uploading knowledge files…',
          cancellable: true
        }, async (_progress, cancellationToken) => {
          const subscription = cancellationToken.onCancellationRequested(() => cancellationSource.cancel());
          try {
            return await uploadPromise;
          } finally {
            subscription.dispose();
          }
        })
      : await uploadPromise;

    if (result.uploaded.length) {
      logger.info('KnowledgeFiles', `Uploaded ${result.uploaded.length} knowledge file(s)`);
      logger.logInfo(TelemetryEventsKeys.UploadKnowledgeFileSuccess, `Uploaded (${result.uploaded.length}): <pii>${result.uploaded.join(', ')}</pii>`);
    }
  } finally {
    cancellationSource.dispose();
  }
}

