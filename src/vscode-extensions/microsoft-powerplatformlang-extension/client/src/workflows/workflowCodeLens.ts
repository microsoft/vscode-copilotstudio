import * as vscode from 'vscode';
import {
  parseWorkflow,
  findEmbeddedJsonFields,
} from './workflowParser';
import type { WfNode } from './workflowParser';
import {
  FOCUS_NODE_COMMAND,
  EDIT_EMBEDDED_JSON_COMMAND,
} from './workflowCommands';
import type { FocusNodeArgs, EditEmbeddedJsonArgs } from './workflowCommands';

export class WorkflowCodeLensProvider implements vscode.CodeLensProvider {
  public provideCodeLenses(document: vscode.TextDocument): vscode.ProviderResult<vscode.CodeLens[]> {
    const lenses: vscode.CodeLens[] = [];
    const text = document.getText();

    const model = parseWorkflow(text);
    if (!model.valid) {
      return [];
    }

    const addFocusLenses = (nodes: WfNode[], trail: string[]): void => {
      for (const node of nodes) {
        if (node.kind === 'action') {
          const startPos = document.positionAt(node.range.offset);
          const lensRange = new vscode.Range(startPos, startPos);
          const context = trail.length > 0 ? ` (in: ${trail.join(' › ')})` : '';
          const args: FocusNodeArgs = {
            uri: document.uri.toString(),
            offset: node.range.offset,
            length: node.range.length,
          };
          lenses.push(
            new vscode.CodeLens(lensRange, {
              title: `$(edit) Focus · ${node.label}${context}`,
              command: FOCUS_NODE_COMMAND,
              arguments: [args],
            }),
          );
          for (const branch of node.children) {
            const branchTrail = branch.kind === 'branch' && branch.label
              ? [...trail, node.label, branch.label]
              : [...trail, node.label];
            addFocusLenses(branch.children, branchTrail);
          }
        }
      }
    };
    addFocusLenses(model.root.children, []);

    for (const field of findEmbeddedJsonFields(text)) {
      const startPos = document.positionAt(field.keyRange.offset);
      const lensRange = new vscode.Range(startPos, startPos);
      const args: EditEmbeddedJsonArgs = {
        uri: document.uri.toString(),
        path: field.path,
        label: field.label,
      };
      lenses.push(
        new vscode.CodeLens(lensRange, {
          title: `$(json) Edit as JSON · ${field.label}`,
          command: EDIT_EMBEDDED_JSON_COMMAND,
          arguments: [args],
        }),
      );
    }

    return lenses;
  }
}
