import * as vscode from 'vscode';
import { WorkflowSymbolProvider } from './workflowSymbolProvider';
import { WorkflowCodeLensProvider } from './workflowCodeLens';
import { registerWorkflowCommands } from './workflowCommands';
import { WorkflowDiagnosticsManager } from './workflowDiagnostics';

const WORKFLOW_SELECTOR: vscode.DocumentSelector = {
  pattern: '**/workflows/**/workflow.json',
};

export function initializeWorkflowFeatures(context: vscode.ExtensionContext): void {
  registerWorkflowCommands(context);

  context.subscriptions.push(
    vscode.languages.registerDocumentSymbolProvider(WORKFLOW_SELECTOR, new WorkflowSymbolProvider()),
    vscode.languages.registerCodeLensProvider(WORKFLOW_SELECTOR, new WorkflowCodeLensProvider()),
  );

  new WorkflowDiagnosticsManager().register(context);
}
