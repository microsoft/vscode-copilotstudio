import { parseTree } from 'jsonc-parser';
import type { Node } from 'jsonc-parser';
import type { ConnectorInfo } from '../types';

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
  outcomes?: { id: string; label: string }[];
  iconUri?: string;
  catalogIconUri?: string;
  connectorId?: string;
  iconCategory?: string;
  operationKind?: string;
  functionCategory?: string;
  note?: { text: string; color?: string };
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
  format: 'designer' | 'classic';
  nodes: GraphVizNode[];
  edges: GraphVizEdge[];
  valid: boolean;
  direction: 'horizontal' | 'vertical';
}

const DEFAULT_WIDTH = 240;
const DEFAULT_HEIGHT = 66;
const HORIZONTAL_GAP = 80;
const CLASSIC_CARD_WIDTH = 600;
const CLASSIC_CARD_HEIGHT = 74;
const CLASSIC_VERTICAL_GAP = 46;

function findProp(objNode: Node | undefined, name: string): Node | undefined {
  if (!objNode || objNode.type !== 'object' || !objNode.children) {
    return undefined;
  }
  for (const prop of objNode.children) {
    if (prop.type === 'property' && prop.children && prop.children.length >= 2) {
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

function validateIconUri(iconUri: string | undefined): string | undefined {
  if (!iconUri) {
    return undefined;
  }
  try {
    const url = new URL(iconUri);
    return url.protocol === 'https:' && !url.username && !url.password ? url.href : undefined;
  } catch {
    return undefined;
  }
}

const CLASSIC_CONNECTOR_TYPES = new Set([
  'ApiApp', 'ApiConnection', 'OpenApiConnection', 'OpenApiConnectionWebhook',
  'ApiConnectionWebhook', 'ApiManagement', 'ApiConnectionNotification',
]);

function connectorCatalogKey(id: string | undefined): string | undefined {
  return id?.match(/^(?:\/providers\/Microsoft\.PowerApps\/apis\/)?([\w-]+)$/i)?.[1].toLowerCase();
}

function resolveConnectorId(
  apiId: string | undefined,
  connectionName: string | undefined,
  connectionReferences: Node | undefined,
): string | undefined {
  const reference = connectionName ? findProp(connectionReferences, connectionName) : undefined;
  return connectorCatalogKey(apiId ?? getString(findProp(reference, 'api'), 'name'));
}

function buildConnectorIconMap(connectors: readonly Pick<ConnectorInfo, 'internalId' | 'iconUri'>[]): Map<string, string> {
  const icons = new Map<string, string>();
  for (const connector of connectors) {
    const key = connectorCatalogKey(connector.internalId);
    const iconUri = validateIconUri(connector.iconUri);
    if (key && iconUri) {
      icons.set(key, iconUri);
    }
  }
  return icons;
}

function buildActionSpanIndex(definition: Node | undefined): Map<string, { offset: number; length: number; label: string }> {
  const index = new Map<string, { offset: number; length: number; label: string }>();

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
        index.set(nodeId, {
          offset: prop.offset,
          length: prop.length,
          label: getString(metadata, 'description') || humanizeName(String(prop.children[0].value)),
        });
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

function objectProperties(objNode: Node | undefined): Node[] {
  return objNode && objNode.type === 'object' && objNode.children
    ? objNode.children.filter((child) => child.type === 'property' && child.children && child.children.length >= 2)
    : [];
}

function humanizeName(name: string): string {
  return name.replace(/_/g, ' ');
}

function buildClassicGraphModel(
  definition: Node | undefined,
  connectionReferences: Node | undefined,
  connectorIcons: ReadonlyMap<string, string>,
): WorkflowGraphModel {
  const rawNodes = new Map<string, RawNode>();
  const edges: GraphVizEdge[] = [];
  const order = new Map<string, number>();
  let nextOrder = 0;

  const addNode = (
    id: string,
    label: string,
    type: string,
    sourceNode?: Node,
    isContainer = false,
    parentId?: string,
    branch?: string,
  ): void => {
    if (rawNodes.has(id)) {
      return;
    }
    const operation = sourceNode?.type === 'property' ? sourceNode.children?.[1] : sourceNode;
    const host = findProp(findProp(operation, 'inputs'), 'host');
    const connectionName = getString(host, 'connectionName')
      ?? getString(findProp(host, 'connection'), 'referenceName');
    const connectorKey = CLASSIC_CONNECTOR_TYPES.has(type)
      ? resolveConnectorId(getString(host, 'apiId'), connectionName, connectionReferences) : undefined;
    rawNodes.set(id, {
      id,
      label,
      type,
      x: 0,
      y: 0,
      localX: 0,
      localY: 0,
      width: CLASSIC_CARD_WIDTH,
      height: CLASSIC_CARD_HEIGHT,
      parentId,
      isContainer,
      actionOffset: sourceNode?.offset,
      actionLength: sourceNode?.length,
      branch,
      operationKind: getString(operation, 'kind'),
      connectorId: connectorKey,
      catalogIconUri: connectorKey ? connectorIcons.get(connectorKey) : undefined,
    });
    order.set(id, nextOrder++);
  };

  const triggerIds: string[] = [];
  for (const triggerProp of objectProperties(findProp(definition, 'triggers'))) {
    const triggerName = String(triggerProp.children![0].value);
    const triggerValue = triggerProp.children![1];
    const triggerId = `trigger:${triggerName}`;
    const triggerType = getString(triggerValue, 'type') ?? 'Manual';
    const triggerKind = getString(triggerValue, 'kind');
    let label = humanizeName(triggerName);
    if (triggerName === 'manual' && triggerType === 'Request') {
      label = triggerKind === 'Skills' ? 'When an agent calls the flow'
        : triggerKind?.toLowerCase() === 'http' || !triggerKind ? 'When an HTTP request is received' : label;
    }
    triggerIds.push(triggerId);
    addNode(triggerId, label, triggerType, triggerProp);
  }

  const collectActions = (actionsObj: Node | undefined, scopePrefix: string, parentActionId?: string, branch?: string): void => {
    const props = objectProperties(actionsObj);
    const localIds = new Map<string, string>();
    for (const prop of props) {
      const actionName = String(prop.children![0].value);
      localIds.set(actionName, `${scopePrefix}${actionName}`);
    }

    for (const prop of props) {
      const actionName = String(prop.children![0].value);
      const actionValue = prop.children![1];
      const id = localIds.get(actionName)!;
      const actionType = getString(actionValue, 'type') ?? 'action';
      if (actionType === 'Switch') {
        const branchBlockId = `${id}/branches`;
        addNode(branchBlockId, actionName, 'SwitchBlock', actionValue, true, parentActionId, branch);
        addNode(id, humanizeName(actionName), actionType, prop, false, branchBlockId, 'switch');
      } else {
        addNode(id, humanizeName(actionName), actionType, prop, isClassicContainerType(actionType), parentActionId, branch);
      }
    }

    for (const prop of props) {
      const actionName = String(prop.children![0].value);
      const actionValue = prop.children![1];
      const id = localIds.get(actionName)!;
      const actionType = getString(actionValue, 'type') ?? 'action';

      const runAfter = findProp(actionValue, 'runAfter');
      const dependencies = objectProperties(runAfter);
      const targetId = rawNodes.has(`${id}/branches`) ? `${id}/branches` : id;
      if (dependencies.length === 0) {
        if (parentActionId) {
          edges.push({
            id: `${parentActionId}->${targetId}`,
            source: parentActionId,
            target: targetId,
            label: branch,
            sourceHandle: 'internal-source',
          });
        } else {
          for (const triggerId of triggerIds) {
            edges.push({ id: `${triggerId}->${targetId}`, source: triggerId, target: targetId });
          }
        }
      }

      for (const dep of dependencies) {
        const dependencyName = String(dep.children![0].value);
        const dependencyId = localIds.get(dependencyName) ?? dependencyName;
        const source = rawNodes.has(`${dependencyId}/branches`) ? `${dependencyId}/branches` : dependencyId;
        if (!rawNodes.has(source)) {
          continue;
        }
        const statusValues = dep.children![1].type === 'array' && dep.children![1].children
          ? dep.children![1].children.map((status) => String(status.value))
          : [];
        const label = statusValues.length > 0 && statusValues.some((status) => status.toUpperCase() !== 'SUCCEEDED')
          ? statusValues.join(', ')
          : undefined;
        edges.push({ id: `${source}->${targetId}`, source, target: targetId, label });
      }

      const childScopePrefix = `${id}/`;
      if (actionType === 'Switch') {
        const branchBlockId = `${id}/branches`;

        for (const caseProp of objectProperties(findProp(actionValue, 'cases'))) {
          const caseName = String(caseProp.children![0].value);
          const caseValue = caseProp.children![1];
          const caseId = `${id}/case:${caseName}`;
          addNode(caseId, caseName, 'Case', caseValue, true, branchBlockId, 'case');
          edges.push({
            id: `${id}->${caseId}`,
            source: id,
            target: caseId,
            label: getString(caseValue, 'case'),
          });
          collectActions(findProp(caseValue, 'actions'), `${caseId}/`, caseId, caseName);
        }

        const defaultValue = findProp(actionValue, 'default');
        if (defaultValue) {
          const defaultId = `${id}/default`;
          addNode(defaultId, 'default', 'Default', defaultValue, true, branchBlockId, 'default');
          edges.push({
            id: `${id}->${defaultId}`,
            source: id,
            target: defaultId,
            label: 'default',
          });
          collectActions(findProp(defaultValue, 'actions'), `${defaultId}/`, defaultId, 'default');
        }
        continue;
      }

      if (actionType === 'If') {
        const trueActions = findProp(actionValue, 'actions');
        if (trueActions) {
          const trueId = `${id}/true`;
          addNode(trueId, 'true', 'Branch', trueActions, true, id, 'true');
          edges.push({ id: `${id}->${trueId}`, source: id, target: trueId, label: 'true', sourceHandle: 'internal-source' });
          collectActions(trueActions, `${trueId}/`, trueId, 'true');
        }

        const falseActions = findProp(findProp(actionValue, 'else'), 'actions');
        if (falseActions) {
          const falseId = `${id}/false`;
          addNode(falseId, 'false', 'Branch', falseActions, true, id, 'false');
          edges.push({ id: `${id}->${falseId}`, source: id, target: falseId, label: 'false', sourceHandle: 'internal-source' });
          collectActions(falseActions, `${falseId}/`, falseId, 'false');
        }
        continue;
      }

      collectActions(findProp(actionValue, 'actions'), childScopePrefix, id, actionType === 'If' ? 'true' : undefined);
      collectActions(findProp(findProp(actionValue, 'else'), 'actions'), `${id}/else/`, id, 'false');

      for (const caseProp of objectProperties(findProp(actionValue, 'cases'))) {
        const caseName = String(caseProp.children![0].value);
        collectActions(findProp(caseProp.children![1], 'actions'), `${id}/case:${caseName}/`, id, `case ${caseName}`);
      }
      collectActions(findProp(findProp(actionValue, 'default'), 'actions'), `${id}/default/`, id, 'default');
    }
  };

  collectActions(findProp(definition, 'actions'), '');
  layoutClassicNodes(rawNodes, edges, triggerIds, order);

  const nodes: GraphVizNode[] = [...rawNodes.values()].map((node) => ({
    id: node.id,
    label: node.label,
    type: node.type,
    x: node.x,
    y: node.y,
    width: node.width,
    height: node.height,
    parentId: node.parentId,
    isContainer: node.isContainer,
    operationKind: node.operationKind,
    connectorId: node.connectorId,
    catalogIconUri: node.catalogIconUri,
    actionOffset: node.actionOffset,
    actionLength: node.actionLength,
  }));

  return { nodes, edges, valid: nodes.length > 0, direction: 'vertical', format: 'classic' };
}

function isClassicContainerType(actionType: string): boolean {
  return actionType === 'If' || actionType === 'Foreach' || actionType === 'Until' || actionType === 'Scope';
}

function layoutClassicNodes(
  rawNodes: Map<string, RawNode>,
  edges: GraphVizEdge[],
  triggerIds: string[],
  order: Map<string, number>,
): void {
  const incoming = new Map<string, string[]>();
  const rootNodeIds = new Set<string>();
  const branchTargetsBySource = new Map<string, string[]>();
  for (const [nodeId, node] of rawNodes) {
    if (!node.parentId) {
      rootNodeIds.add(nodeId);
      incoming.set(nodeId, []);
    }
  }
  for (const edge of edges) {
    if (rootNodeIds.has(edge.source) && rootNodeIds.has(edge.target)) {
      incoming.get(edge.target)?.push(edge.source);
      const target = rawNodes.get(edge.target);
      if (target?.branch === 'case' || target?.branch === 'default') {
        const targets = branchTargetsBySource.get(edge.source) ?? [];
        targets.push(edge.target);
        branchTargetsBySource.set(edge.source, targets);
      }
    }
  }

  const levels = new Map<string, number>();
  const visiting = new Set<string>();
  const completionLevels = new Map<string, number>();
  const levelOf = (nodeId: string): number => {
    const existing = levels.get(nodeId);
    if (existing !== undefined) {
      return existing;
    }
    if (visiting.has(nodeId)) {
      return 0;
    }
    visiting.add(nodeId);
    const dependencies = incoming.get(nodeId) ?? [];
    const level = dependencies.length === 0
      ? (triggerIds.includes(nodeId) ? 0 : 1)
      : Math.max(...dependencies.map((dependencyId) => dependencyLevelFor(nodeId, dependencyId))) + 1;
    visiting.delete(nodeId);
    levels.set(nodeId, level);
    return level;
  };

  const completionLevelOf = (nodeId: string): number => {
    const existing = completionLevels.get(nodeId);
    if (existing !== undefined) {
      return existing;
    }
    const branchTargets = branchTargetsBySource.get(nodeId) ?? [];
    const level = Math.max(levelOf(nodeId), ...branchTargets.map((targetId) => completionLevelOf(targetId)));
    completionLevels.set(nodeId, level);
    return level;
  };

  const dependencyLevelFor = (targetId: string, dependencyId: string): number => {
    const target = rawNodes.get(targetId);
    if (target?.branch === 'case' || target?.branch === 'default') {
      return levelOf(dependencyId);
    }
    return completionLevelOf(dependencyId);
  };

  for (const nodeId of rootNodeIds) {
    levelOf(nodeId);
  }

  const byLevel = new Map<number, RawNode[]>();
  for (const node of rawNodes.values()) {
    if (node.parentId) {
      continue;
    }
    const level = levels.get(node.id) ?? 0;
    const nodesAtLevel = byLevel.get(level) ?? [];
    nodesAtLevel.push(node);
    byLevel.set(level, nodesAtLevel);
  }

  for (const [level, nodesAtLevel] of byLevel) {
    nodesAtLevel.sort((a, b) => (order.get(a.id) ?? 0) - (order.get(b.id) ?? 0));
    const rowWidth = nodesAtLevel.length * CLASSIC_CARD_WIDTH + Math.max(0, nodesAtLevel.length - 1) * HORIZONTAL_GAP;
    nodesAtLevel.forEach((node, index) => {
      node.x = index * (CLASSIC_CARD_WIDTH + HORIZONTAL_GAP) - rowWidth / 2;
      node.y = level * (CLASSIC_CARD_HEIGHT + CLASSIC_VERTICAL_GAP);
      node.localX = node.x;
      node.localY = node.y;
    });
  }

  for (const node of [...rawNodes.values()].filter((n) => !n.parentId && n.isContainer)) {
    layoutClassicContainerChildren(node, rawNodes, edges, order);
  }

  fitContainersToChildren(rawNodes);
  spreadClassicLevels(rawNodes, byLevel);
}

function layoutClassicContainerChildren(
  container: RawNode,
  rawNodes: Map<string, RawNode>,
  edges: GraphVizEdge[],
  order: Map<string, number>,
): void {
  const children = [...rawNodes.values()]
    .filter((node) => node.parentId === container.id)
    .sort((a, b) => (order.get(a.id) ?? 0) - (order.get(b.id) ?? 0));
  if (children.length === 0) {
    return;
  }

  if (container.type === 'If') {
    const left = children.filter((child) => child.branch !== 'false');
    const right = children.filter((child) => child.branch === 'false');
    const leftX = container.x - CLASSIC_CARD_WIDTH - HORIZONTAL_GAP / 2;
    const rightX = container.x + CLASSIC_CARD_WIDTH + HORIZONTAL_GAP / 2;
    const startY = container.y + CONTAINER_PAD_TOP;
    layoutClassicColumn(left, leftX, startY, rawNodes, edges, order);
    layoutClassicColumn(right, rightX, startY, rawNodes, edges, order);
    fitContainerToChildren(container, rawNodes);
    return;
  }

  if (container.type === 'SwitchBlock') {
    const switchCards = children.filter((child) => child.branch === 'switch');
    const branches = children.filter((child) => child.branch !== 'switch');
    const startY = container.y + CONTAINER_PAD_TOP;
    layoutClassicColumn(switchCards, container.x, startY, rawNodes, edges, order);
    const branchY = startY + (switchCards[0]?.height ?? CLASSIC_CARD_HEIGHT) + CLASSIC_VERTICAL_GAP;
    layoutClassicRow(branches, container.x, branchY, rawNodes, edges, order);
    fitContainerToChildren(container, rawNodes);
    return;
  }

  layoutClassicColumn(children, container.x, container.y + CONTAINER_PAD_TOP, rawNodes, edges, order);
  fitContainerToChildren(container, rawNodes);
}

function layoutClassicRow(
  children: RawNode[],
  startX: number,
  y: number,
  rawNodes: Map<string, RawNode>,
  edges: GraphVizEdge[],
  order: Map<string, number>,
): void {
  let x = startX;
  for (const child of orderClassicChildren(children, edges, order)) {
    const desiredX = x;
    child.x = x;
    child.y = y;
    child.localX = child.x;
    child.localY = child.y;
    if (child.isContainer) {
      layoutClassicContainerChildren(child, rawNodes, edges, order);
      shiftNodeAndChildren(child, rawNodes, desiredX - child.x, y - child.y);
    }
    x = child.x + child.width + HORIZONTAL_GAP;
  }
}

function layoutClassicColumn(
  children: RawNode[],
  x: number,
  startY: number,
  rawNodes: Map<string, RawNode>,
  edges: GraphVizEdge[],
  order: Map<string, number>,
): void {
  let y = startY;
  for (const child of orderClassicChildren(children, edges, order)) {
    const desiredY = y;
    child.x = x;
    child.y = y;
    child.localX = child.x;
    child.localY = child.y;
    if (child.isContainer) {
      layoutClassicContainerChildren(child, rawNodes, edges, order);
      shiftNodeAndChildren(child, rawNodes, x - child.x, desiredY - child.y);
    }
    y = child.y + child.height + CLASSIC_VERTICAL_GAP;
  }
}

function orderClassicChildren(children: RawNode[], edges: GraphVizEdge[], order: Map<string, number>): RawNode[] {
  const childIds = new Set(children.map((child) => child.id));
  const incoming = new Map<string, string[]>();
  for (const child of children) {
    incoming.set(child.id, []);
  }
  for (const edge of edges) {
    if (childIds.has(edge.source) && childIds.has(edge.target)) {
      incoming.get(edge.target)?.push(edge.source);
    }
  }

  const ranks = new Map<string, number>();
  const visiting = new Set<string>();
  const rankOf = (nodeId: string): number => {
    const existing = ranks.get(nodeId);
    if (existing !== undefined) {
      return existing;
    }
    if (visiting.has(nodeId)) {
      return 0;
    }
    visiting.add(nodeId);
    const dependencies = incoming.get(nodeId) ?? [];
    const rank = dependencies.length === 0 ? 0 : Math.max(...dependencies.map(rankOf)) + 1;
    visiting.delete(nodeId);
    ranks.set(nodeId, rank);
    return rank;
  };

  return [...children].sort((a, b) => {
    const rankDelta = rankOf(a.id) - rankOf(b.id);
    return rankDelta !== 0 ? rankDelta : (order.get(a.id) ?? 0) - (order.get(b.id) ?? 0);
  });
}

function spreadClassicLevels(rawNodes: Map<string, RawNode>, byLevel: Map<number, RawNode[]>): void {
  const levels = [...byLevel.keys()].sort((a, b) => a - b);
  let y = 0;
  for (const level of levels) {
    const nodesAtLevel = byLevel.get(level) ?? [];
    const rowWidth = nodesAtLevel.reduce((total, node) => total + node.width, 0)
      + Math.max(0, nodesAtLevel.length - 1) * HORIZONTAL_GAP;
    let x = -rowWidth / 2;
    for (const node of nodesAtLevel) {
      const deltaX = x - node.x;
      shiftNodeAndChildren(node, rawNodes, deltaX, 0);
      x += node.width + HORIZONTAL_GAP;
    }

    const minY = Math.min(...nodesAtLevel.map((node) => node.y));
    const deltaY = Number.isFinite(minY) ? y - minY : 0;
    for (const node of nodesAtLevel) {
      shiftNodeAndChildren(node, rawNodes, 0, deltaY);
    }
    const maxHeight = Math.max(...nodesAtLevel.map((node) => node.height), CLASSIC_CARD_HEIGHT);
    y += maxHeight + CLASSIC_VERTICAL_GAP;
  }
}

function shiftNodeAndChildren(node: RawNode, rawNodes: Map<string, RawNode>, deltaX: number, deltaY: number): void {
  node.x += deltaX;
  node.y += deltaY;
  node.localX = node.x;
  node.localY = node.y;
  for (const child of rawNodes.values()) {
    if (child.parentId === node.id) {
      shiftNodeAndChildren(child, rawNodes, deltaX, deltaY);
    }
  }
}

interface RawNode extends GraphVizNode {
  localX: number;
  localY: number;
  branch?: string;
}

export function buildGraphModel(
  text: string,
  connectors: readonly Pick<ConnectorInfo, 'internalId' | 'iconUri'>[] = [],
): WorkflowGraphModel {
  let tree: Node | undefined;
  try {
    tree = parseTree(text);
  } catch {
    return { nodes: [], edges: [], valid: false, direction: 'horizontal', format: 'designer' };
  }
  if (!tree) {
    return { nodes: [], edges: [], valid: false, direction: 'horizontal', format: 'designer' };
  }

  const properties = findProp(tree, 'properties');
  const definition = findProp(properties, 'definition');
  const triggers = findProp(definition, 'triggers');
  if (!triggers || triggers.type !== 'object' || !triggers.children) {
    return { nodes: [], edges: [], valid: false, direction: 'horizontal', format: 'designer' };
  }

  const actionSpans = buildActionSpanIndex(definition);
  const connectionReferences = findProp(properties, 'connectionReferences');
  const connectorIcons = buildConnectorIconMap(connectors);

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
        const data = findProp(n, 'data');
        const config = findProp(data, 'config');
        const connectorId = type === 'connector'
          ? resolveConnectorId(getString(config, 'apiName'), getString(config, 'connectionName'), connectionReferences)
          : undefined;
        const outcomes: { id: string; label: string }[] = [];
        for (const outcome of findProp(data, 'outcomes')?.children ?? []) {
          const outcomeId = getString(outcome, 'id');
          const label = getString(outcome, 'label');
          if (outcomeId && label) {
            outcomes.push({ id: outcomeId, label });
          }
        }
        const defaultHeight = outcomes.length > 1
          ? 68 + outcomes.length * 42
          : DEFAULT_HEIGHT;
        const position = findProp(n, 'position');
        const localX = getNumber(position, 'x') ?? 0;
        const localY = getNumber(position, 'y') ?? 0;
        const measured = findProp(n, 'measured');
        const width = getNumber(n, 'width') ?? getNumber(measured, 'width') ?? DEFAULT_WIDTH;
        const height = getNumber(n, 'height') ?? getNumber(measured, 'height') ?? defaultHeight;
        const parentId = getString(n, 'parentId');
        const span = actionSpans.get(id);

        rawNodes.set(id, {
          id,
          label: getString(n, 'name') || getString(n, 'label') || getString(config, 'displayName')
            || span?.label || humanizeName(type) || 'Action',
          type,
          x: 0,
          y: 0,
          localX: Number.isFinite(localX) ? localX : 0,
          localY: Number.isFinite(localY) ? localY : 0,
          width: width > 0 && Number.isFinite(width) ? width : DEFAULT_WIDTH,
          height: height > 0 && Number.isFinite(height) ? height : defaultHeight,
          parentId,
          isContainer: false,
          outcomes: outcomes.length > 0 ? outcomes : undefined,
          iconUri: validateIconUri(getString(config, 'iconUri')),
          catalogIconUri: connectorId ? connectorIcons.get(connectorId) : undefined,
          connectorId,
          iconCategory: getString(config, 'paletteIconHint'),
          functionCategory: type === 'builtinFunction' ? getString(config, 'category') : undefined,
          note: type === 'canvasNote' ? {
            text: getString(config, 'markdown') ?? '',
            color: getString(config, 'color'),
          } : undefined,
          actionOffset: span?.offset ?? (type === 'start' ? triggerProp.offset : undefined),
          actionLength: span?.length ?? (type === 'start' ? triggerProp.length : undefined),
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
          && !/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i.test(sourceHandle)
          ? sourceHandle
          : undefined;
        edges.push({ id: id ?? `${source}->${target}`, source, target, label: getString(e, 'label') || label, sourceHandle, targetHandle });
      }
    }
  }

  if (rawNodes.size === 0) {
    return buildClassicGraphModel(definition, connectionReferences, connectorIcons);
  }

  for (const node of rawNodes.values()) {
    const seen = new Set([node.id]);
    let parentId = node.parentId;
    while (parentId) {
      if (seen.has(parentId) || !rawNodes.has(parentId)) {
        node.parentId = undefined;
        break;
      }
      seen.add(parentId);
      parentId = rawNodes.get(parentId)?.parentId;
    }
  }
  const parents = new Set([...rawNodes.values()].map((node) => node.parentId));
  for (const node of rawNodes.values()) {
    node.isContainer = parents.has(node.id);
  }
  resolveAbsolutePositions(rawNodes);
  fitContainersToChildren(rawNodes, true);

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
    outcomes: n.outcomes,
    iconUri: n.iconUri,
    catalogIconUri: n.catalogIconUri,
    connectorId: n.connectorId,
    iconCategory: n.iconCategory,
    functionCategory: n.functionCategory,
    note: n.note,
    actionOffset: n.actionOffset,
    actionLength: n.actionLength,
  }));

  return { nodes, edges, valid: nodes.length > 0, direction: 'horizontal', format: 'designer' };
}

function resolveAbsolutePositions(rawNodes: Map<string, RawNode>): void {
  const resolved = new Set<string>();
  const visiting = new Set<string>();

  const resolve = (node: RawNode): void => {
    if (resolved.has(node.id)) {
      return;
    }
    if (visiting.has(node.id)) {
      node.x = node.localX;
      node.y = node.localY;
      resolved.add(node.id);
      return;
    }
    visiting.add(node.id);
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
    visiting.delete(node.id);
    resolved.add(node.id);
  };

  for (const node of rawNodes.values()) {
    resolve(node);
  }
}

const CONTAINER_PAD_X = 30;
const CONTAINER_PAD_TOP = 56;
const CONTAINER_PAD_BOTTOM = 30;
const MIN_CONTAINER_GUTTER = 12;

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

function fitContainersToChildren(rawNodes: Map<string, RawNode>, preserveSavedBounds = false): void {
  const nodes = [...rawNodes.values()];
  const containers = nodes.filter((n) => n.isContainer);
  const overlapping = new Set(containers.filter((container) => preserveSavedBounds && nodes.some((sibling) =>
    sibling.id !== container.id && sibling.parentId === container.parentId && sibling.type !== 'canvasNote'
    && sibling.x < container.x + container.width && sibling.x + sibling.width > container.x
    && sibling.y < container.y + container.height && sibling.y + sibling.height > container.y)));
  containers.sort((a, b) => depthOf(b, rawNodes) - depthOf(a, rawNodes));

  for (const container of containers) {
    fitContainerToChildren(container, rawNodes, preserveSavedBounds && !overlapping.has(container));
  }
}

function fitContainerToChildren(container: RawNode, rawNodes: Map<string, RawNode>, preserveSavedBounds = false): void {
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
    return;
  }

  const savedRight = container.x + container.width;
  const savedBottom = container.y + container.height;
  const right = preserveSavedBounds && savedRight >= maxX + MIN_CONTAINER_GUTTER
    ? savedRight : Math.max(maxX + CONTAINER_PAD_X, preserveSavedBounds ? savedRight : -Infinity);
  const bottom = preserveSavedBounds && savedBottom >= maxY + MIN_CONTAINER_GUTTER
    ? savedBottom : Math.max(maxY + CONTAINER_PAD_BOTTOM, preserveSavedBounds ? savedBottom : -Infinity);
  container.x = preserveSavedBounds && container.x <= minX - MIN_CONTAINER_GUTTER
    ? container.x : Math.min(minX - CONTAINER_PAD_X, preserveSavedBounds ? container.x : Infinity);
  container.y = Math.min(minY - CONTAINER_PAD_TOP, preserveSavedBounds ? container.y : Infinity);
  container.width = right - container.x;
  container.height = bottom - container.y;
}
