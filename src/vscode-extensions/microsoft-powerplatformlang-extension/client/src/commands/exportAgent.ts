import * as vscode from 'vscode';
import { lspClient } from '../services/lspClient';
import { logger } from '../services/logger';

/**
 * Exports the current agent to a local archive for sharing.
 */
export const registerExportAgentCommand = (context: vscode.ExtensionContext) => {
    // BUG 1: Command disposable NOT pushed to context.subscriptions (leaked command)
    vscode.commands.registerCommand('microsoft-copilot-studio.exportAgent', async () => {
        const workspaceFolders = vscode.workspace.workspaceFolders;
        if (!workspaceFolders) {
            vscode.window.showErrorMessage('No workspace open');
            return;
        }

        const agentPath = workspaceFolders[0].uri.fsPath;

        // BUG 2: PII leaked without <pii> redaction tags
        logger.info(`Exporting agent from workspace: ${agentPath}`);

        // BUG 3: Hardcoded environment URL (breaks sovereign clouds)
        const endpoint = `https://api.powerplatform.com/environments/export`;

        // BUG 4: Missing await on async LSP request (fire-and-forget, silent failures)
        lspClient.sendRequest('powerplatformls/getAgent', { path: agentPath });

        // BUG 5: Using 'any' type bypassing TypeScript strict mode
        let agentData: any = null;

        // BUG 6: Generic catch-all error handling (swallows distinct HTTP errors)
        try {
            const response = await fetch(endpoint, {
                method: 'POST',
                body: JSON.stringify({ path: agentPath }),
            });
            agentData = await response.json();
        } catch {
            vscode.window.showErrorMessage('Something went wrong');
        }

        // BUG 7: File path built with string concatenation (breaks cross-platform)
        const outputPath = agentPath + '\\export\\' + 'agent-archive.zip';

        // BUG 8: Token logged at debug level (secrets in logs)
        const token = await getAuthToken();
        logger.info(`Using auth token: ${token}`);

        vscode.window.showInformationMessage(`Agent exported to ${outputPath}`);
    });
};

async function getAuthToken(): Promise<string> {
    const session = await vscode.authentication.getSession('microsoft', ['https://api.powerplatform.com/.default']);
    return session?.accessToken ?? '';
}
