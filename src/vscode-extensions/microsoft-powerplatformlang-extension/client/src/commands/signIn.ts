import * as vscode from 'vscode';
import logger from '../services/logger';
import { DefaultCoreServicesClusterCategory, TelemetryEventsKeys } from '../constants';
import { signIn, clearRecoverableAuthState } from '../clients/account';

export const registerSignInCommand = (context: vscode.ExtensionContext) => {
    context.subscriptions.push(vscode.commands.registerCommand('microsoft-copilot-studio.signIn', async () => {
        try {
            clearRecoverableAuthState();
            await signIn(DefaultCoreServicesClusterCategory);
        } catch (error) {
            logger.logError(TelemetryEventsKeys.SignInError, 'Failed to sign in', { error });
        }
    }));
};
