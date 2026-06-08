// @ts-nocheck
(function () {
  const vscode = acquireVsCodeApi();
  const SVG_NS = 'http://www.w3.org/2000/svg';

  const surface = document.getElementById('surface');
  const banner = document.getElementById('banner');

  let model = { nodes: [], edges: [], valid: false };
  let view = { x: 0, y: 0, scale: 1 };
  let nodeById = new Map();
  let selectedId = null;

  const TYPE_COLORS = {
    start: '#4F6BED',
    agent: '#8661C5',
    prompt: '#0E7FC1',
    classify: '#C19C00',
    m365Copilot: '#0A8043',
    connector: '#CA5010',
    variable: '#4894FE',
    loop: '#6B69D6',
    ifElse: '#9A6324',
    builtinFunction: '#5D5A88',
    canvasNote: '#797673',
  };

  function colorFor(type) {
    return TYPE_COLORS[type] || 'var(--vscode-descriptionForeground)';
  }

  function el(name, attrs, parent) {
    const node = document.createElementNS(SVG_NS, name);
    if (attrs) {
      for (const k of Object.keys(attrs)) {
        node.setAttribute(k, String(attrs[k]));
      }
    }
    if (parent) {
      parent.appendChild(node);
    }
    return node;
  }

  function clear() {
    while (surface.firstChild) {
      surface.removeChild(surface.firstChild);
    }
  }

  function applyView(root) {
    root.setAttribute('transform', `translate(${view.x} ${view.y}) scale(${view.scale})`);
  }

  const CONTAINER_HEADER = 50;

  function edgeEndpoints(source, target, edge) {
    const sHandle = edge.sourceHandle || '';
    const tHandle = edge.targetHandle || '';

    let sx;
    let sy;
    if (sHandle === 'internal-source') {
      sx = target.x + target.width / 2;
      sy = source.y + CONTAINER_HEADER;
    } else {
      sx = source.x + source.width / 2;
      sy = source.y + source.height;
    }

    let tx;
    let ty;
    if (tHandle === 'internal-target') {
      tx = sx;
      ty = target.y + target.height;
    } else {
      tx = target.x + target.width / 2;
      ty = target.y;
    }

    return { p1: { x: sx, y: sy }, p2: { x: tx, y: ty } };
  }

  function render() {
    clear();
    nodeById = new Map();
    for (const n of model.nodes) {
      nodeById.set(n.id, n);
    }

    const root = el('g', {}, surface);
    applyView(root);

    const edgeLayer = el('g', {}, root);
    const containerLayer = el('g', {}, root);
    const nodeLayer = el('g', {}, root);

    for (const n of model.nodes) {
      if (!n.isContainer) {
        continue;
      }
      el('rect', {
        class: 'wf-container',
        x: n.x,
        y: n.y,
        width: n.width,
        height: n.height,
      }, containerLayer);
      el('text', {
        class: 'wf-container-label',
        x: n.x + 12,
        y: n.y + 20,
      }, containerLayer).textContent = n.label;
    }

    for (const e of model.edges) {
      const s = nodeById.get(e.source);
      const t = nodeById.get(e.target);
      if (!s || !t) {
        continue;
      }
      const { p1, p2 } = edgeEndpoints(s, t, e);
      const dy = Math.abs(p2.y - p1.y);
      const curve = Math.max(Math.min(dy / 2, 60), 12);
      const d = `M ${p1.x} ${p1.y} C ${p1.x} ${p1.y + curve}, ${p2.x} ${p2.y - curve}, ${p2.x} ${p2.y}`;
      el('path', { class: 'wf-edge', d }, edgeLayer);

      if (e.label) {
        const lx = (p1.x + p2.x) / 2;
        const ly = (p1.y + p2.y) / 2;
        const approxW = e.label.length * 6 + 8;
        el('rect', {
          class: 'wf-edge-label-bg',
          x: lx - approxW / 2,
          y: ly - 9,
          width: approxW,
          height: 16,
          rx: 3,
        }, edgeLayer);
        el('text', {
          class: 'wf-edge-label',
          x: lx,
          y: ly + 3,
          'text-anchor': 'middle',
        }, edgeLayer).textContent = e.label;
      }
    }

    for (const n of model.nodes) {
      if (n.isContainer) {
        continue;
      }
      const g = el('g', { class: 'wf-node', 'data-id': n.id }, nodeLayer);
      if (n.id === selectedId) {
        g.classList.add('selected');
      }
      el('rect', {
        class: 'wf-node-card',
        x: n.x,
        y: n.y,
        width: n.width,
        height: n.height,
      }, g);
      el('rect', {
        class: 'wf-node-accent',
        x: n.x,
        y: n.y,
        width: 5,
        height: n.height,
        fill: colorFor(n.type),
      }, g);
      el('text', {
        class: 'wf-node-label',
        x: n.x + 16,
        y: n.y + n.height / 2 - 2,
      }, g).textContent = truncate(n.label, n.width);
      el('text', {
        class: 'wf-node-type',
        x: n.x + 16,
        y: n.y + n.height / 2 + 14,
      }, g).textContent = n.type;

      g.addEventListener('click', () => {
        if (typeof n.actionOffset === 'number' && typeof n.actionLength === 'number') {
          vscode.postMessage({ type: 'reveal', nodeId: n.id, offset: n.actionOffset, length: n.actionLength });
        }
      });
    }

    if (!model.valid) {
      banner.textContent = 'No workflow graph found in this file.';
      banner.classList.remove('hidden');
    } else {
      banner.classList.add('hidden');
    }
  }

  function truncate(text, width) {
    const max = Math.max(4, Math.floor((width - 24) / 7));
    return text.length > max ? text.slice(0, max - 1) + '\u2026' : text;
  }

  function bounds() {
    if (model.nodes.length === 0) {
      return { minX: 0, minY: 0, maxX: 100, maxY: 100 };
    }
    let minX = Infinity;
    let minY = Infinity;
    let maxX = -Infinity;
    let maxY = -Infinity;
    for (const n of model.nodes) {
      minX = Math.min(minX, n.x);
      minY = Math.min(minY, n.y);
      maxX = Math.max(maxX, n.x + n.width);
      maxY = Math.max(maxY, n.y + n.height);
    }
    return { minX, minY, maxX, maxY };
  }

  function fit() {
    const b = bounds();
    const pad = 40;
    const w = b.maxX - b.minX + pad * 2;
    const h = b.maxY - b.minY + pad * 2;
    const rect = surface.getBoundingClientRect();
    const scale = Math.min(rect.width / w, rect.height / h, 1.5);
    view.scale = scale > 0 ? scale : 1;
    view.x = -(b.minX - pad) * view.scale + (rect.width - w * view.scale) / 2;
    view.y = -(b.minY - pad) * view.scale;
    render();
  }

  function zoom(factor, cx, cy) {
    const rect = surface.getBoundingClientRect();
    const px = (cx ?? rect.width / 2);
    const py = (cy ?? rect.height / 2);
    const wx = (px - view.x) / view.scale;
    const wy = (py - view.y) / view.scale;
    view.scale = Math.min(Math.max(view.scale * factor, 0.1), 3);
    view.x = px - wx * view.scale;
    view.y = py - wy * view.scale;
    render();
  }

  let panning = false;
  let panStart = null;
  surface.addEventListener('mousedown', (ev) => {
    if (ev.target.closest('.wf-node')) {
      return;
    }
    panning = true;
    panStart = { x: ev.clientX - view.x, y: ev.clientY - view.y };
    surface.classList.add('panning');
  });
  window.addEventListener('mousemove', (ev) => {
    if (!panning || !panStart) {
      return;
    }
    view.x = ev.clientX - panStart.x;
    view.y = ev.clientY - panStart.y;
    const root = surface.firstChild;
    if (root) {
      applyView(root);
    }
  });
  window.addEventListener('mouseup', () => {
    panning = false;
    panStart = null;
    surface.classList.remove('panning');
  });
  surface.addEventListener('wheel', (ev) => {
    ev.preventDefault();
    const rect = surface.getBoundingClientRect();
    zoom(ev.deltaY < 0 ? 1.1 : 0.9, ev.clientX - rect.left, ev.clientY - rect.top);
  }, { passive: false });

  document.getElementById('btn-fit').addEventListener('click', fit);
  document.getElementById('btn-zoom-in').addEventListener('click', () => zoom(1.2));
  document.getElementById('btn-zoom-out').addEventListener('click', () => zoom(0.8));

  function selectByOffset(offset) {
    let found = null;
    let bestLen = Infinity;
    for (const n of model.nodes) {
      if (n.isContainer) {
        continue;
      }
      if (typeof n.actionOffset === 'number' && typeof n.actionLength === 'number') {
        if (offset >= n.actionOffset && offset <= n.actionOffset + n.actionLength) {
          if (n.actionLength < bestLen) {
            bestLen = n.actionLength;
            found = n.id;
          }
        }
      }
    }
    if (found !== selectedId) {
      selectedId = found;
      render();
    }
  }

  window.addEventListener('message', (event) => {
    const msg = event.data;
    if (!msg) {
      return;
    }
    if (msg.type === 'model') {
      const firstRender = model.nodes.length === 0;
      model = msg.model || { nodes: [], edges: [], valid: false };
      if (firstRender) {
        fit();
      } else {
        render();
      }
    } else if (msg.type === 'selection') {
      selectByOffset(msg.offset);
    }
  });

  vscode.postMessage({ type: 'ready' });
})();
