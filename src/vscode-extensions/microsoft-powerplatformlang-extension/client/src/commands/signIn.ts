import * as vscode from 'vscode';
import logger from '../services/logger';
import { DefaultCoreServicesClusterCategory } from '../constants';
import { signIn } from '../clients/account';

export const registerSignInCommand = (context: vscode.ExtensionContext) => {
    context.subscriptions.push(vscode.commands.registerCommand('microsoft-copilot-studio.signIn', async () => {
        const startTime = Date.now();
        try {
            await signIn(DefaultCoreServicesClusterCategory);
            logger.logFeatureEvent({
                feature: 'auth',
                operation: 'signIn',
                outcome: 'success',
                durationMs: Date.now() - startTime,
            });
        } catch (error) {
            const message = (error as Error).message;
            logger.logError(`Failed to sign in: ${message}`, 'auth', { showDialog: true });
            logger.logFeatureEvent({
                feature: 'auth',
                operation: 'signIn',
                outcome: 'failure',
                errorType: (error as Error).name || 'Error',
                errorMessage: message,
                durationMs: Date.now() - startTime,
            });
        }
    }));
};
