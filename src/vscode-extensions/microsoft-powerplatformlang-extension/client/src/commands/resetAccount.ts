import * as vscode from 'vscode';
import logger from '../services/logger';
import { resetAccount } from '../clients/account';

export const registerResetAccountCommand = (context: vscode.ExtensionContext) => {
    context.subscriptions.push(vscode.commands.registerCommand('microsoft-copilot-studio.resetAccount', async () => {
        try {
            resetAccount();
            logger.logFeatureEvent({
                feature: 'auth',
                operation: 'resetAccount',
                outcome: 'success',
            });
        } catch (error) {
            const message = (error as Error).message;
            logger.logError(`Failed to reset account: ${message}`, 'auth', { showDialog: true });
            logger.logFeatureEvent({
                feature: 'auth',
                operation: 'resetAccount',
                outcome: 'failure',
                errorType: (error as Error).name || 'Error',
                errorMessage: message,
            });
        }
    }));
};
