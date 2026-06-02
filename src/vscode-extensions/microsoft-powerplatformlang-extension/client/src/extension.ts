// The module 'vscode' contains the VS Code extensibility API
// Import the module and reference it with the alias vscode in your code below
import * as vscode from 'vscode';
import { randomUUID } from "node:crypto";
import lspClientService from './services/lspClient';
import logger from './services/logger';
import { configureTreeView } from './clone/tree';
import { initializeAgentDirectoryHandler } from './clone/agentDirectory';
import { initializeWorkspaceManager } from './sync/workspaceManager';
import { initializeWorkspaceScm } from './sync/workspaceScm';
import { initializeAgentChangesTree } from './sync/agentChangesTreeProvider';
import { addWorkspaceChangeSubscription, getAllWorkspaces } from './sync/localWorkspaces';
import { initializeVirtualKnowledgeTree } from './knowledgeFiles/virtualKnowledgeFile';
import { registerCloneAgentCommand } from './commands/cloneAgent';
import { registerSessionInfoCommand } from './commands/sessionInfo';
import { registerReportIssueCommand } from './commands/reportIssue';
import { registerOpenKnowledgeFileCommand } from './commands/openKnowledgeFile';
import { registerResetAccountCommand } from './commands/resetAccount';
import { registerSyncCommands } from './commands/syncWorkspace';
import { registerReattachAgentCommand } from './commands/reattachAgent';
import { registerTelemetrySettingsListeners } from './services/telemetry';
import { maybeOpenFileFromPostOpen } from './startup/postOpen';
import { registerSignInCommand } from './commands/signIn';
import { registerOriginalFileSystemProvider } from './commands/originalFileSystemProvider';
import { registerRemoteFileSystemProvider } from './commands/remoteFileSystemProvider';

// This method is called when your extension is activated
// Your extension is activated the very first time the command is executed
export async function activate(context: vscode.ExtensionContext) {
  const sessionId = randomUUID();
  const isDebugging = process.env.VSCODE_DEBUG === 'true';

  // Create output channel for LSP logs and extension logs.
  // Using `createLogOutputChannel` gives each line a timestamp and a color-coded
  // [error]/[warning]/[info] prefix.
  const outputChannel = vscode.window.createOutputChannel("Copilot Studio Language Server", { log: true });
  if (isDebugging) {
    outputChannel.show();
  }

  // Initialize logger with output channel for both telemetry and output window writes.
  // Logger includes the sessionId for correlation across telemetry and logs.
  logger.initialize(context, sessionId, outputChannel);
  logger.logInfo(`Extension activating (debug=${isDebugging})`, 'startup');

  // Register commands and features that do not depend on the LSP client
  registerSignInCommand(context);
  registerResetAccountCommand(context);
  registerReportIssueCommand(context, sessionId);

  // Initialize and start LSP client
  try {
    await lspClientService.initializeAndStart(context, outputChannel, sessionId);
  } catch (error) {
    return;
  }

  // Register commands and features that depend on the LSP client
  registerTelemetrySettingsListeners(context);
  initializeAgentDirectoryHandler(context);
  registerOriginalFileSystemProvider(context);
  registerRemoteFileSystemProvider(context);
  configureTreeView(context);
  initializeWorkspaceManager(context);
  initializeWorkspaceScm(context);
  initializeAgentChangesTree(context);
  initializeVirtualKnowledgeTree(context);
  registerSyncCommands(context);
  registerCloneAgentCommand(context);
  registerSessionInfoCommand(context, sessionId);
  registerOpenKnowledgeFileCommand(context);
  registerReattachAgentCommand(context);

  // We use a post-window-reload instruction to open the cloned agent file
  // after the workspace has been added to the window.
  // This is a one-shot operation, so we just fire-and-forget here.
  void maybeOpenFileFromPostOpen(context)
    .catch(err => {
      logger.logError(
        `Post-open file logic failed: ${(err as Error).message}`,
        'startup',
        { showDialog: false }
      );
    });

  // expose test-friendly API
  return {
    addWorkspaceChangeSubscription,
    getAllWorkspaces
  };
}

// This method is called when your extension is deactivated
export async function deactivate() {
  await lspClientService.dispose();
}
