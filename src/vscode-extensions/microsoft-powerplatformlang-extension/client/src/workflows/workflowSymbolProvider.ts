import * as vscode from 'vscode';
import { parseWorkflow } from './workflowParser';
import type { WfNode } from './workflowParser';

function graphKindToSymbolKind(graphType: string | undefined): vscode.SymbolKind {
  switch (graphType) {
    case 'agent':
      return vscode.SymbolKind.Class;
    case 'prompt':
      return vscode.SymbolKind.String;
    case 'classify':
      return vscode.SymbolKind.Enum;
    case 'm365Copilot':
      return vscode.SymbolKind.Event;
    case 'connector':
      return vscode.SymbolKind.Interface;
    case 'variable':
      return vscode.SymbolKind.Variable;
    case 'loop':
      return vscode.SymbolKind.Array;
    case 'ifElse':
      return vscode.SymbolKind.Operator;
    case 'builtinFunction':
      return vscode.SymbolKind.Function;
    case 'canvasNote':
      return vscode.SymbolKind.Object;
    default:
      return undefined as unknown as vscode.SymbolKind;
  }
}

function actionTypeToSymbolKind(actionType: string | undefined): vscode.SymbolKind {
  switch (actionType) {
    case 'Switch':
      return vscode.SymbolKind.Enum;
    case 'If':
      return vscode.SymbolKind.Operator;
    case 'Foreach':
    case 'Until':
    case 'Scope':
      return vscode.SymbolKind.Array;
    case 'SetVariable':
    case 'InitializeVariable':
      return vscode.SymbolKind.Variable;
    case 'Wait':
      return vscode.SymbolKind.Function;
    case 'OpenApiConnectionWebhook':
      return vscode.SymbolKind.Event;
    case 'OpenApiConnection':
      return vscode.SymbolKind.Method;
    default:
      return vscode.SymbolKind.Field;
  }
}

function pickKind(node: WfNode): vscode.SymbolKind {
  if (node.kind === 'branch') {
    return vscode.SymbolKind.Namespace;
  }
  const graphKind = graphKindToSymbolKind(node.graphType);
  if (graphKind !== undefined) {
    return graphKind;
  }
  return actionTypeToSymbolKind(node.actionType);
}

function toRange(document: vscode.TextDocument, offset: number, length: number): vscode.Range {
  const safeLength = Math.max(length, 1);
  return new vscode.Range(
    document.positionAt(offset),
    document.positionAt(offset + safeLength),
  );
}

function buildSymbol(document: vscode.TextDocument, node: WfNode): vscode.DocumentSymbol {
  const fullRange = toRange(document, node.range.offset, node.range.length);
  const selRangeRaw = toRange(document, node.selectionRange.offset, node.selectionRange.length);
  const selRange = fullRange.contains(selRangeRaw) ? selRangeRaw : fullRange;

  const detail = node.kind === 'branch' ? '' : node.detail ?? '';
  const symbol = new vscode.DocumentSymbol(
    node.label,
    detail,
    pickKind(node),
    fullRange,
    selRange,
  );
  symbol.children = node.children.map((child) => buildSymbol(document, child));
  return symbol;
}

export class WorkflowSymbolProvider implements vscode.DocumentSymbolProvider {
  public provideDocumentSymbols(
    document: vscode.TextDocument,
  ): vscode.ProviderResult<vscode.DocumentSymbol[]> {
    const model = parseWorkflow(document.getText());
    if (!model.valid) {
      return [];
    }
    return model.root.children.map((child) => buildSymbol(document, child));
  }
}
