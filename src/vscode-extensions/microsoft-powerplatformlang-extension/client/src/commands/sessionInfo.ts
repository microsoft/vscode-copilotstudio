import * as os from 'os';
import * as vscode from 'vscode';
import { lspClient } from '../services/lspClient';

export const registerSessionInfoCommand = (context: vscode.ExtensionContext, sessionId: string) => {
  const sessionInfoCommand = vscode.commands.registerCommand('microsoft-copilot-studio.sessionInfo', async () => {
    // Process Id is in lspClient._serverProcess
    const pid = (<any>lspClient)._serverProcess.pid;
    const currentUTCTime = new Date().toISOString();
    const platform = os.platform();
    const arch = os.arch();

    // Modal will display multilines. non-modal will strip all \n. 
    // User can then copy  and pase in bug reports. 
    // This should not include any PII - instead get that from logs. 		
    const info = `Microsoft Copilot Studio Session Info\nProcessId:${pid}\nTimestamp: ${currentUTCTime}\nSessionId: ${sessionId}\nPlatform: ${platform}-${arch}\n`;
    vscode.window.showInformationMessage(info, { modal: true });
  });
  context.subscriptions.push(sessionInfoCommand);
};
