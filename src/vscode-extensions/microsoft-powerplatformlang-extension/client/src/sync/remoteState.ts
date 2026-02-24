
import { ExtensionContext, Uri, workspace } from "vscode";
import { RemoteFileRequest, GetFileResponse } from '../types';
import { findWorkspaceForUri } from './localWorkspaces';
import { lspClient, buildLspRequestPayload } from "../services/lspClient";
import { LspMethods, TelemetryEventsKeys } from "../constants";
import logger from "../services/logger";

export const REMOTE_STATE_SCHEME = 'mcs-remote';

export function initializeRemoteCacheDocumentContentProvider(context: ExtensionContext) {
  context.subscriptions.push(
    workspace.registerTextDocumentContentProvider(REMOTE_STATE_SCHEME, {
      provideTextDocumentContent: async (uri: Uri) => {
        return getRemoteFileContent(uri);
      }
    }));
}

// NOTE: "fetching" in error messages below is intentionally NOT changed to align with
// the UI terminology (Preview/Get/Apply). These errors describe the inability to
// retrieve file content, where "fetch" is the accurate technical term.

async function getRemoteFileContent(uri: Uri): Promise<string | null> {
  const workspace = findWorkspaceForUri(uri.query);
  if (!workspace) {
    logger.logError(TelemetryEventsKeys.GetRemoteFileError, undefined, { message: `Error fetching file: could not locate workspace for file <pii>${uri}</pii>` });
    return null;
  }

  const { syncInfo, workspaceUri } = workspace;
  if (!syncInfo) {
    logger.logError(TelemetryEventsKeys.GetRemoteFileError, `Error fetching file: connection file .mcs::conn.json is missing, please clone again.`);
    return null;
  }

  const { agentManagementEndpoint, dataverseEndpoint, environmentId } = syncInfo;
  if (!dataverseEndpoint || !environmentId || !agentManagementEndpoint) {
    logger.logError(TelemetryEventsKeys.GetRemoteFileError, `Error fetching file: connection settings in .mcs::conn.json are incomplete or invalid, please clone again.`);
    return null;
  }

  const request: RemoteFileRequest = {
    ...(await buildLspRequestPayload(syncInfo)),
    schemaName: uri.path.substring(1), // Remove leading slash
    workspaceUri
  };

  try {
    const result = await lspClient.sendRequest<GetFileResponse>(LspMethods.GET_REMOTE_FILE, request);
    return result.content;
  } catch (error) {
    logger.logError(TelemetryEventsKeys.GetRemoteFileError, `Error fetching file: ${(error as Error).message}`);
    return null;
  }
}