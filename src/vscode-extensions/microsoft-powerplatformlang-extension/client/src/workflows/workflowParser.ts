import { parseTree, findNodeAtLocation } from 'jsonc-parser';
import type { Node } from 'jsonc-parser';

interface WfSpan {
  offset: number;
  length: number;
}

type WfNodeKind = 'root' | 'action' | 'branch';

export interface WfNode {
  label: string;
  detail?: string;
  kind: WfNodeKind;
  actionType?: string;
  graphType?: string;
  range: WfSpan;
  selectionRange: WfSpan;
  children: WfNode[];
}

interface WorkflowModel {
  root: WfNode;
  valid: boolean;
}

interface GraphNodeInfo {
  type: string;
  name: string;
}

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

function span(node: Node): WfSpan {
  return { offset: node.offset, length: node.length };
}

function buildGraphIndex(definition: Node | undefined): Map<string, GraphNodeInfo> {
  const index = new Map<string, GraphNodeInfo>();
  const triggers = findProp(definition, 'triggers');
  if (!triggers || !triggers.children) {
    return index;
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
    if (!nodes || nodes.type !== 'array' || !nodes.children) {
      continue;
    }
    for (const n of nodes.children) {
      const id = getString(n, 'id');
      if (!id) {
        continue;
      }
      index.set(id, {
        type: getString(n, 'type') ?? '',
        name: getString(n, 'name') ?? '',
      });
    }
  }
  return index;
}

const CONTAINER_TYPES = new Set(['Switch', 'If', 'Foreach', 'Until', 'Scope']);

function isContainerType(actionType: string | undefined): boolean {
  return actionType !== undefined && CONTAINER_TYPES.has(actionType);
}

function walkActions(actionsObj: Node | undefined, graph: Map<string, GraphNodeInfo>): WfNode[] {
  const result: WfNode[] = [];
  if (!actionsObj || actionsObj.type !== 'object' || !actionsObj.children) {
    return result;
  }
  for (const prop of actionsObj.children) {
    if (prop.type !== 'property' || !prop.children || prop.children.length < 2) {
      continue;
    }
    const keyNode = prop.children[0];
    const valueNode = prop.children[1];
    const key = String(keyNode.value);
    const actionType = getString(valueNode, 'type');

    const metadata = findProp(valueNode, 'metadata');
    const nodeId = getString(metadata, 'nodeId');
    const description = getString(metadata, 'description');
    const graphInfo = nodeId ? graph.get(nodeId) : undefined;

    const label = graphInfo?.name || description || key;

    const node: WfNode = {
      label,
      detail: actionType,
      kind: 'action',
      actionType,
      graphType: graphInfo?.type,
      range: span(prop),
      selectionRange: span(keyNode),
      children: [],
    };

    if (isContainerType(actionType)) {
      node.children = walkContainerBranches(valueNode, actionType!, graph);
    }

    result.push(node);
  }
  return result;
}

function walkContainerBranches(
  valueNode: Node,
  actionType: string,
  graph: Map<string, GraphNodeInfo>,
): WfNode[] {
  const branches: WfNode[] = [];

  const pushBranch = (label: string, container: Node | undefined): void => {
    if (!container) {
      return;
    }
    const actionsObj = findProp(container, 'actions');
    const children = walkActions(actionsObj, graph);
    const rangeSource = actionsObj ?? container;
    branches.push({
      label,
      kind: 'branch',
      range: span(rangeSource),
      selectionRange: span(rangeSource),
      children,
    });
  };

  switch (actionType) {
    case 'If': {
      const thisActions = findProp(valueNode, 'actions');
      if (thisActions) {
        branches.push({
          label: 'If',
          kind: 'branch',
          range: span(thisActions),
          selectionRange: span(thisActions),
          children: walkActions(thisActions, graph),
        });
      }
      pushBranch('Else', findProp(valueNode, 'else'));
      break;
    }
    case 'Switch': {
      const cases = findProp(valueNode, 'cases');
      if (cases && cases.type === 'object' && cases.children) {
        for (const caseProp of cases.children) {
          if (caseProp.type !== 'property' || !caseProp.children || caseProp.children.length < 2) {
            continue;
          }
          const caseKey = String(caseProp.children[0].value);
          pushBranch(`case "${caseKey}"`, caseProp.children[1]);
        }
      }
      pushBranch('default', findProp(valueNode, 'default'));
      break;
    }
    case 'Foreach':
    case 'Until':
    case 'Scope':
    default: {
      const actionsObj = findProp(valueNode, 'actions');
      return walkActions(actionsObj, graph);
    }
  }

  return branches;
}

export function parseWorkflow(text: string): WorkflowModel {
  const emptyRoot: WfNode = {
    label: 'Workflow',
    kind: 'root',
    range: { offset: 0, length: 0 },
    selectionRange: { offset: 0, length: 0 },
    children: [],
  };

  let tree: Node | undefined;
  try {
    tree = parseTree(text);
  } catch {
    return { root: emptyRoot, valid: false };
  }
  if (!tree) {
    return { root: emptyRoot, valid: false };
  }

  const properties = findProp(tree, 'properties');
  const definition = findProp(properties, 'definition');
  const actions = findProp(definition, 'actions');
  if (!actions) {
    return { root: emptyRoot, valid: false };
  }

  const graph = buildGraphIndex(definition);
  const root: WfNode = {
    ...emptyRoot,
    range: span(actions),
    selectionRange: span(actions),
    children: walkActions(actions, graph),
  };

  return { root, valid: true };
}

export function isWorkflowFile(fsPathOrPath: string): boolean {
  const normalized = fsPathOrPath.replace(/\\/g, '/').toLowerCase();
  return /\/workflows\/[^/]+\/workflow\.json$/.test(normalized);
}

interface EmbeddedJsonField {
  path: (string | number)[];
  label: string;
  keyRange: WfSpan;
  valueRange: WfSpan;
}

function looksLikeJson(value: string): boolean {
  const trimmed = value.trim();
  if (!(trimmed.startsWith('{') || trimmed.startsWith('['))) {
    return false;
  }
  try {
    const parsed = JSON.parse(value);
    return typeof parsed === 'object' && parsed !== null;
  } catch {
    return false;
  }
}

function collectEmbedded(
  node: Node,
  path: (string | number)[],
  out: EmbeddedJsonField[],
): void {
  if (node.type === 'object' && node.children) {
    for (const prop of node.children) {
      if (prop.type !== 'property' || !prop.children || prop.children.length < 2) {
        continue;
      }
      const keyNode = prop.children[0];
      const valueNode = prop.children[1];
      const key = String(keyNode.value);
      if (valueNode.type === 'string' && typeof valueNode.value === 'string' && looksLikeJson(valueNode.value)) {
        out.push({
          path: [...path, key],
          label: key,
          keyRange: span(keyNode),
          valueRange: span(valueNode),
        });
      } else {
        collectEmbedded(valueNode, [...path, key], out);
      }
    }
  } else if (node.type === 'array' && node.children) {
    node.children.forEach((child, i) => collectEmbedded(child, [...path, i], out));
  }
}

export function findEmbeddedJsonFields(text: string): EmbeddedJsonField[] {
  let tree: Node | undefined;
  try {
    tree = parseTree(text);
  } catch {
    return [];
  }
  if (!tree) {
    return [];
  }
  const out: EmbeddedJsonField[] = [];
  collectEmbedded(tree, [], out);
  return out;
}

export function resolveStringValueSpan(text: string, path: (string | number)[]): WfSpan | undefined {
  let tree: Node | undefined;
  try {
    tree = parseTree(text);
  } catch {
    return undefined;
  }
  if (!tree) {
    return undefined;
  }
  const node = findNodeAtLocation(tree, path);
  if (!node || node.type !== 'string') {
    return undefined;
  }
  return span(node);
}
