import { ExtensionContext, Uri, workspace } from "vscode";
import { getWorkspaceByUri } from "./localWorkspaces";
import { lspClient } from "../services/lspClient";
import { LspMethods, TelemetryEventsKeys } from "../constants";
import { GetFileRequest, GetFileResponse } from "../types";
import logger from "../services/logger";

export const LOCAL_STATE_SCHEME = 'mcs';

// Deprecated: This is old logic that only refresh one time.
export function initializeLocalCacheDocumentContentProvider(context: ExtensionContext) {
  context.subscriptions.push(
    workspace.registerTextDocumentContentProvider(LOCAL_STATE_SCHEME, {
      provideTextDocumentContent: async (uri: Uri) => {
        return retrieveLastSyncedFile(uri);
      }
    }));
}

async function retrieveLastSyncedFile(uri: Uri): Promise<string | null> {
  if (uri.authority === "empty") {
    return "";
  }

  const workspace = getWorkspaceByUri(uri);
  if (!workspace) {
    logger.logError(TelemetryEventsKeys.GetLocalFileError, undefined, { message: `Error retrieving file: could not locate workspace for file <pii>${uri}</pii>` });
    return null;
  }

  const request: GetFileRequest = {
    workspaceUri: workspace.workspaceUri,
    schemaName: uri.path.substring(1) // Remove leading slash,
  };

  try {
    const result = await lspClient.sendRequest<GetFileResponse>(LspMethods.GET_CACHED_FILE, request);
    return result.content;
  } catch (error) {
    logger.logError(TelemetryEventsKeys.GetLocalFileError, `Error retrieving file: ${(error as Error).message}`);
    return null;
  }
}