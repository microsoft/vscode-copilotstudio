// @ts-nocheck
(function () {
  const vscode = acquireVsCodeApi();
  const SVG_NS = 'http://www.w3.org/2000/svg';
  const PAN_THRESHOLD = 4;
  const surface = document.getElementById('surface');
  const canvas = document.getElementById('canvas');
  const banner = document.getElementById('banner');
  const DESIGNER_NODE_ICONS = {
    agent: 'agent',
    agentExtract: 'extract',
    builtinFunction: 'function',
    canvasNote: 'note',
    classifyOnInlineAgent: 'classify',
    connector: 'connector',
    ifElse: 'if-else',
    loop: 'loop',
    m365Copilot: null,
    start: 'start',
    variable: 'variable',
  };
  const DESIGNER_PALETTE_ICONS = {
    humanReview: 'human-review',
  };
  const DESIGNER_FUNCTION_ICONS = {
    'providers/Microsoft.ProcessSimple/operationGroups/DataOperation': 'function-data-operation',
    'providers/Microsoft.ProcessSimple/operationGroups/Datetime': 'function-datetime',
    'providers/Microsoft.ProcessSimple/operationGroups/Http': 'function-http',
    'providers/Microsoft.ProcessSimple/operationGroups/Request': 'function-request',
    'providers/Microsoft.ProcessSimple/operationGroups/Schedule': 'function-datetime',
  };
  // https://learn.microsoft.com/dotnet/api/microsoft.powerplatform.management.models.flowactiontype
  const CLASSIC_OPERATION_ICONS = {
    ApiApp: null,
    ApiConnection: null,
    ApiConnectionNotification: null,
    ApiConnectionWebhook: null,
    ApiManagement: null,
    AppendToArrayVariable: 'variable-operation',
    AppendToStringVariable: 'variable-operation',
    As2Decode: null,
    As2Encode: null,
    Batch: null,
    Changeset: null,
    Compose: 'compose',
    DecrementVariable: 'variable-operation',
    Expression: 'function',
    FlatFileDecoding: 'function',
    FlatFileEncoding: 'function',
    Flow: 'workflow',
    Foreach: 'foreach',
    Function: 'azure-function',
    Http: 'http',
    HttpWebhook: 'webhook',
    If: 'condition',
    IncrementVariable: 'variable-operation',
    InitializeVariable: 'variable-operation',
    IntegrationAccountArtifactLookup: 'function',
    // WDL uses JavaScriptCode; FlowActionType uses JavascriptCode.
    JavaScriptCode: 'function',
    JavascriptCode: 'function',
    Join: 'function',
    Liquid: 'function',
    Manual: null,
    OpenApiConnection: null,
    OpenApiConnectionWebhook: null,
    ParseJson: 'function',
    Query: 'query',
    Recurrence: 'recurrence',
    Request: 'agent-flow',
    Response: 'agent-flow',
    RosettaNetDecode: null,
    RosettaNetEncode: null,
    RosettaNetWaitForResponse: null,
    Scope: 'scope',
    Select: 'function',
    SendToBatch: 'function',
    SetVariable: 'variable-operation',
    SlidingWindow: null,
    SwiftEncode: null,
    Switch: 'switch',
    Table: 'function',
    Terminate: 'terminate',
    Until: 'until',
    Wait: 'wait',
    Workflow: 'workflow',
    XmlValidation: 'function',
    Xslt: 'function',
  };
  const nodeElements = new Map();
  let model = { nodes: [], edges: [], valid: false };
  let nodeById = new Map();
  let selectedId = null;
  let view = { x: 0, y: 0, scale: 1 };
  let root;
  let panStart = null;
  let suppressClick = false;

  function el(name, attrs, parent) {
    const node = document.createElementNS(SVG_NS, name);
    for (const [key, value] of Object.entries(attrs || {})) {
      node.setAttribute(key, String(value));
    }
    parent.appendChild(node);
    return node;
  }

  function applyView() {
    if (root) {
      root.setAttribute('transform', `translate(${view.x} ${view.y}) scale(${view.scale})`);
    }
    const gridSpacing = Math.max(4, 20 * view.scale);
    surface.style.backgroundSize = `${gridSpacing}px ${gridSpacing}px`;
    surface.style.backgroundPosition = `${view.x}px ${view.y}px`;
    document.getElementById('btn-reset').textContent = `${Math.round(view.scale * 100)}%`;
  }

  function truncate(text, length) {
    return text.length > length ? `${text.slice(0, Math.max(1, length - 3))}...` : text;
  }

  function branchRows(node) {
    if (node.isContainer || !node.outcomes || node.outcomes.length < 2) {
      return [];
    }
    const top = model.format === 'designer'
      ? Math.min(64, node.height * 0.4) : Math.min(52, node.height * 0.35);
    const gap = Math.min(8, node.height * 0.04);
    const height = (node.height - top - 12 - gap * (node.outcomes.length - 1)) / node.outcomes.length;
    return node.outcomes.map((outcome, index) => ({
      ...outcome, y: node.y + top + index * (height + gap), height,
    }));
  }

  function edgePoints(source, target, edge) {
    const sx = source.x + source.width / 2;
    const tx = target.x + target.width / 2;
    if (edge.sourceHandle === 'internal-source' && source.isContainer) {
      if (model.direction === 'horizontal') {
        const row = branchRows(target)[0];
        const sy = source.y + source.height / 2;
        const ty = row ? row.y + row.height / 2 : target.y + target.height / 2;
        const bend = (target.x - source.x) / 2;
        return [[source.x, sy], [source.x + bend, sy], [target.x - bend, ty], [target.x, ty]];
      }
      return [[tx, source.y + 40], [tx, target.y]];
    }
    if (edge.targetHandle === 'internal-target' && target.isContainer) {
      if (model.direction === 'horizontal') {
        const sy = source.y + source.height / 2;
        const ty = target.y + target.height / 2;
        const start = source.x + source.width;
        const end = target.x + target.width;
        const bend = (end - start) / 2;
        return [[start, sy], [start + bend, sy], [end - bend, ty], [end, ty]];
      }
      return [[sx, source.y + source.height], [sx, target.y + target.height]];
    }
    if (model.direction === 'horizontal') {
      const sourceRow = branchRows(source).find(row => row.id === edge.sourceHandle);
      const targetRow = branchRows(target)[0];
      const sy = sourceRow ? sourceRow.y + sourceRow.height / 2 : source.y + source.height / 2;
      const ty = targetRow ? targetRow.y + targetRow.height / 2 : target.y + target.height / 2;
      const start = source.x + source.width;
      const end = target.x;
      const bend = Math.max(32, Math.abs(end - start) / 2);
      return [[start, sy], [start + bend, sy], [end - bend, ty], [end, ty]];
    }
    if (target.y >= source.y + source.height + 24) {
      const y = (source.y + source.height + target.y) / 2;
      return [[sx, source.y + source.height], [sx, y], [tx, y], [tx, target.y]];
    }
    if (target.x >= source.x + source.width + 24) {
      const x = (source.x + source.width + target.x) / 2;
      const sy = source.y + source.height / 2;
      const ty = target.y + target.height / 2;
      return [[source.x + source.width, sy], [x, sy], [x, ty], [target.x, ty]];
    }
    const x = Math.max(source.x + source.width, target.x + target.width) + 40;
    const sy = source.y + source.height / 2;
    const ty = target.y + target.height / 2;
    return [[source.x + source.width, sy], [x, sy], [x, ty], [target.x + target.width, ty]];
  }

  function drawIcon(node, group, y, defs) {
    const isClassic = model.format === 'classic';
    const size = isClassic ? 40 : 20;
    const icon = el('g', {
      class: 'wf-node-icon', transform: `translate(${node.x + (isClassic ? 16 : 18)} ${y - size / 2})`, 'aria-hidden': 'true',
    }, group);
    if (isClassic) {
      const clipId = `icon-clip-${nodeElements.size}`;
      const clip = el('clipPath', { id: clipId }, defs);
      el('rect', { width: size, height: size, rx: 8 }, clip);
      icon.setAttribute('clip-path', `url(#${clipId})`);
    }
    const fallback = el('g', { class: 'wf-node-icon-fallback' }, icon);
    const icons = isClassic ? CLASSIC_OPERATION_ICONS : DESIGNER_NODE_ICONS;
    const category = isClassic && (node.type === 'Request' || node.type === 'Response')
      && node.operationKind?.toLowerCase() === 'http'
      ? 'response' : !isClassic && node.type !== 'm365Copilot'
      && Object.hasOwn(DESIGNER_PALETTE_ICONS, node.iconCategory)
      ? DESIGNER_PALETTE_ICONS[node.iconCategory]
      : Object.hasOwn(icons, node.type) ? icons[node.type] : undefined;
    let categoryImage;
    if (category) {
      const image = categoryImage = el('image', {
        class: 'wf-category-icon', width: size, height: size, preserveAspectRatio: 'xMidYMid meet',
        'data-category': category,
      }, fallback);
      image.addEventListener('error', () => {
        image.remove();
        group.querySelector('title').textContent += '\nCategory icon unavailable.';
      }, { once: true });
      image.setAttribute('href', `${surface.getAttribute('data-icon-base')}/${category}.svg`);
    }
    if (model.format === 'designer' && node.type === 'builtinFunction'
      && Object.hasOwn(DESIGNER_FUNCTION_ICONS, node.functionCategory)) {
      const subcategory = DESIGNER_FUNCTION_ICONS[node.functionCategory];
      const image = el('image', {
        class: 'wf-subcategory-icon', width: size, height: size, preserveAspectRatio: 'xMidYMid meet',
        'data-subcategory': subcategory,
      }, fallback);
      image.addEventListener('load', () => categoryImage?.classList.add('hidden'));
      image.addEventListener('error', () => {
        image.remove();
        categoryImage?.classList.remove('hidden');
        group.querySelector('title').textContent += '\nFunction subcategory icon unavailable.';
      }, { once: true });
      image.setAttribute('href', `${surface.getAttribute('data-icon-base')}/${subcategory}.svg`);
    }
    const remoteIcons = [
      { uri: node.iconUri, className: 'wf-metadata-icon', error: 'Step icon unavailable.' },
      { uri: node.catalogIconUri, className: 'wf-catalog-icon', error: 'Connector catalog icon unavailable.' },
    ].filter((entry, index, entries) => entry.uri && !entries.slice(0, index).some(previous => previous.uri === entry.uri));
    function drawRemoteIcon(index) {
      const source = remoteIcons[index];
      if (!source) {
        return;
      }
      const image = el('image', {
        class: source.className, width: size, height: size, preserveAspectRatio: 'xMidYMid meet',
      }, icon);
      image.addEventListener('load', () => fallback.classList.add('hidden'));
      image.addEventListener('error', () => {
        image.remove();
        fallback.classList.remove('hidden');
        group.querySelector('title').textContent += `\n${source.error}`;
        drawRemoteIcon(index + 1);
      }, { once: true });
      image.setAttribute('href', source.uri);
    }
    drawRemoteIcon(0);
  }

  function drawNote(node, group, defs) {
    const clipId = `note-clip-${nodeElements.size}`;
    const clip = el('clipPath', { id: clipId }, defs);
    el('rect', { x: node.x + 12, y: node.y + 10, width: Math.max(0, node.width - 24), height: Math.max(0, node.height - 20) }, clip);
    const text = node.note.text.trim() ? node.note.text : 'Empty note';
    group.querySelector('title').textContent = `${node.label}\n${text}`;
    group.setAttribute('aria-label', `${node.label}, ${text}`);
    const label = el('text', {
      class: `wf-note-text${node.note.text.trim() ? '' : ' wf-note-empty'}`,
      'clip-path': `url(#${clipId})`,
    }, group);
    const columns = Math.max(1, Math.floor((node.width - 24) / 7.5));
    let lineIndex = 0;
    const maxLines = Math.max(1, Math.ceil(node.height / 18));
    for (const line of text.split(/\r?\n/)) {
      for (let offset = 0; offset < Math.max(1, line.length) && lineIndex < maxLines; offset += columns) {
        el('tspan', { x: node.x + 12, y: node.y + 24 + lineIndex++ * 18 }, label).textContent = line.slice(offset, offset + columns);
      }
      if (lineIndex >= maxLines) {
        break;
      }
    }
  }

  function render() {
    const focusedNode = document.activeElement?.closest('.wf-node');
    const focusedNodeId = focusedNode && surface.contains(focusedNode) ? focusedNode.getAttribute('data-id') : null;
    surface.replaceChildren();
    surface.setAttribute('data-format', model.format);
    nodeElements.clear();
    nodeById = new Map(model.nodes.map(node => [node.id, node]));
    const defs = el('defs', {}, surface);
    const marker = el('marker', {
      id: 'arrow', viewBox: '0 0 10 10', refX: 7, refY: 5,
      markerWidth: 10, markerHeight: 10, markerUnits: 'userSpaceOnUse', orient: 'auto',
    }, defs);
    el('path', { d: 'M 2 1 L 7 5 L 2 9', class: 'wf-arrow' }, marker);
    root = el('g', {}, surface);
    applyView();
    const containerLayer = el('g', {}, root);
    const edgeLayer = el('g', {}, root);
    const nodeLayer = el('g', {}, root);

    for (const node of model.nodes) {
      const rows = branchRows(node);
      const group = el('g', {
        class: 'wf-node', 'data-id': node.id, tabindex: 0, role: 'button',
        'aria-label': [node.label, node.type, ...rows.map(row => row.label)].join(', '),
      }, node.isContainer ? containerLayer : nodeLayer);
      nodeElements.set(node.id, group);
      el('title', {}, group).textContent = `${node.label}\n${node.type}`;
      el('rect', {
        class: node.note ? 'wf-node-card wf-note' : node.isContainer ? 'wf-node-card wf-container' : 'wf-node-card',
        'data-note-color': node.note?.color || 'yellow',
        x: node.x, y: node.y, width: node.width, height: node.height,
        rx: node.note ? 4 : model.format === 'classic' ? 12 : node.isContainer ? 10 : 20,
      }, group);
      if (node.note) {
        drawNote(node, group, defs);
      } else {
        const headerY = node.y + (node.isContainer ? 24 : rows.length ? Math.min(26, node.height * 0.175) : node.height / 2);
        drawIcon(node, group, headerY, defs);
        const labelInset = model.format === 'classic' ? 72 : 58;
        const max = Math.max(4, Math.floor((node.width - (model.format === 'classic' ? 88 : 72)) / 7.5));
        el('text', { class: 'wf-node-label', x: node.x + labelInset, y: headerY + 5 }, group).textContent = truncate(node.label, max);
        if (model.format === 'designer' && rows.length) {
          const dividerY = (headerY + 10 + rows[0].y) / 2;
          el('line', {
            class: 'wf-header-divider', x1: node.x, x2: node.x + node.width,
            y1: dividerY, y2: dividerY, 'aria-hidden': 'true',
          }, group);
        }
        for (const row of rows) {
          const branch = el('g', {}, group);
          el('title', {}, branch).textContent = row.label;
          el('rect', {
            class: 'wf-branch', x: node.x + 16, y: row.y, width: node.width - 32, height: row.height, rx: row.height / 2,
          }, branch);
          el('text', {
            class: 'wf-branch-label', x: node.x + node.width / 2, y: row.y + row.height / 2 + 4,
            'text-anchor': 'middle',
          }, branch).textContent = truncate(row.label, max);
          el('circle', { class: 'wf-port', cx: node.x + node.width, cy: row.y + row.height / 2, r: 3.5 }, branch);
        }
      }
      const reveal = () => {
        selectNode(node.id);
        if (typeof node.actionOffset === 'number' && typeof node.actionLength === 'number') {
          vscode.postMessage({ type: 'reveal', nodeId: node.id, offset: node.actionOffset, length: node.actionLength });
        }
      };
      group.addEventListener('click', () => {
        if (!suppressClick) {
          reveal();
        }
      });
      group.addEventListener('keydown', event => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          event.stopPropagation();
          reveal();
        }
      });
    }
    model.edges.forEach(edge => {
      const source = nodeById.get(edge.source);
      const target = nodeById.get(edge.target);
      if (!source || !target) {
        return;
      }
      const points = edgePoints(source, target, edge);
      const path = points.length === 4
        ? `M ${points[0].join(' ')} C ${points.slice(1).map(point => point.join(' ')).join(', ')}`
        : `M ${points[0].join(' ')} L ${points[1].join(' ')}`;
      el('path', {
        class: 'wf-edge', d: path, 'aria-hidden': 'true',
        ...(model.format === 'designer' ? {} : { 'marker-end': 'url(#arrow)' }),
      }, edgeLayer);
      if (model.format !== 'classic') {
        for (const point of [points[0], points[points.length - 1]]) {
          el('circle', { class: 'wf-port', cx: point[0], cy: point[1], r: 3.5, 'aria-hidden': 'true' }, nodeLayer);
        }
      }
      const hasBranchRow = branchRows(source).some(row => row.id === edge.sourceHandle
        && (edge.label === row.label || edge.label === row.id || !edge.label));
      if (edge.label && !hasBranchRow) {
        const first = points[0];
        const last = points[points.length - 1];
        const x = (first[0] + last[0]) / 2;
        const y = (first[1] + last[1]) / 2;
        const label = truncate(edge.label, 36);
        const width = label.length * 7 + 16;
        const group = el('g', { role: 'img', 'aria-label': edge.label }, edgeLayer);
        el('title', {}, group).textContent = edge.label;
        el('rect', {
          class: 'wf-edge-label-bg', x: x - width / 2, y: y - 11, width, height: 22, rx: 4, 'aria-hidden': 'true',
        }, group);
        el('text', { class: 'wf-edge-label', x, y: y + 4, 'text-anchor': 'middle' }, group).textContent = label;
      }
    });
    selectNode(selectedId);
    banner.textContent = 'No workflow graph found in this file.';
    banner.classList.toggle('hidden', model.valid);
    if (focusedNodeId !== null) {
      (nodeElements.get(focusedNodeId) || canvas).focus({ preventScroll: true });
    }
  }

  function selectNode(id) {
    selectedId = id;
    for (const [nodeId, element] of nodeElements) {
      element.classList.toggle('selected', nodeId === id);
      element.setAttribute('aria-pressed', String(nodeId === id));
    }
  }

  function fit() {
    if (!model.nodes.length) {
      return;
    }
    const bounds = root.getBBox();
    const minX = bounds.x;
    const minY = bounds.y;
    const maxX = bounds.x + bounds.width;
    const maxY = bounds.y + bounds.height;
    const rect = surface.getBoundingClientRect();
    view.scale = Math.max(0.02, Math.min(rect.width / (maxX - minX + 96), rect.height / (maxY - minY + 96), 1.5));
    view.x = rect.width / 2 - (minX + maxX) * view.scale / 2;
    view.y = rect.height / 2 - (minY + maxY) * view.scale / 2;
    applyView();
  }

  function zoom(factor, x, y) {
    const rect = surface.getBoundingClientRect();
    const px = x ?? rect.width / 2;
    const py = y ?? rect.height / 2;
    const worldX = (px - view.x) / view.scale;
    const worldY = (py - view.y) / view.scale;
    view.scale = Math.min(Math.max(view.scale * factor, 0.02), 3);
    view.x = px - worldX * view.scale;
    view.y = py - worldY * view.scale;
    applyView();
  }

  surface.addEventListener('pointerdown', event => {
    if (panStart) {
      return;
    }
    suppressClick = false;
    if (event.button !== 0 || (event.target.closest('.wf-node') && !event.target.closest('.wf-container'))) {
      return;
    }
    panStart = { x: view.x, y: view.y, clientX: event.clientX, clientY: event.clientY, pointerId: event.pointerId };
  });
  surface.addEventListener('pointermove', event => {
    if (!panStart || event.pointerId !== panStart.pointerId) {
      return;
    }
    if (!(event.buttons & 1)) {
      endPan(event);
      return;
    }
    const dx = event.clientX - panStart.clientX;
    const dy = event.clientY - panStart.clientY;
    if (!suppressClick) {
      if (Math.hypot(dx, dy) < PAN_THRESHOLD) {
        return;
      }
      suppressClick = true;
      surface.setPointerCapture(event.pointerId);
      surface.classList.add('panning');
    }
    view.x = panStart.x + dx;
    view.y = panStart.y + dy;
    applyView();
  });
  function endPan(event) {
    if (panStart && event.pointerId !== panStart.pointerId) {
      return;
    }
    panStart = null;
    surface.classList.remove('panning');
  }
  surface.addEventListener('pointerup', endPan);
  surface.addEventListener('pointercancel', endPan);
  surface.addEventListener('lostpointercapture', endPan);
  surface.addEventListener('pointerleave', event => {
    if (!suppressClick) {
      endPan(event);
    }
  });
  surface.addEventListener('wheel', event => {
    event.preventDefault();
    const rect = surface.getBoundingClientRect();
    zoom(Math.exp(-event.deltaY * 0.0015), event.clientX - rect.left, event.clientY - rect.top);
  }, { passive: false });
  canvas.addEventListener('keydown', event => {
    const directions = { ArrowLeft: [40, 0], ArrowRight: [-40, 0], ArrowUp: [0, 40], ArrowDown: [0, -40] };
    if (directions[event.key]) {
      event.preventDefault();
      view.x += directions[event.key][0];
      view.y += directions[event.key][1];
      applyView();
    } else if (event.key.toLowerCase() === 'f') {
      event.preventDefault();
      fit();
    } else if (event.key === '+' || event.key === '=') {
      event.preventDefault();
      zoom(1.2);
    } else if (event.key === '-') {
      event.preventDefault();
      zoom(1 / 1.2);
    } else if (event.key === '0') {
      event.preventDefault();
      zoom(1 / view.scale);
    }
  });
  document.getElementById('btn-fit').addEventListener('click', fit);
  document.getElementById('btn-zoom-in').addEventListener('click', () => zoom(1.2));
  document.getElementById('btn-zoom-out').addEventListener('click', () => zoom(1 / 1.2));
  document.getElementById('btn-reset').addEventListener('click', () => zoom(1 / view.scale));
  window.addEventListener('message', event => {
    const message = event.data;
    if (!message) {
      return;
    }
    if (message.type === 'model') {
      const first = model.nodes.length === 0;
      model = message.model;
      render();
      if (first || message.fit) {
        fit();
      }
    } else if (message.type === 'selection') {
      const containing = model.nodes.filter(node => typeof node.actionOffset === 'number'
        && message.offset >= node.actionOffset && message.offset < node.actionOffset + node.actionLength);
      containing.sort((a, b) => a.actionLength - b.actionLength);
      selectNode(containing[0]?.id || null);
    }
  });
  vscode.postMessage({ type: 'ready' });
})();
