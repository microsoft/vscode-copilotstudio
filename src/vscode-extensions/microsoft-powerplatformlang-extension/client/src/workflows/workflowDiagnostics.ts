import * as vscode from 'vscode';
import { parseTree } from 'jsonc-parser';
import type { Node } from 'jsonc-parser';
import { isWorkflowFile } from './workflowParser';

interface RawDiagnostic {
  offset: number;
  length: number;
  message: string;
  severity: vscode.DiagnosticSeverity;
}

const SOURCE = 'Copilot Studio Workflow';
const DEBOUNCE_MS = 400;
const CONTAINER_TYPES = new Set(['Switch', 'If', 'Foreach', 'Until', 'Scope']);

function findProp(objNode: Node | undefined, name: string): Node | undefined {
  if (!objNode || objNode.type !== 'object' || !objNode.children) {
    return undefined;
  }
  for (const prop of objNode.children) {
    if (prop.type === 'property' && prop.children && prop.children.length >= 2) {
      const keyNode = prop.children[0];
      if (keyNode.value === name) {
        return prop.children[1];
      }
    }
  }
  return undefined;
}

function getString(objNode: Node | undefined, name: string): string | undefined {
  const v = findProp(objNode, name);
  return v && typeof v.value === 'string' ? v.value : undefined;
}

interface GraphInfo {
  ids: Set<string>;
  duplicateIds: { id: string; offset: number; length: number }[];
  edges: { node: Node; source: string | undefined; target: string | undefined }[];
}

function collectGraph(definition: Node | undefined): GraphInfo {
  const info: GraphInfo = { ids: new Set(), duplicateIds: [], edges: [] };
  const triggers = findProp(definition, 'triggers');
  if (!triggers || triggers.type !== 'object' || !triggers.children) {
    return info;
  }
  for (const triggerProp of triggers.children) {
    if (triggerProp.type !== 'property' || !triggerProp.children || triggerProp.children.length < 2) {
      continue;
    }
    const triggerVal = triggerProp.children[1];
    const metadata = findProp(triggerVal, 'metadata');
    const associatedData = findProp(metadata, 'associatedData');
    const graph = findProp(associatedData, 'graph');

    const nodes = findProp(graph, 'nodes');
    if (nodes && nodes.type === 'array' && nodes.children) {
      for (const n of nodes.children) {
        const idNode = findProp(n, 'id');
        const id = idNode && typeof idNode.value === 'string' ? idNode.value : undefined;
        if (!id) {
          continue;
        }
        if (info.ids.has(id)) {
          info.duplicateIds.push({ id, offset: idNode!.offset, length: idNode!.length });
        } else {
          info.ids.add(id);
        }
      }
    }

    const edges = findProp(graph, 'edges');
    if (edges && edges.type === 'array' && edges.children) {
      for (const e of edges.children) {
        info.edges.push({
          node: e,
          source: getString(e, 'source'),
          target: getString(e, 'target'),
        });
      }
    }
  }
  return info;
}

function walkActionsScope(
  actionsObj: Node | undefined,
  graph: GraphInfo,
  out: RawDiagnostic[],
): void {
  if (!actionsObj || actionsObj.type !== 'object' || !actionsObj.children) {
    return;
  }

  const siblingKeys = new Set<string>();
  for (const prop of actionsObj.children) {
    if (prop.type === 'property' && prop.children && prop.children.length >= 1) {
      siblingKeys.add(String(prop.children[0].value));
    }
  }

  for (const prop of actionsObj.children) {
    if (prop.type !== 'property' || !prop.children || prop.children.length < 2) {
      continue;
    }
    const keyNode = prop.children[0];
    const valueNode = prop.children[1];
    const actionType = getString(valueNode, 'type');

    const metadata = findProp(valueNode, 'metadata');
    const nodeIdNode = findProp(metadata, 'nodeId');
    if (nodeIdNode && typeof nodeIdNode.value === 'string') {
      const nodeId = nodeIdNode.value;
      if (graph.ids.size > 0 && !graph.ids.has(nodeId)) {
        out.push({
          offset: nodeIdNode.offset,
          length: nodeIdNode.length,
          message: `Action "${String(keyNode.value)}" references graph node "${nodeId}", which does not exist in the trigger graph. The action tree and graph are out of sync.`,
          severity: vscode.DiagnosticSeverity.Warning,
        });
      }
    }

    const runAfter = findProp(valueNode, 'runAfter');
    if (runAfter && runAfter.type === 'object' && runAfter.children) {
      for (const raProp of runAfter.children) {
        if (raProp.type !== 'property' || !raProp.children || raProp.children.length < 1) {
          continue;
        }
        const raKeyNode = raProp.children[0];
        const raKey = String(raKeyNode.value);
        if (!siblingKeys.has(raKey)) {
          out.push({
            offset: raKeyNode.offset,
            length: raKeyNode.length,
            message: `runAfter for "${String(keyNode.value)}" references "${raKey}", which is not a sibling action in this scope.`,
            severity: vscode.DiagnosticSeverity.Warning,
          });
        }
      }
    }

    if (actionType && CONTAINER_TYPES.has(actionType)) {
      walkContainer(valueNode, actionType, graph, out);
    }
  }
}

function walkContainer(
  valueNode: Node,
  actionType: string,
  graph: GraphInfo,
  out: RawDiagnostic[],
): void {
  switch (actionType) {
    case 'If': {
      walkActionsScope(findProp(valueNode, 'actions'), graph, out);
      walkActionsScope(findProp(findProp(valueNode, 'else'), 'actions'), graph, out);
      break;
    }
    case 'Switch': {
      const cases = findProp(valueNode, 'cases');
      if (cases && cases.type === 'object' && cases.children) {
        for (const caseProp of cases.children) {
          if (caseProp.type !== 'property' || !caseProp.children || caseProp.children.length < 2) {
            continue;
          }
          walkActionsScope(findProp(caseProp.children[1], 'actions'), graph, out);
        }
      }
      walkActionsScope(findProp(findProp(valueNode, 'default'), 'actions'), graph, out);
      break;
    }
    default: {
      walkActionsScope(findProp(valueNode, 'actions'), graph, out);
      break;
    }
  }
}

function checkMalformedEmbeddedJson(node: Node, out: RawDiagnostic[]): void {
  if (node.type === 'string' && typeof node.value === 'string') {
    const trimmed = node.value.trim();
    if (trimmed.startsWith('{"') || trimmed.startsWith('[{')) {
      try {
        JSON.parse(node.value);
      } catch {
        out.push({
          offset: node.offset,
          length: node.length,
          message: 'This value appears to contain embedded JSON, but it could not be parsed. Use "Edit as JSON" to fix it.',
          severity: vscode.DiagnosticSeverity.Warning,
        });
      }
    }
    return;
  }
  if (node.children) {
    for (const child of node.children) {
      if (node.type === 'property' && child === node.children[0]) {
        continue;
      }
      checkMalformedEmbeddedJson(child, out);
    }
  }
}

function computeWorkflowDiagnostics(text: string): RawDiagnostic[] {
  let tree: Node | undefined;
  try {
    tree = parseTree(text);
  } catch {
    return [];
  }
  if (!tree) {
    return [];
  }

  const out: RawDiagnostic[] = [];
  const properties = findProp(tree, 'properties');
  const definition = findProp(properties, 'definition');
  if (!definition) {
    return out;
  }

  const graph = collectGraph(definition);

  for (const dup of graph.duplicateIds) {
    out.push({
      offset: dup.offset,
      length: dup.length,
      message: `Duplicate graph node id "${dup.id}". Node ids must be unique.`,
      severity: vscode.DiagnosticSeverity.Warning,
    });
  }

  for (const edge of graph.edges) {
    for (const endpoint of ['source', 'target'] as const) {
      const value = edge[endpoint];
      if (value !== undefined && graph.ids.size > 0 && !graph.ids.has(value)) {
        const endpointNode = findProp(edge.node, endpoint);
        if (endpointNode) {
          out.push({
            offset: endpointNode.offset,
            length: endpointNode.length,
            message: `Edge ${endpoint} "${value}" does not reference an existing graph node.`,
            severity: vscode.DiagnosticSeverity.Warning,
          });
        }
      }
    }
  }

  walkActionsScope(findProp(definition, 'actions'), graph, out);
  checkMalformedEmbeddedJson(tree, out);

  return out;
}

export class WorkflowDiagnosticsManager {
  private readonly collection: vscode.DiagnosticCollection;
  private readonly timers = new Map<string, NodeJS.Timeout>();

  constructor() {
    this.collection = vscode.languages.createDiagnosticCollection('copilotStudioWorkflow');
  }

  register(context: vscode.ExtensionContext): void {
    context.subscriptions.push(
      this.collection,
      vscode.workspace.onDidOpenTextDocument((doc) => this.scheduleValidate(doc)),
      vscode.workspace.onDidChangeTextDocument((e) => this.scheduleValidate(e.document)),
      vscode.workspace.onDidCloseTextDocument((doc) => this.clear(doc)),
    );

    for (const doc of vscode.workspace.textDocuments) {
      this.scheduleValidate(doc);
    }
  }

  private scheduleValidate(document: vscode.TextDocument): void {
    if (!isWorkflowFile(document.uri.fsPath)) {
      return;
    }
    const key = document.uri.toString();
    const existing = this.timers.get(key);
    if (existing) {
      clearTimeout(existing);
    }
    this.timers.set(
      key,
      setTimeout(() => {
        this.timers.delete(key);
        this.validate(document);
      }, DEBOUNCE_MS),
    );
  }

  private validate(document: vscode.TextDocument): void {
    if (document.isClosed) {
      return;
    }
    const raw = computeWorkflowDiagnostics(document.getText());
    const diagnostics = raw.map((r) => {
      const range = new vscode.Range(
        document.positionAt(r.offset),
        document.positionAt(r.offset + r.length),
      );
      const diagnostic = new vscode.Diagnostic(range, r.message, r.severity);
      diagnostic.source = SOURCE;
      return diagnostic;
    });
    this.collection.set(document.uri, diagnostics);
  }

  private clear(document: vscode.TextDocument): void {
    const key = document.uri.toString();
    const existing = this.timers.get(key);
    if (existing) {
      clearTimeout(existing);
      this.timers.delete(key);
    }
    this.collection.delete(document.uri);
  }
}
