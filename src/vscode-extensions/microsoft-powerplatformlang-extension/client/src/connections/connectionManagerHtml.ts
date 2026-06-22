import * as vscode from 'vscode';

function getNonce(): string {
  let text = '';
  const possible = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  for (let i = 0; i < 32; i++) {
    text += possible.charAt(Math.floor(Math.random() * possible.length));
  }
  return text;
}

export function buildConnectionManagerHtml(webview: vscode.Webview, agentName: string): string {
  const nonce = getNonce();
  const csp = [
    `default-src 'none'`,
    `img-src ${webview.cspSource} https: data:`,
    `style-src 'nonce-${nonce}'`,
    `script-src 'nonce-${nonce}'`
  ].join('; ');

  const title = escapeHtml(agentName);

  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta http-equiv="Content-Security-Policy" content="${csp}" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <title>Connections</title>
  <style nonce="${nonce}">
    :root { color-scheme: light dark; }
    body {
      font-family: var(--vscode-font-family);
      font-size: var(--vscode-font-size);
      color: var(--vscode-foreground);
      background: var(--vscode-editor-background);
      padding: 12px 16px;
      margin: 0;
    }
    h1 { font-size: 1.15em; margin: 0 0 4px 0; }
    .subtitle { color: var(--vscode-descriptionForeground); margin: 0 0 12px 0; }
    .toolbar { display: flex; gap: 8px; align-items: center; margin-bottom: 12px; }
    .search {
      font-family: inherit; font-size: inherit;
      color: var(--vscode-input-foreground);
      background: var(--vscode-input-background);
      border: 1px solid var(--vscode-input-border, var(--vscode-dropdown-border));
      padding: 4px 8px; border-radius: 2px; min-width: 220px; margin-left: auto;
      box-sizing: border-box;
    }
    .search::placeholder { color: var(--vscode-input-placeholderForeground); }
    button {
      font-family: inherit;
      font-size: inherit;
      color: var(--vscode-button-foreground);
      background: var(--vscode-button-background);
      border: none;
      padding: 4px 12px;
      border-radius: 2px;
      cursor: pointer;
    }
    button:hover { background: var(--vscode-button-hoverBackground); }
    button.secondary {
      color: var(--vscode-button-secondaryForeground);
      background: var(--vscode-button-secondaryBackground);
    }
    button.secondary:hover { background: var(--vscode-button-secondaryHoverBackground); }
    button:disabled { opacity: 0.5; cursor: default; }
    .linkbtn {
      background: none;
      color: var(--vscode-textLink-foreground);
      padding: 0;
      border: none;
      cursor: pointer;
      font-size: 0.9em;
    }
    .linkbtn:hover { background: none; text-decoration: underline; }
    select {
      font-family: inherit;
      font-size: inherit;
      color: var(--vscode-dropdown-foreground);
      background: var(--vscode-dropdown-background);
      border: 1px solid var(--vscode-dropdown-border);
      padding: 4px 6px;
      border-radius: 2px;
      width: 100%;
      min-width: 0;
      max-width: 100%;
      box-sizing: border-box;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .band-picker select { flex: 1 1 auto; min-width: 0; }
    .dot { display: inline-block; width: 10px; height: 10px; border-radius: 50%; margin-right: 6px; vertical-align: middle; }
    .dot.bound { background: var(--vscode-testing-iconPassed, #2da44e); }
    .dot.unbound { background: var(--vscode-editorWarning-foreground, #d4a017); }
    .banner { padding: 8px 10px; border-radius: 2px; margin-bottom: 12px; }
    .banner.error { background: var(--vscode-inputValidation-errorBackground); border: 1px solid var(--vscode-inputValidation-errorBorder); }
    .banner.warning { background: var(--vscode-inputValidation-warningBackground); border: 1px solid var(--vscode-inputValidation-warningBorder); }
    .banner.info { background: var(--vscode-inputValidation-infoBackground); border: 1px solid var(--vscode-inputValidation-infoBorder); }
    .hidden { display: none; }
    .empty { color: var(--vscode-descriptionForeground); padding: 16px 0; }
    .spinner { color: var(--vscode-descriptionForeground); }
    .badge { display: inline-block; padding: 1px 6px; border-radius: 8px; font-size: 0.8em; margin-left: 6px; }
    .badge.undeclared { background: var(--vscode-editorWarning-foreground, #d4a017); color: var(--vscode-editor-background); }

    table.conn { border-collapse: collapse; width: 100%; }
    table.conn thead th {
      text-align: left; padding: 6px 10px; font-size: 0.85em; font-weight: 600;
      color: var(--vscode-descriptionForeground);
      border-bottom: 1px solid var(--vscode-panel-border);
      position: sticky; top: 0; background: var(--vscode-editor-background); z-index: 1;
    }
    table.conn td { padding: 8px 10px; border-bottom: 1px solid var(--vscode-panel-border); vertical-align: top; }
    .col-status { width: 120px; }
    .col-conn { width: 260px; max-width: 260px; }
    .conn-cell { max-width: 260px; overflow: hidden; }
    tr.band > td { background: var(--vscode-list-hoverBackground); padding: 7px 10px; }
    .band-inner { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
    .band-chevron { cursor: pointer; width: 12px; display: inline-block; user-select: none; color: var(--vscode-descriptionForeground); }
    .band-title { font-weight: 600; }
    .band-count { color: var(--vscode-descriptionForeground); font-size: 0.9em; }
    .pill { display: inline-block; padding: 1px 8px; border-radius: 8px; font-size: 0.8em; }
    .pill.attention { background: var(--vscode-editorWarning-foreground, #d4a017); color: var(--vscode-editor-background); }
    .band-picker { display: flex; align-items: center; gap: 6px; margin-left: auto; flex-wrap: nowrap; width: 260px; max-width: 260px; justify-content: flex-end; box-sizing: border-box; }
    .band-mode-toggle {
      flex: none; background: none; border: none; padding: 2px 5px; cursor: pointer;
      border-radius: 4px; color: var(--vscode-descriptionForeground);
      font-size: 1.05em; line-height: 1;
      display: inline-flex; align-items: center; justify-content: center;
    }
    .band-mode-toggle:hover { background: var(--vscode-toolbar-hoverBackground, rgba(128,128,128,0.2)); color: var(--vscode-foreground); }
    .band-mode-toggle:focus { outline: 1px solid var(--vscode-focusBorder); }

    .ref-name { font-weight: 600; display: flex; align-items: center; gap: 6px; }
    .ref-sub { margin-top: 3px; }
    .ref-remove {
      background: none; border: none; padding: 2px; cursor: pointer; border-radius: 4px;
      color: var(--vscode-foreground);
      font-size: 1.1em; font-weight: 700; line-height: 1; opacity: 0;
      display: inline-flex; align-items: center; justify-content: center;
    }
    .ref-remove:hover {
      color: var(--vscode-errorForeground, #f14c4c);
      background: var(--vscode-toolbar-hoverBackground, rgba(128,128,128,0.2));
    }
    tr:hover .ref-remove { opacity: 1; }
    .ref-remove:focus { opacity: 1; outline: 1px solid var(--vscode-focusBorder); }
    .conn-current { color: var(--vscode-foreground); }
    .conn-pending { color: var(--vscode-textLink-foreground); }
    .conn-none { color: var(--vscode-editorWarning-foreground, #d4a017); }

    .usage-list { margin: 2px 0 0 0; padding: 0; list-style: none; }
    .usage-list li { margin: 0 0 3px 0; }
    .usage-link { background: none; border: none; padding: 0; cursor: pointer; color: var(--vscode-textLink-foreground); font-size: 0.9em; text-align: left; }
    .usage-link:hover { background: none; text-decoration: underline; }
    .usage-none { color: var(--vscode-descriptionForeground); font-size: 0.9em; }
    .wf-usage { display: flex; align-items: center; gap: 8px; }
    .wf-dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; vertical-align: middle; flex: none; }
    .wf-dot.on { background: var(--vscode-testing-iconPassed, #2da44e); }
    .wf-dot.off { background: var(--vscode-editorWarning-foreground, #d4a017); }
    .wf-toggle {
      background: none; border: none; padding: 2px 5px; cursor: pointer;
      border-radius: 4px; color: var(--vscode-descriptionForeground);
      font-size: 1.15em; line-height: 1;
      display: inline-flex; align-items: center; justify-content: center;
    }
    .wf-toggle:hover { background: var(--vscode-toolbar-hoverBackground, rgba(128,128,128,0.2)); color: var(--vscode-foreground); }
    .wf-toggle:focus { outline: 1px solid var(--vscode-focusBorder); }
    .wf-toggle.on { color: var(--vscode-testing-iconPassed, #2da44e); }
    .wf-toggle:disabled { opacity: 0.5; cursor: not-allowed; }
    .disabled-wrap { display: inline-block; cursor: not-allowed; }
    .disabled-wrap > button { pointer-events: none; }

    .add-row { display: flex; gap: 8px; align-items: center; margin: 12px 0 0 0; }
  </style>
</head>
<body>
  <h1>Connections</h1>
  <p class="subtitle">${title}</p>
  <div class="toolbar">
    <button id="apply" disabled>Apply</button>
    <button id="refresh" class="secondary">Refresh</button>
    <span id="status" class="spinner"></span>
    <input id="search" class="search" type="search" placeholder="Filter connections…" aria-label="Filter connections" />
  </div>
  <div id="banner" class="banner hidden"></div>
  <div id="content"></div>
  <div class="add-row">
    <button id="addRef" class="secondary">Add connection reference…</button>
  </div>
  <script nonce="${nonce}">
    const vscode = acquireVsCodeApi();
    const contentEl = document.getElementById('content');
    const applyBtn = document.getElementById('apply');
    const refreshBtn = document.getElementById('refresh');
    const statusEl = document.getElementById('status');
    const bannerEl = document.getElementById('banner');
    const addRefBtn = document.getElementById('addRef');
    const searchInput = document.getElementById('search');

    let views = [];
    let workflows = [];
    let groups = [];
    let filterQuery = '';
    const individualGroups = {};
    const collapsedGroups = {};

    const KEEP = '__keep__';
    const CREATE = '__create__';

    function tail(id) {
      if (!id) { return ''; }
      const parts = String(id).split('/').filter(Boolean);
      return parts.length ? parts[parts.length - 1] : String(id);
    }

    function connectorKeyOf(v) { return v.connectorName || v.connectorId || ''; }

    function candidateDisplayName(c) {
      return c.displayName || tail(c.name);
    }

    function collidingCandidateNames(candidates) {
      const counts = {};
      candidates.forEach(function (c) {
        const key = candidateDisplayName(c).toLowerCase();
        counts[key] = (counts[key] || 0) + 1;
      });
      const collisions = {};
      Object.keys(counts).forEach(function (k) { if (counts[k] > 1) { collisions[k] = true; } });
      return collisions;
    }

    function candidateLabel(c, collisions) {
      const name = candidateDisplayName(c);
      const idTail = collisions && collisions[name.toLowerCase()] ? ' \u00b7 ' + tail(c.name) : '';
      const showOwner = c.owner && c.owner.toLowerCase() !== name.toLowerCase();
      const owner = showOwner ? ' \u2014 ' + c.owner : '';
      const status = c.status ? ' (' + c.status + ')' : '';
      return name + idTail + owner + status;
    }

    function isConnectable(c) {
      return (c.status || '').toLowerCase() === 'connected';
    }

    function showBanner(kind, message) {
      bannerEl.className = 'banner ' + kind;
      bannerEl.textContent = message;
    }

    function clearBanner() {
      bannerEl.className = 'banner hidden';
      bannerEl.textContent = '';
    }

    function setBusy(busy, message) {
      applyBtn.disabled = busy || !hasPendingChanges();
      refreshBtn.disabled = busy;
      addRefBtn.disabled = busy;
      contentEl.querySelectorAll('select').forEach(function (el) { el.disabled = busy; });
      contentEl.querySelectorAll('button').forEach(function (el) {
        if (busy) { el.disabled = true; }
        else if (!el.dataset.lockedDisabled) { el.disabled = false; }
      });
      statusEl.textContent = message || '';
    }

    function buildGroups() {
      const map = {};
      const order = [];
      views.forEach(function (v) {
        const key = connectorKeyOf(v);
        if (!map[key]) {
          map[key] = { key: key, connectorName: v.connectorName, connectorId: v.connectorId, references: [], candByName: {} };
          order.push(key);
        }
        map[key].references.push(v);
        (v.candidates || []).forEach(function (c) {
          if (!map[key].candByName[c.name]) { map[key].candByName[c.name] = c; }
        });
      });
      groups = order.map(function (key) {
        const g = map[key];
        g.candidates = Object.keys(g.candByName).map(function (n) { return g.candByName[n]; });
        g.unboundCount = g.references.filter(function (r) { return !r.boundConnectionExists; }).length;
        g.allBound = g.unboundCount === 0;
        return g;
      });
      groups.sort(function (a, b) {
        if (a.allBound !== b.allBound) { return a.allBound ? 1 : -1; }
        return String(a.connectorName || a.connectorId).localeCompare(String(b.connectorName || b.connectorId));
      });
    }

    function normalizeWorkflowPath(filePath) {
      return String(filePath || '').replace(/\\\\/g, '/').replace(/metadata\\.yml$/i, '').replace(/\\/+$/, '').toLowerCase();
    }

    function workflowByFilePath(filePath) {
      const target = normalizeWorkflowPath(filePath);
      if (!target) { return null; }
      for (let i = 0; i < workflows.length; i++) {
        if (normalizeWorkflowPath(workflows[i].filePath) === target) { return workflows[i]; }
      }
      return null;
    }

    function workflowJsonPath(metadataPath) {
      return String(metadataPath || '').replace(/metadata\\.yml$/i, 'workflow.json');
    }

    function buildWorkflowUsage(u, wf) {
      const li = document.createElement('li');
      li.className = 'wf-usage';

      const on = wf.state === 2;
      const dot = document.createElement('span');
      dot.className = 'wf-dot ' + (on ? 'on' : 'off');
      dot.title = on ? 'Workflow is running' : workflowBlockTitle(wf);
      li.appendChild(dot);

      const jsonPath = workflowJsonPath(u.filePath);
      const link = document.createElement('button');
      link.className = 'usage-link';
      link.textContent = u.displayName || tail(u.filePath) || u.filePath;
      link.title = jsonPath;
      link.addEventListener('click', function () {
        vscode.postMessage({ type: 'openUsage', filePath: jsonPath });
      });
      li.appendChild(link);

      const wfName = wf.displayName || u.displayName || tail(u.filePath) || u.filePath;
      const btn = document.createElement('button');
      btn.className = 'wf-toggle';
      btn.textContent = '\u23FB';
      let actionEl = btn;
      if (on) {
        btn.classList.add('on');
        const msg = "Disable workflow '" + wfName + "'. It is currently enabled.";
        btn.title = msg;
        btn.setAttribute('aria-label', msg);
        btn.addEventListener('click', function () { toggleWorkflow(wf); });
      } else if (!wf.canEnable) {
        btn.disabled = true;
        btn.dataset.lockedDisabled = 'true';
        const lockMsg = "Can't enable workflow '" + wfName + "' yet. " + workflowBlockTitle(wf) + '.';
        btn.title = lockMsg;
        btn.setAttribute('aria-label', lockMsg);
        const wrap = document.createElement('span');
        wrap.className = 'disabled-wrap';
        wrap.title = lockMsg;
        wrap.addEventListener('click', function () { showBanner('info', lockMsg); });
        wrap.appendChild(btn);
        actionEl = wrap;
      } else {
        const msg = "Enable workflow '" + wfName + "'. It is currently disabled.";
        btn.title = msg;
        btn.setAttribute('aria-label', msg);
        btn.addEventListener('click', function () { toggleWorkflow(wf); });
      }
      li.appendChild(actionEl);

      li.addEventListener('contextmenu', function (e) {
        e.preventDefault();
        toggleWorkflow(wf);
      });

      return li;
    }

    function buildUsedBy(v) {
      const wrap = document.createElement('div');
      const usages = v.usages || [];
      if (usages.length === 0) {
        const none = document.createElement('span');
        none.className = 'usage-none';
        none.textContent = 'Not used';
        wrap.appendChild(none);
        return wrap;
      }
      const ul = document.createElement('ul');
      ul.className = 'usage-list';
      usages.forEach(function (u) {
        if (u.kind === 2) {
          const wf = workflowByFilePath(u.filePath);
          if (wf) {
            ul.appendChild(buildWorkflowUsage(u, wf));
            return;
          }
        }
        const li = document.createElement('li');
        const link = document.createElement('button');
        link.className = 'usage-link';
        link.textContent = u.displayName || tail(u.filePath) || u.filePath;
        link.title = u.filePath;
        link.addEventListener('click', function () {
          vscode.postMessage({ type: 'openUsage', filePath: u.filePath });
        });
        li.appendChild(link);
        ul.appendChild(li);
      });
      wrap.appendChild(ul);
      return wrap;
    }

    function setSelectTick(select, show) {
      for (let i = 0; i < select.options.length; i++) {
        const opt = select.options[i];
        let text = opt.textContent;
        if (text.indexOf('✅ ') === 0) { text = text.substring('✅ '.length); }
        const tickable = opt.value !== KEEP && opt.value !== CREATE;
        opt.textContent = (show && opt.selected && tickable ? '✅ ' : '') + text;
      }
    }

    function attachTickHandlers(select) {
      select.addEventListener('focus', function () { setSelectTick(select, true); });
      select.addEventListener('blur', function () { setSelectTick(select, false); });
    }

    function buildRefSelect(v) {
      const select = document.createElement('select');
      select.id = 'sel-' + v.connectionReferenceLogicalName;

      const keepOpt = document.createElement('option');
      keepOpt.value = KEEP;
      keepOpt.textContent = v.boundConnectionExists ? 'Keep current connection' : 'Choose a connection…';
      select.appendChild(keepOpt);

      const candidates = (v.candidates || []).filter(function (c) {
        return isConnectable(c) || tail(c.name) === tail(v.boundConnectionId);
      });
      const collisions = collidingCandidateNames(candidates);
      candidates.forEach(function (c) {
        const opt = document.createElement('option');
        opt.value = c.name;
        opt.textContent = candidateLabel(c, collisions);
        if (v.boundConnectionExists && tail(c.name) === tail(v.boundConnectionId)) { opt.selected = true; }
        select.appendChild(opt);
      });

      const createOpt = document.createElement('option');
      createOpt.value = CREATE;
      createOpt.textContent = 'Create new connection…';
      select.appendChild(createOpt);

      attachTickHandlers(select);
      select.addEventListener('change', function () {
        setSelectTick(select, true);
        refreshApplyState();
      });
      return select;
    }

    function currentConnLabel(v) {
      if (!v.boundConnectionExists) { return null; }
      const cand = (v.candidates || []).find(function (c) { return tail(c.name) === tail(v.boundConnectionId); });
      return cand ? (cand.displayName || tail(cand.name)) : tail(v.boundConnectionId);
    }

    function renderConnCellCurrent(td, v) {
      td.innerHTML = '';
      const span = document.createElement('span');
      const label = currentConnLabel(v);
      if (label) { span.className = 'conn-current'; span.textContent = label; }
      else { span.className = 'conn-none'; span.textContent = 'Not connected'; }
      td.appendChild(span);
    }

    function setConnCellPending(td, v, group, val) {
      if (val === KEEP) { renderConnCellCurrent(td, v); return; }
      td.innerHTML = '';
      const span = document.createElement('span');
      span.className = 'conn-pending';
      if (val === CREATE) {
        span.textContent = '→ New connection (on Apply)';
      } else {
        const cand = (group.candidates || []).find(function (c) { return c.name === val; });
        span.textContent = '→ ' + (cand ? (cand.displayName || tail(cand.name)) : tail(val)) + ' (pending)';
      }
      td.appendChild(span);
    }

    function groupCurrentConnection(group) {
      if (!group.references.length) { return null; }
      let commonTail = null;
      for (let i = 0; i < group.references.length; i++) {
        const r = group.references[i];
        if (!r.boundConnectionExists) { return null; }
        const t = tail(r.boundConnectionId);
        if (commonTail === null) { commonTail = t; }
        else if (commonTail !== t) { return null; }
      }
      const cand = (group.candidates || []).find(function (c) { return tail(c.name) === commonTail; });
      return { tail: commonTail, label: cand ? (cand.displayName || tail(cand.name)) : commonTail };
    }

    function buildGroupSelect(group, index) {
      const select = document.createElement('select');
      select.id = 'gsel-' + index;

      const common = groupCurrentConnection(group);
      const anyBound = group.references.some(function (r) { return r.boundConnectionExists; });
      const keepOpt = document.createElement('option');
      keepOpt.value = KEEP;
      keepOpt.textContent = anyBound ? 'Keep current connections' : 'Choose a connection…';
      select.appendChild(keepOpt);

      const candidates = group.candidates.filter(function (c) {
        return isConnectable(c) || (common && tail(c.name) === common.tail);
      });
      const collisions = collidingCandidateNames(candidates);
      let selectedCurrent = false;
      candidates.forEach(function (c) {
        const opt = document.createElement('option');
        opt.value = c.name;
        opt.textContent = candidateLabel(c, collisions);
        if (common && tail(c.name) === common.tail) { opt.selected = true; selectedCurrent = true; }
        select.appendChild(opt);
      });

      if (common && !selectedCurrent) {
        keepOpt.textContent = common.label;
      }

      const createOpt = document.createElement('option');
      createOpt.value = CREATE;
      createOpt.textContent = 'Create new connection…';
      select.appendChild(createOpt);

      attachTickHandlers(select);
      select.addEventListener('change', function () {
        setSelectTick(select, true);
        const val = select.value;
        group.references.forEach(function (v) {
          const td = document.getElementById('conn-' + v.connectionReferenceLogicalName);
          if (td) { setConnCellPending(td, v, group, val); }
        });
        refreshApplyState();
      });
      return select;
    }

    function buildRefRow(v, index, individual, collapsed) {
      const tr = document.createElement('tr');
      tr.className = 'rows-' + index;
      if (collapsed) { tr.style.display = 'none'; }

      const statusTd = document.createElement('td');
      const dot = document.createElement('span');
      dot.className = 'dot ' + (v.boundConnectionExists ? 'bound' : 'unbound');
      statusTd.appendChild(dot);
      statusTd.appendChild(document.createTextNode(v.boundConnectionExists ? 'Connected' : 'Not connected'));
      tr.appendChild(statusTd);

      const refTd = document.createElement('td');
      const name = document.createElement('div');
      name.className = 'ref-name';
      const nameText = document.createElement('span');
      nameText.textContent = v.connectionReferenceLogicalName;
      name.appendChild(nameText);
      if (v.isDeclared === false) {
        const badge = document.createElement('span');
        badge.className = 'badge undeclared';
        badge.textContent = 'Not declared — added on Apply';
        name.appendChild(badge);
      } else {
        const removeBtn = document.createElement('button');
        removeBtn.className = 'ref-remove';
        removeBtn.textContent = '✕';
        removeBtn.title = 'Remove reference';
        removeBtn.setAttribute('aria-label', 'Remove reference ' + v.connectionReferenceLogicalName);
        removeBtn.addEventListener('click', function () {
          vscode.postMessage({ type: 'deleteReference', logicalName: v.connectionReferenceLogicalName });
        });
        name.appendChild(removeBtn);
      }
      refTd.appendChild(name);
      if (v.isDeclared === false) {
        const sub = document.createElement('div');
        sub.className = 'ref-sub';
        const declareLink = document.createElement('button');
        declareLink.className = 'linkbtn';
        declareLink.textContent = 'Declare now';
        declareLink.addEventListener('click', function () {
          vscode.postMessage({ type: 'declareReference', logicalName: v.connectionReferenceLogicalName });
        });
        sub.appendChild(declareLink);
        refTd.appendChild(sub);
      }
      tr.appendChild(refTd);

      const usedTd = document.createElement('td');
      usedTd.appendChild(buildUsedBy(v));
      tr.appendChild(usedTd);

      const connTd = document.createElement('td');
      connTd.className = 'conn-cell';
      connTd.id = 'conn-' + v.connectionReferenceLogicalName;
      if (individual) {
        connTd.appendChild(buildRefSelect(v));
      } else {
        renderConnCellCurrent(connTd, v);
      }
      tr.appendChild(connTd);

      return tr;
    }

    function buildBandRow(group, index, individual, collapsed) {
      const tr = document.createElement('tr');
      tr.className = 'band';
      const td = document.createElement('td');
      td.colSpan = 4;
      const inner = document.createElement('div');
      inner.className = 'band-inner';

      const chevron = document.createElement('span');
      chevron.className = 'band-chevron';
      chevron.textContent = collapsed ? '▸' : '▾';
      chevron.addEventListener('click', function () {
        if (collapsedGroups[group.key]) { delete collapsedGroups[group.key]; }
        else { collapsedGroups[group.key] = true; }
        renderConnections();
      });
      inner.appendChild(chevron);

      const title = document.createElement('span');
      title.className = 'band-title';
      title.textContent = group.connectorName || group.connectorId || 'Connector';
      inner.appendChild(title);

      const count = document.createElement('span');
      count.className = 'band-count';
      count.textContent = '(' + group.references.length + ')';
      inner.appendChild(count);

      if (!group.allBound) {
        const pill = document.createElement('span');
        pill.className = 'pill attention';
        pill.textContent = group.unboundCount + ' need' + (group.unboundCount === 1 ? 's' : '') + ' a connection';
        inner.appendChild(pill);
      }

      const picker = document.createElement('span');
      picker.className = 'band-picker';
      if (!individual) {
        picker.appendChild(buildGroupSelect(group, index));
        if (group.references.length > 1) {
          const indivBtn = document.createElement('button');
          indivBtn.className = 'band-mode-toggle';
          indivBtn.textContent = '\u2630';
          const indivMsg = 'Set each connection reference individually';
          indivBtn.title = indivMsg;
          indivBtn.setAttribute('aria-label', indivMsg);
          indivBtn.addEventListener('click', function () {
            individualGroups[group.key] = true;
            renderConnections();
          });
          picker.appendChild(indivBtn);
        }
      } else {
        const backBtn = document.createElement('button');
        backBtn.className = 'band-mode-toggle';
        backBtn.textContent = '\u21A9';
        const backMsg = 'Use one connection for all references';
        backBtn.title = backMsg;
        backBtn.setAttribute('aria-label', backMsg);
        backBtn.addEventListener('click', function () {
          delete individualGroups[group.key];
          renderConnections();
        });
        picker.appendChild(backBtn);
      }
      inner.appendChild(picker);

      td.appendChild(inner);
      tr.appendChild(td);
      return tr;
    }

    function matchesFilter(v) {
      if (!filterQuery) { return true; }
      const q = filterQuery.toLowerCase();
      function hit(s) { return !!s && String(s).toLowerCase().indexOf(q) !== -1; }
      if (hit(v.connectionReferenceLogicalName) || hit(v.connectorName) || hit(v.connectorId)) { return true; }
      if (hit(currentConnLabel(v))) { return true; }
      const usages = v.usages || [];
      for (let i = 0; i < usages.length; i++) {
        if (hit(usages[i].displayName) || hit(usages[i].filePath)) { return true; }
      }
      return false;
    }

    function renderConnections() {
      contentEl.innerHTML = '';
      if (!views.length) {
        contentEl.innerHTML = '<div class="empty">This agent has no connection references.</div>';
        refreshApplyState();
        return;
      }

      const table = document.createElement('table');
      table.className = 'conn';
      const thead = document.createElement('thead');
      thead.innerHTML = '<tr><th class="col-status">Status</th><th>Connection reference</th><th>Used by</th><th class="col-conn">Connection</th></tr>';
      table.appendChild(thead);
      const tbody = document.createElement('tbody');

      const hasFilter = !!filterQuery;
      let anyVisible = false;
      groups.forEach(function (group, index) {
        const individual = !!individualGroups[group.key];
        const collapsed = !!collapsedGroups[group.key] && !hasFilter;
        const bandVisible = !hasFilter || group.references.some(matchesFilter);
        if (bandVisible) { anyVisible = true; }
        const bandRow = buildBandRow(group, index, individual, collapsed);
        if (!bandVisible) { bandRow.style.display = 'none'; }
        tbody.appendChild(bandRow);
        group.references.forEach(function (v) {
          const row = buildRefRow(v, index, individual, collapsed);
          if (!bandVisible || (hasFilter && !matchesFilter(v))) { row.style.display = 'none'; }
          tbody.appendChild(row);
        });
      });

      table.appendChild(tbody);
      contentEl.appendChild(table);
      if (hasFilter && !anyVisible) {
        const none = document.createElement('div');
        none.className = 'empty';
        none.textContent = 'No connections match "' + filterQuery + '".';
        contentEl.appendChild(none);
      }
      refreshApplyState();
    }

    function groupHasPending(group, index) {
      if (individualGroups[group.key]) {
        return group.references.some(function (v) {
          const sel = document.getElementById('sel-' + v.connectionReferenceLogicalName);
          if (!sel) { return false; }
          const val = sel.value;
          if (val === KEEP) { return false; }
          if (val === CREATE) { return true; }
          return tail(val) !== tail(v.boundConnectionId);
        });
      }
      const gsel = document.getElementById('gsel-' + index);
      if (!gsel) { return false; }
      const val = gsel.value;
      if (val === KEEP) { return false; }
      if (val === CREATE) { return true; }
      return group.references.some(function (v) { return tail(val) !== tail(v.boundConnectionId); });
    }

    function hasPendingChanges() {
      return groups.some(function (g, i) { return groupHasPending(g, i); });
    }

    function refreshApplyState() {
      applyBtn.disabled = !hasPendingChanges();
    }

    function collectApply() {
      const bindings = [];
      const creates = [];
      groups.forEach(function (group, index) {
        if (individualGroups[group.key]) {
          group.references.forEach(function (v) {
            const sel = document.getElementById('sel-' + v.connectionReferenceLogicalName);
            if (!sel) { return; }
            const val = sel.value;
            if (val === CREATE) {
              creates.push({ connectionReferenceLogicalName: v.connectionReferenceLogicalName, connectorName: v.connectorName || v.connectorId });
              return;
            }
            if (val === KEEP) { return; }
            if (tail(val) === tail(v.boundConnectionId)) { return; }
            const cand = (v.candidates || []).find(function (c) { return c.name === val; });
            bindings.push({ connectionReferenceLogicalName: v.connectionReferenceLogicalName, connectionId: val, connectionDisplayName: cand ? (cand.displayName || undefined) : undefined });
          });
          return;
        }
        const gsel = document.getElementById('gsel-' + index);
        if (!gsel) { return; }
        const val = gsel.value;
        if (val === KEEP) { return; }
        if (val === CREATE) {
          group.references.forEach(function (v) {
            creates.push({ connectionReferenceLogicalName: v.connectionReferenceLogicalName, connectorName: v.connectorName || v.connectorId });
          });
          return;
        }
        const cand = group.candidates.find(function (c) { return c.name === val; });
        group.references.forEach(function (v) {
          if (tail(val) === tail(v.boundConnectionId)) { return; }
          bindings.push({ connectionReferenceLogicalName: v.connectionReferenceLogicalName, connectionId: val, connectionDisplayName: cand ? (cand.displayName || undefined) : undefined });
        });
      });
      return { bindings: bindings, creates: creates };
    }

    function workflowBlockTitle(w) {
      const boundByName = {};
      views.forEach(function (v) { boundByName[v.connectionReferenceLogicalName] = v.boundConnectionExists; });
      const blocking = (w.connectionReferenceLogicalNames || []).filter(function (n) { return !boundByName[n]; });
      if (blocking.length === 0) { return 'Draft — not running'; }
      return 'Waiting on: ' + blocking.join(', ');
    }

    function toggleWorkflow(w) {
      if (w.state !== 2 && !w.canEnable) {
        showBanner('info', 'Connect this workflow to its connections before you can enable it.');
        return;
      }
      vscode.postMessage({ type: 'enableWorkflow', workflowId: w.workflowId, activate: w.state !== 2 });
    }

    function renderAll() {
      buildGroups();
      renderConnections();
    }

    function maybeShowCatalogBanner() {
      if (views.some(function (v) { return v.catalogUnavailable; })) {
        showBanner('warning', "Some connections couldn't be loaded \u2014 the service may be temporarily unavailable. Click Refresh to try again.");
      }
    }

    applyBtn.addEventListener('click', function () {
      const payload = collectApply();
      if (payload.bindings.length === 0 && payload.creates.length === 0) { return; }
      vscode.postMessage({ type: 'apply', bindings: payload.bindings, creates: payload.creates });
    });

    refreshBtn.addEventListener('click', function () {
      vscode.postMessage({ type: 'refresh' });
    });

    addRefBtn.addEventListener('click', function () {
      vscode.postMessage({ type: 'addReference' });
    });

    searchInput.addEventListener('input', function () {
      filterQuery = searchInput.value.trim();
      renderConnections();
    });

    searchInput.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') {
        searchInput.value = '';
        filterQuery = '';
        renderConnections();
      }
    });

    window.addEventListener('message', function (event) {
      const msg = event.data;
      if (msg.type === 'data') {
        views = msg.views || [];
        workflows = msg.workflows || [];
        clearBanner();
        renderAll();
        maybeShowCatalogBanner();
        setBusy(false, '');
      } else if (msg.type === 'busy') {
        setBusy(msg.busy, msg.message);
      } else if (msg.type === 'error') {
        showBanner('error', msg.message);
        setBusy(false, '');
      } else if (msg.type === 'warning') {
        showBanner('warning', msg.message);
      } else if (msg.type === 'info') {
        showBanner('info', msg.message);
      }
    });

    vscode.postMessage({ type: 'ready' });
  </script>
</body>
</html>`;
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}
