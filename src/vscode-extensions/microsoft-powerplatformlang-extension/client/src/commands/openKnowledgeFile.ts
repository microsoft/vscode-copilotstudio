import * as vscode from 'vscode';
import logger from '../services/logger';

export const registerOpenKnowledgeFileCommand = (context: vscode.ExtensionContext) => {
    context.subscriptions.push(
        vscode.commands.registerCommand('virtualKnowledge.openLocal', async (uri: vscode.Uri) => {
            try {
                await vscode.workspace.fs.readFile(uri);
            } catch (error) {
                const message = `Failed to open local file for <pii>${uri.path}</pii>: ${error}`;
                logger.logError(message, 'knowledge', { showDialog: true });
                logger.logFeatureEvent({
                    feature: 'knowledge',
                    operation: 'openLocalFile',
                    outcome: 'failure',
                    errorType: error instanceof Error ? error.name : 'Error',
                    errorMessage: message,
                });
            }
        })
    );
};