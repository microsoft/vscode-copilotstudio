import { parseTree } from 'jsonc-parser';
import type { Node } from 'jsonc-parser';

interface GraphVizNode {
  id: string;
  label: string;
  type: string;
  x: number;
  y: number;
  width: number;
  height: number;
  parentId?: string;
  isContainer: boolean;
  actionOffset?: number;
  actionLength?: number;
}

interface GraphVizEdge {
  id: string;
  source: string;
  target: string;
  label?: string;
  sourceHandle?: string;
  targetHandle?: string;
}

interface WorkflowGraphModel {
  nodes: GraphVizNode[];
  edges: GraphVizEdge[];
  valid: boolean;
}

const DEFAULT_WIDTH = 240;
const DEFAULT_HEIGHT = 66;
const CONTAINER_TYPES = new Set(['loop', 'ifElse', 'switch', 'scope', 'foreach', 'until', 'condition']);

function findProp(objNode: Node | undefined, name: string): Node | undefined {
  if (!objNode || objNode.type !== 'object' || !objNode.children) {
    return undefined;
  }
  for (const prop of objNode.children) {
    if (prop.type === 'property' && prop.children && prop.children.length >= 1) {
      if (prop.children[0].value === name) {
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

function getNumber(objNode: Node | undefined, name: string): number | undefined {
  const v = findProp(objNode, name);
  return v && typeof v.value === 'number' ? v.value : undefined;
}

function buildActionSpanIndex(definition: Node | undefined): Map<string, { offset: number; length: number }> {
  const index = new Map<string, { offset: number; length: number }>();

  const walk = (actionsObj: Node | undefined): void => {
    if (!actionsObj || actionsObj.type !== 'object' || !actionsObj.children) {
      return;
    }
    for (const prop of actionsObj.children) {
      if (prop.type !== 'property' || !prop.children || prop.children.length < 2) {
        continue;
      }
      const valueNode = prop.children[1];
      const metadata = findProp(valueNode, 'metadata');
      const nodeId = getString(metadata, 'nodeId');
      if (nodeId) {
        index.set(nodeId, { offset: prop.offset, length: prop.length });
      }

      walk(findProp(valueNode, 'actions'));
      walk(findProp(findProp(valueNode, 'else'), 'actions'));
      const cases = findProp(valueNode, 'cases');
      if (cases && cases.type === 'object' && cases.children) {
        for (const caseProp of cases.children) {
          if (caseProp.type === 'property' && caseProp.children && caseProp.children.length >= 2) {
            walk(findProp(caseProp.children[1], 'actions'));
          }
        }
      }
      walk(findProp(findProp(valueNode, 'default'), 'actions'));
    }
  };

  walk(findProp(definition, 'actions'));
  return index;
}

interface RawNode extends GraphVizNode {
  localX: number;
  localY: number;
}

export function buildGraphModel(text: string): WorkflowGraphModel {
  let tree: Node | undefined;
  try {
    tree = parseTree(text);
  } catch {
    return { nodes: [], edges: [], valid: false };
  }
  if (!tree) {
    return { nodes: [], edges: [], valid: false };
  }

  const properties = findProp(tree, 'properties');
  const definition = findProp(properties, 'definition');
  const triggers = findProp(definition, 'triggers');
  if (!triggers || triggers.type !== 'object' || !triggers.children) {
    return { nodes: [], edges: [], valid: false };
  }

  const actionSpans = buildActionSpanIndex(definition);

  const rawNodes = new Map<string, RawNode>();
  const edges: GraphVizEdge[] = [];

  for (const triggerProp of triggers.children) {
    if (triggerProp.type !== 'property' || !triggerProp.children || triggerProp.children.length < 2) {
      continue;
    }
    const triggerVal = triggerProp.children[1];
    const metadata = findProp(triggerVal, 'metadata');
    const associatedData = findProp(metadata, 'associatedData');
    const graph = findProp(associatedData, 'graph');

    const nodesArr = findProp(graph, 'nodes');
    if (nodesArr && nodesArr.type === 'array' && nodesArr.children) {
      for (const n of nodesArr.children) {
        const id = getString(n, 'id');
        if (!id) {
          continue;
        }
        const type = getString(n, 'type') ?? '';
        const position = findProp(n, 'position');
        const localX = getNumber(position, 'x') ?? 0;
        const localY = getNumber(position, 'y') ?? 0;
        const measured = findProp(n, 'measured');
        const width = getNumber(n, 'width') ?? getNumber(measured, 'width') ?? DEFAULT_WIDTH;
        const height = getNumber(n, 'height') ?? getNumber(measured, 'height') ?? DEFAULT_HEIGHT;
        const parentId = getString(n, 'parentId');
        const span = actionSpans.get(id);

        rawNodes.set(id, {
          id,
          label: getString(n, 'name') ?? id,
          type,
          x: 0,
          y: 0,
          localX,
          localY,
          width,
          height,
          parentId,
          isContainer: CONTAINER_TYPES.has(type),
          actionOffset: span?.offset,
          actionLength: span?.length,
        });
      }
    }

    const edgesArr = findProp(graph, 'edges');
    if (edgesArr && edgesArr.type === 'array' && edgesArr.children) {
      for (const e of edgesArr.children) {
        const id = getString(e, 'id');
        const source = getString(e, 'source');
        const target = getString(e, 'target');
        if (!source || !target) {
          continue;
        }
        const sourceHandle = getString(e, 'sourceHandle');
        const targetHandle = getString(e, 'targetHandle');
        const label = sourceHandle && sourceHandle !== 'internal-source' && sourceHandle !== 'output'
          && sourceHandle !== 'external-output'
          ? sourceHandle
          : undefined;
        edges.push({ id: id ?? `${source}->${target}`, source, target, label, sourceHandle, targetHandle });
      }
    }
  }

  resolveAbsolutePositions(rawNodes);
  fitContainersToChildren(rawNodes);

  const nodes: GraphVizNode[] = [...rawNodes.values()].map((n) => ({
    id: n.id,
    label: n.label,
    type: n.type,
    x: n.x,
    y: n.y,
    width: n.width,
    height: n.height,
    parentId: n.parentId,
    isContainer: n.isContainer,
    actionOffset: n.actionOffset,
    actionLength: n.actionLength,
  }));

  return { nodes, edges, valid: nodes.length > 0 };
}

function resolveAbsolutePositions(rawNodes: Map<string, RawNode>): void {
  const resolved = new Set<string>();

  const resolve = (node: RawNode): void => {
    if (resolved.has(node.id)) {
      return;
    }
    if (!node.parentId) {
      node.x = node.localX;
      node.y = node.localY;
    } else {
      const parent = rawNodes.get(node.parentId);
      if (!parent) {
        node.x = node.localX;
        node.y = node.localY;
      } else {
        resolve(parent);
        node.x = parent.x + node.localX;
        node.y = parent.y + node.localY;
      }
    }
    resolved.add(node.id);
  };

  for (const node of rawNodes.values()) {
    resolve(node);
  }
}

const CONTAINER_PAD_X = 30;
const CONTAINER_PAD_TOP = 56;
const CONTAINER_PAD_BOTTOM = 30;

function depthOf(node: RawNode, rawNodes: Map<string, RawNode>): number {
  let depth = 0;
  let parentId = node.parentId;
  const seen = new Set<string>();
  while (parentId && !seen.has(parentId)) {
    seen.add(parentId);
    depth += 1;
    parentId = rawNodes.get(parentId)?.parentId;
  }
  return depth;
}

function fitContainersToChildren(rawNodes: Map<string, RawNode>): void {
  const containers = [...rawNodes.values()].filter((n) => n.isContainer);
  containers.sort((a, b) => depthOf(b, rawNodes) - depthOf(a, rawNodes));

  for (const container of containers) {
    let minX = Infinity;
    let minY = Infinity;
    let maxX = -Infinity;
    let maxY = -Infinity;
    let hasChild = false;

    for (const child of rawNodes.values()) {
      if (child.parentId !== container.id) {
        continue;
      }
      hasChild = true;
      minX = Math.min(minX, child.x);
      minY = Math.min(minY, child.y);
      maxX = Math.max(maxX, child.x + child.width);
      maxY = Math.max(maxY, child.y + child.height);
    }

    if (!hasChild) {
      continue;
    }

    container.x = minX - CONTAINER_PAD_X;
    container.y = minY - CONTAINER_PAD_TOP;
    container.width = maxX - minX + CONTAINER_PAD_X * 2;
    container.height = maxY - minY + CONTAINER_PAD_TOP + CONTAINER_PAD_BOTTOM;
  }
}
