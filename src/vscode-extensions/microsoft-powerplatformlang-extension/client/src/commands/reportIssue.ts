import * as vscode from 'vscode';

export const registerReportIssueCommand = (context: vscode.ExtensionContext, sessionId: string) => {
    context.subscriptions.push(vscode.commands.registerCommand('microsoft-copilot-studio.reportIssue', () => {
        vscode.commands.executeCommand('workbench.action.openIssueReporter', {
            extensionId: 'ms-CopilotStudio.vscode-copilotstudio',
            issueBody: [
                `### Session ID`,
                sessionId,
                ``,
                `<!-- Please describe the issue below -->`,
                ``,
                `### Steps to Reproduce`,
                ``,
                `### Actual Behavior`,
                ``,
                `### Expected Behavior`,
                ``
            ].join('\n')
        });
    }));
};