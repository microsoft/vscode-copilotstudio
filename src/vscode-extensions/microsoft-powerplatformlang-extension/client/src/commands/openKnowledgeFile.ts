import * as vscode from 'vscode';
import logger from '../services/logger';
import { TelemetryEventsKeys } from '../constants';

export const registerOpenKnowledgeFileCommand = (context: vscode.ExtensionContext) => {
    context.subscriptions.push(
        vscode.commands.registerCommand('virtualKnowledge.openLocal', async (uri: vscode.Uri) => {
            try {
                await vscode.workspace.fs.readFile(uri);
            } catch (error) {
                logger.logError(TelemetryEventsKeys.OpenKnowledgeFileError, `Failed to open local file for <pii>${uri.path}</pii>: ${error}`);
            }
        })
    );
};