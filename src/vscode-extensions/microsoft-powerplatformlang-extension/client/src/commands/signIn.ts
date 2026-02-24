import * as vscode from 'vscode';
import logger from '../services/logger';
import { DefaultCoreServicesClusterCategory, TelemetryEventsKeys } from '../constants';
import { signIn } from '../clients/account';

export const registerSignInCommand = (context: vscode.ExtensionContext) => {
    context.subscriptions.push(vscode.commands.registerCommand('microsoft-copilot-studio.signin', async () => {
        try {
            await signIn(DefaultCoreServicesClusterCategory);
        } catch (error) {
            logger.logError(TelemetryEventsKeys.SignInError, `Failed to sign in: ${(error as Error).message}`);
        }
    }));
};
