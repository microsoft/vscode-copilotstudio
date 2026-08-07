import * as vscode from 'vscode';
import { AuthError, clearAuthAccountState } from '../clients/account';
import { CopilotStudioWorkspace } from './localWorkspaces';
import { TelemetryEventsKeys } from '../constants';
import logger, { formatPii, PiiRedactionType } from '../services/logger';

const REATTACH_COMMAND = 'microsoft-copilot-studio.reattachAgent';

export async function handleSyncAuthError(workspace: CopilotStudioWorkspace, error: unknown, retry?: () => Promise<void>): Promise<boolean> {
  if (!(error instanceof AuthError)) {
    return false;
  }

  const accountInfo = workspace.syncInfo?.accountInfo;
  const accountLabel = accountInfo?.accountEmail ?? error.accountEmail ?? accountInfo?.accountId ?? error.accountId ?? 'the linked account';

  if (error.classification === 'cancelled') {
    logger.logInfo(TelemetryEventsKeys.AuthPromptSuppressed, undefined, { message: `Auth prompt cancelled for ${formatPii(accountLabel, PiiRedactionType.EmailAddress)}` });
    return true;
  }

  const signIn = 'Sign in';
  const retarget = 'Retarget agent…';
  let choice = await vscode.window.showWarningMessage(`Couldn't sign in with ${accountLabel}. Sign in again, or retarget this agent to an environment you can access.`, signIn, retarget);

  if (choice === signIn) {
    clearAuthAccountState(accountInfo?.accountId, accountInfo?.accountEmail);
    if (retry) {
      await retry();
    }
  } else if (choice === retarget) {
    await vscode.commands.executeCommand(REATTACH_COMMAND, { workspace });
  }
  return true;
}
