import * as vscode from 'vscode';
import logger from '../services/logger';
import { TelemetryEventsKeys } from '../constants';
import { isWorkflowFile } from '../workflows/workflowParser';
import { WorkflowVisualizerController } from './workflowVisualizer';

const VISUALIZE_WORKFLOW_COMMAND = 'microsoft-copilot-studio.workflow.visualize';

export function initializeWorkflowVisualization(context: vscode.ExtensionContext): void {
  context.subscriptions.push(
    vscode.commands.registerCommand(VISUALIZE_WORKFLOW_COMMAND, async (resource?: vscode.Uri) => {
      try {
        const uri = resource ?? vscode.window.activeTextEditor?.document.uri;
        if (!uri || !isWorkflowFile(uri.fsPath)) {
          void vscode.window.showWarningMessage('Open a workflow.json file to visualize it.');
          return;
        }
        const document = await vscode.workspace.openTextDocument(uri);
        WorkflowVisualizerController.show(context, document);
      } catch (error) {
        logger.logError(TelemetryEventsKeys.WorkflowVisualizeError, `Failed to open workflow visualizer: <pii>${error}</pii>`);
      }
    }),
  );
}
