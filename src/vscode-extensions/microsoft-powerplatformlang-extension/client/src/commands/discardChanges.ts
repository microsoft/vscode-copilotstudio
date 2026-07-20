import { commands, ExtensionContext, ProgressLocation, Uri, window, workspace as VSworkspace } from 'vscode';
import { CopilotStudioWorkspace, getAllWorkspaces, hasConnectionFileInWorkspace } from '../sync/localWorkspaces';
import { selectWorkspace } from '../sync/workspacePicker';
import { getWorkspaceChanges, refreshLocalChanges } from '../sync/workspaceScm';
import { getActiveSyncUri, withSyncCommandBusy } from '../sync/workspaceSynchronizer';
import { DiscardChangeInput, discardLocalChanges, DiscardResult } from '../sync/discardChanges';
import { lspClient } from '../services/lspClient';
import { LspMethods, TelemetryEventsKeys } from '../constants';
import { GetFileRequest, GetFileResponse } from '../types';
import logger from '../services/logger';

/** Accepted invocation argument shapes (title bar passes nothing; tree/tests may pass a workspace). */
type WorkspaceArg = { ws: CopilotStudioWorkspace } | CopilotStudioWorkspace | null | undefined;

const DISCARD_OPERATION = 'Discard changes';
const CONFIRM_LABEL = 'Discard Changes';

function resolveWorkspaceArg(arg?: WorkspaceArg): CopilotStudioWorkspace | undefined {
  if (arg && typeof arg === 'object') {
    if ('ws' in arg && arg.ws) {
      return arg.ws;
    }
    if ('workspaceUri' in arg) {
      return arg as CopilotStudioWorkspace;
    }
  }
  return undefined;
}

/**
 * Registers the "Discard changes" command shown in the Agent Changes view title bar.
 * It reverts all of the selected agent's local changes back to their last-synced
 * (cached) baseline, entirely offline:
 *   - locally added files are deleted,
 *   - locally updated/deleted files are rewritten from the cached baseline.
 * Binary changes that the text-only cache cannot restore (icon / knowledge files)
 * are reported so the user can restore them with Get.
 */
export const registerDiscardChangesCommand = (context: ExtensionContext) => {
  const command = commands.registerCommand('microsoft-copilot-studio.discardChanges', async (arg?: WorkspaceArg) => {
    try {
      logger.logInfo(TelemetryEventsKeys.SyncWorkspaceClick, undefined, { message: `${DISCARD_OPERATION} operation initiated`, operation: DISCARD_OPERATION });

      // Never discard while a sync is running (the button is gated by !mcs.isSyncing,
      // but the command palette can still reach us).
      if (getActiveSyncUri() !== undefined) {
        logger.logWarning(
          TelemetryEventsKeys.SyncWorkspaceCancel,
          'Cannot discard while a sync operation is in progress.',
          { operation: DISCARD_OPERATION },
        );
        return;
      }

      const selectedWorkspace = resolveWorkspaceArg(arg) ?? await selectWorkspace();
      if (!selectedWorkspace) {
        const message = getAllWorkspaces().length > 0
          ? `No workspace selected. ${DISCARD_OPERATION} operation cancelled.`
          : `No workspace found for ${DISCARD_OPERATION} operation`;
        logger.logWarning(TelemetryEventsKeys.SyncWorkspaceCancel, undefined, { message });
        return;
      }

      if (!selectedWorkspace.syncInfo) {
        const detail = hasConnectionFileInWorkspace(selectedWorkspace.workspaceUri)
          ? 'connection settings in .mcs::conn.json are incomplete or invalid, please clone again.'
          : 'connection file .mcs::conn.json is missing, please clone again.';
        logger.logError(TelemetryEventsKeys.SyncWorkspaceError, `Cannot perform ${DISCARD_OPERATION.toLowerCase()} operation: ${detail}`);
        return;
      }

      const localChanges = getWorkspaceChanges(selectedWorkspace.workspaceUri)?.localChanges ?? [];
      if (localChanges.length === 0) {
        void window.showInformationMessage(`No local changes to discard for "${selectedWorkspace.displayName}".`);
        return;
      }

      const count = localChanges.length;
      const confirm = await window.showWarningMessage(
        `Discard ${count} local change${count === 1 ? '' : 's'} for "${selectedWorkspace.displayName}"?`,
        {
          modal: true,
          detail: 'This restores the affected files to their last-synced state and cannot be undone.',
        },
        CONFIRM_LABEL,
      );
      if (confirm !== CONFIRM_LABEL) {
        logger.logWarning(TelemetryEventsKeys.SyncWorkspaceCancel, undefined, { message: `${DISCARD_OPERATION} cancelled by user.` });
        return;
      }

      const inputs: DiscardChangeInput[] = localChanges.map(resource => ({
        schemaName: resource.schemaName,
        changeType: resource.type,
        changeKind: resource.changeKind,
        fileUri: resource.fullResourceUri,
      }));

      let result: DiscardResult | undefined;
      // Hold the busy state for the whole operation so sync buttons stay disabled
      // and a progress bar shows at the top of the Agent Changes view.
      await withSyncCommandBusy(selectedWorkspace.workspaceUri, async () => {
        await window.withProgress({
          location: ProgressLocation.Notification,
          title: `${DISCARD_OPERATION} operation in progress. Please wait...`,
          cancellable: false,
        }, async () => {
          result = await discardLocalChanges(inputs, {
            getCachedContent: async (schemaName: string) => {
              const request: GetFileRequest = { workspaceUri: selectedWorkspace.workspaceUri, schemaName };
              const response = await lspClient.sendRequest<GetFileResponse>(LspMethods.GET_CACHED_FILE, request);
              return response.content;
            },
            writeFile: async (uri: Uri, content: string) => {
              await VSworkspace.fs.writeFile(uri, Buffer.from(content, 'utf8'));
            },
            deleteFile: async (uri: Uri) => {
              try {
                await VSworkspace.fs.delete(uri, { recursive: false, useTrash: false });
              } catch (error) {
                // An already-missing file is a no-op success.
                const code = (error as { code?: string }).code;
                if (!code || !code.includes('NotFound')) {
                  throw error;
                }
              }
            },
          });
        });

        // Recompute local changes so the tree/badge reflect the discard immediately.
        await refreshLocalChanges(selectedWorkspace.workspaceUri);
      });

      reportResult(selectedWorkspace.displayName, result);
    } catch (error) {
      logger.logError(TelemetryEventsKeys.SyncWorkspaceError, `Failed to execute ${DISCARD_OPERATION.toLowerCase()} operation: ${(error as Error).message}`);
    }
  });

  context.subscriptions.push(command);
};

function reportResult(agentName: string, result: DiscardResult | undefined): void {
  if (!result) {
    return;
  }
  const reverted = result.restored + result.deleted;
  const revertedText = `Discarded ${reverted} local change${reverted === 1 ? '' : 's'} for "${agentName}".`;

  if (result.skipped.length === 0) {
    logger.logInfo(TelemetryEventsKeys.SyncWorkspaceSuccess, revertedText, { operation: DISCARD_OPERATION });
    return;
  }

  const skippedNames = result.skipped.map(s => s.path).join(', ');
  const skippedCount = result.skipped.length;
  logger.logWarning(
    TelemetryEventsKeys.SyncWorkspaceError,
    `${revertedText} ${skippedCount} item${skippedCount === 1 ? '' : 's'} couldn't be reverted offline and can be restored with Get: <pii>${skippedNames}</pii>.`,
    { operation: DISCARD_OPERATION },
  );
}
