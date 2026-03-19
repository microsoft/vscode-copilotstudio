import * as vscode from 'vscode';
import logger from '../services/logger';
import { DefaultCoreServicesClusterCategory, TelemetryEventsKeys } from '../constants';
import { cloneAgentToLocalFolder, getAgentInfo } from '../clone/getAgent';
import { isCopilotStudioTreeItem, TreeItemKind } from '../clone/tree';
import { IdentifyAgentResponse } from '../types';

export const registerCloneAgentCommand = (context: vscode.ExtensionContext) => {
  const cloneAgentCommand = vscode.commands.registerCommand('microsoft-copilot-studio.cloneAgent', async (treeItem?: unknown) => {
    logger.logInfo(TelemetryEventsKeys.CloneAgentClick);

    let agent: IdentifyAgentResponse | undefined;
    try {
      // When invoked from tree view context menu, treeItem contains the agent and environment
      // Using discriminated union: isCopilotStudioTreeItem validates structure, then kind check narrows type
      if (isCopilotStudioTreeItem(treeItem) && treeItem.kind === TreeItemKind.Agent) {
        agent = {
          agentIdentifier: {
            clusterCategory: DefaultCoreServicesClusterCategory,
            environmentId: treeItem.environment.environmentId,
            agentId: treeItem.agent.agentId,
          },
          environmentInfo: treeItem.environment,
          agentInfo: treeItem.agent,
          accountId: undefined,
        };
      } else {
        // Standard flow: show environment/agent picker
        const clipboardContent = await vscode.env.clipboard.readText();
        const potentialAgentUrl = clipboardContent.startsWith("https://") && clipboardContent.length < 250
          ? clipboardContent
          : undefined;

        agent = await getAgentInfo(potentialAgentUrl, context);
      }
      await cloneAgentToLocalFolder(agent, context);
    } catch (error) {
      logger.logError(TelemetryEventsKeys.CloneAgentError, `Error cloning agent: ${(error as Error).message}`, {
        agentId: agent?.agentInfo?.agentId,
        environmentId: agent?.environmentInfo?.environmentId,
      });
    }
  });
  context.subscriptions.push(cloneAgentCommand);
};