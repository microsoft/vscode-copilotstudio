import { commands, ExtensionContext, ProgressLocation, window } from 'vscode';
import { CopilotStudioWorkspace, getAllWorkspaces, hasConnectionFileInWorkspace } from '../sync/localWorkspaces';
import { selectWorkspace } from '../sync/workspacePicker';
import { getWorkspaceChanges, replaceLocalChanges } from '../sync/workspaceScm';
import { getActiveSyncUri, withSyncCommandBusy } from '../sync/workspaceSynchronizer';
import { lspClient } from '../services/lspClient';
import { LspMethods, TelemetryEventsKeys } from '../constants';
import { Change, DiffRequest, DiscardLocalChangesResponse, DiscardResult, SyncResponse } from '../types';
import logger, { formatPii, PiiRedactionType } from '../services/logger';

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
 * (cached) baseline entirely offline. Projection-aware restoration is performed
 * by the language server so logical changes that span or share files remain safe.
 */
export const registerDiscardChangesCommand = (context: ExtensionContext) => {
  const command = commands.registerCommand('microsoft-copilot-studio.discardChanges', async (arg?: WorkspaceArg) => {
    try {
      logger.logInfo(TelemetryEventsKeys.SyncWorkspaceClick, undefined, { message: `${DISCARD_OPERATION} operation initiated`, syncOperation: DISCARD_OPERATION });

      // Never discard while a sync is running (the button is gated by !mcs.isSyncing,
      // but the command palette can still reach us).
      if (getActiveSyncUri() !== undefined) {
        logger.logWarning(
          TelemetryEventsKeys.SyncWorkspaceCancel,
          'Cannot discard while a sync operation is in progress.',
          { syncOperation: DISCARD_OPERATION },
        );
        return;
      }

      const selectedWorkspace = resolveWorkspaceArg(arg) ?? await selectWorkspace();
      if (!selectedWorkspace) {
        if (getAllWorkspaces().length > 0) {
          logger.logWarning(TelemetryEventsKeys.SyncWorkspaceCancel, `No workspace selected. ${DISCARD_OPERATION} operation cancelled.`);
        } else {
          logger.logError(TelemetryEventsKeys.SyncWorkspaceError, `No workspace found for ${DISCARD_OPERATION} operation`);
        }
        return;
      }

      if (!selectedWorkspace.syncInfo) {
        const detail = hasConnectionFileInWorkspace(selectedWorkspace.workspaceUri)
          ? 'connection settings in .mcs::conn.json are incomplete or invalid, please clone again.'
          : 'connection file .mcs::conn.json is missing, please clone again.';
        logger.logError(TelemetryEventsKeys.SyncWorkspaceError, `Cannot perform ${DISCARD_OPERATION.toLowerCase()} operation: ${detail}`);
        return;
      }

      // Recompute local changes from disk (credential-free) rather than trusting the
      // Agent Changes tree cache, which can under-report while it catches up with disk
      // (e.g. right after an initial local-diff failure). This keeps the pre-flight
      // gate and confirmation count consistent with the disk-based discard below.
      const count = await getLocalChangeCount(selectedWorkspace.workspaceUri);
      if (count === 0) {
        void window.showInformationMessage(`No local changes to discard for "${selectedWorkspace.displayName}".`);
        return;
      }

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

      let result: DiscardResult | undefined;
      let remainingChanges: Change[] = [];
      // withSyncCommandBusy holds the busy state for the whole operation so the sync
      // buttons stay disabled and a progress bar shows at the top of the Agent Changes
      // view. The inner withProgress raises a notification with a "please wait" message
      // while the offline discard runs.
      await withSyncCommandBusy(selectedWorkspace.workspaceUri, async () => {
        await window.withProgress({
          location: ProgressLocation.Notification,
          title: `${DISCARD_OPERATION} operation in progress. Please wait...`,
          cancellable: false,
        }, async () => {
          const response = await lspClient.sendRequest<DiscardLocalChangesResponse>(
            LspMethods.DISCARD_LOCAL_CHANGES,
            { workspaceUri: selectedWorkspace.workspaceUri },
          );
          result = response.result;
          remainingChanges = response.localChanges;
          replaceLocalChanges(selectedWorkspace.workspaceUri, response.localChanges);
        });
      });

      reportResult(selectedWorkspace.displayName, result, remainingChanges);
    } catch (error) {
      logger.logError(
        TelemetryEventsKeys.SyncWorkspaceError,
        `Failed to execute ${DISCARD_OPERATION.toLowerCase()} operation`,
        { error, syncOperation: DISCARD_OPERATION },
      );
    }
  });

  context.subscriptions.push(command);
};

/**
 * Returns the current number of local changes for the workspace.
 *
 * Prefers a fresh, credential-free recomputation from disk via the language server so
 * the pre-flight gate and confirmation count don't depend on the possibly-stale Agent
 * Changes tree cache. Falls back to the cached tree state only if the LSP request fails
 * so this never regresses below the previous behavior.
 */
async function getLocalChangeCount(workspaceUri: string): Promise<number> {
  try {
    const request: DiffRequest = { workspaceUri };
    const response = await lspClient.sendRequest<SyncResponse>(LspMethods.GET_LOCAL_CHANGES, request);
    return response.localChanges.length;
  } catch (error) {
    logger.logWarning(TelemetryEventsKeys.SyncWorkspaceError, undefined, {
      message: `Failed to recompute local changes for ${DISCARD_OPERATION.toLowerCase()}; falling back to cached tree state.`,
      error,
    });
    return getWorkspaceChanges(workspaceUri)?.localChanges.length ?? 0;
  }
}

function reportResult(agentName: string, result: DiscardResult | undefined, remainingChanges: Change[]): void {
  if (!result) {
    return;
  }
  const revertedText = formatDiscardResultMessage(agentName, result);

  if (isDiscardComplete(result, remainingChanges)) {
    logger.logInfo(TelemetryEventsKeys.SyncWorkspaceSuccess, revertedText, { syncOperation: DISCARD_OPERATION });
    return;
  }

  const remainingPaths = getRemainingDiscardPaths(result, remainingChanges);
  const remainingNames = remainingPaths.join(', ');
  const remainingCount = remainingPaths.length;
  logger.logWarning(
    TelemetryEventsKeys.SyncWorkspaceError,
    `${revertedText} ${remainingCount} item${remainingCount === 1 ? '' : 's'} couldn't be reverted offline and can be restored with Get: ${formatPii(remainingNames, PiiRedactionType.FileUri)}.`,
    { syncOperation: DISCARD_OPERATION },
  );
}

export function isDiscardComplete(result: DiscardResult, remainingChanges: Change[]): boolean {
  return result.skipped.length === 0 && remainingChanges.length === 0;
}

export function getRemainingDiscardPaths(result: DiscardResult, remainingChanges: Change[]): string[] {
  return [...new Set([
    ...result.skipped.map(change => change.path),
    ...remainingChanges.map(change => change.uri),
  ])];
}

export function formatDiscardResultMessage(agentName: string, result: DiscardResult): string {
  const reverted = result.restored + result.deleted;
  return `Discarded ${reverted} local change${reverted === 1 ? '' : 's'} for "<pii>${agentName}</pii>".`;
}
