import * as vscode from 'vscode';
import logger from '../services/logger';
import { resetAccount } from '../clients/account';
import { TelemetryEventsKeys } from '../constants';

export const registerResetAccountCommand = (context: vscode.ExtensionContext) => {
    context.subscriptions.push(vscode.commands.registerCommand('microsoft-copilot-studio.resetAccount', async () => {
        try {
            resetAccount();
        } catch (error) {
            logger.logError(TelemetryEventsKeys.ResetAccountError, `Failed to reset account: ${(error as Error).message}`);
        }
    }));
};
