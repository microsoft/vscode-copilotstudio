export const deploymentSelectorStyles = `
    .deployment-context { margin-bottom: 40px; }
    .deployment-header { display: flex; align-items: center; justify-content: space-between; gap: 8px; margin-bottom: 8px; }
    .deployment-header h2 { margin: 0; }
    .deployment-context p { margin: 12px 0; overflow-wrap: anywhere; }
    .workspace-action.linkbtn { margin-top: 12px; font-size: inherit; }
    .deployment-badge {
      display: inline-flex; align-items: center; border-radius: 12px; padding: 3px 9px;
      font-size: 0.85em; font-weight: 600;
      color: var(--vscode-foreground);
      background: color-mix(in srgb, var(--vscode-focusBorder) 12%, transparent);
    }
    .deployment-badge.sync {
      color: #ffffff; background: #146c43;
      font-size: 0.95em; line-height: 1.4; padding: 3px 10px;
      outline: 1px solid var(--vscode-contrastBorder, transparent);
    }
    .deployment-badge.ambiguous {
      color: var(--vscode-editorWarning-foreground);
      background: color-mix(in srgb, var(--vscode-editorWarning-foreground) 12%, transparent);
      outline: 1px solid var(--vscode-contrastBorder, transparent);
    }
    .deployment-warning { color: var(--vscode-editorWarning-foreground); }
    .deployment-picker { position: relative; margin-bottom: 10px; }
    .deployment-trigger {
      display: flex; align-items: center; gap: 8px; width: 100%; min-height: 36px;
      padding: 7px 10px; border-radius: 4px; text-align: left;
      color: var(--vscode-dropdown-foreground); background: var(--vscode-dropdown-background);
      border: 1px solid var(--vscode-dropdown-border, var(--vscode-widget-border, transparent));
    }
    .deployment-trigger:hover { background: var(--vscode-dropdown-background); }
    .deployment-trigger:focus-visible { outline: 1px solid var(--vscode-focusBorder); outline-offset: 1px; }
    .deployment-option-label { flex: 0 1 auto; min-width: 0; overflow-wrap: anywhere; }
    .deployment-trigger .deployment-option-label { overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
    .deployment-picker .deployment-badge { flex: none; }
    .deployment-chevron {
      flex: none; width: 6px; height: 6px; margin: -3px 2px 0 auto;
      border-right: 1px solid currentColor; border-bottom: 1px solid currentColor; transform: rotate(45deg);
    }
    .deployment-options {
      position: absolute; top: calc(100% + 4px); left: 0; right: 0; z-index: 3;
      max-height: min(280px, 40vh); overflow-y: auto;
      color: var(--vscode-dropdown-foreground); background: var(--vscode-dropdown-background);
      border: 1px solid var(--vscode-dropdown-border, var(--vscode-widget-border, transparent));
      border-radius: 4px; box-shadow: 0 4px 12px var(--vscode-widget-shadow, rgba(0,0,0,0.25));
    }
    .deployment-option { display: flex; align-items: center; gap: 8px; padding: 8px 10px; cursor: pointer; }
    .deployment-option[aria-selected="true"] { font-weight: 600; }
    .deployment-option.active {
      color: var(--vscode-list-focusForeground, var(--vscode-dropdown-foreground));
      background: var(--vscode-list-focusBackground, var(--vscode-list-hoverBackground));
      outline: 1px solid var(--vscode-list-focusOutline, var(--vscode-focusBorder)); outline-offset: -1px;
    }
    .deployment-option[aria-disabled="true"] { opacity: 0.5; cursor: default; }
`;

export function buildDeploymentSelectorHtml(details = ''): string {
  return `<section id="deployments-section" class="deployment-context hidden" aria-labelledby="deployments-title">
    <div class="deployment-header">
      <h2 id="deployments-title">Deployments</h2>
      <button id="refresh-deployments" class="secondary" title=" Reload deployments" aria-label="Refresh deployments" disabled>Refresh</button>
    </div>
    <div id="deployment-loading" class="section-loading" role="status">
      <span class="spinner" aria-hidden="true"></span>
      <span>Loading deployments…</span>
    </div>
    <div id="deployment-details" class="hidden">
      <div id="deployment-picker" class="deployment-picker hidden">
        <button id="deployment-select" class="deployment-trigger" type="button" role="combobox" aria-label="Deployment" aria-haspopup="listbox" aria-controls="deployment-options" aria-expanded="false" disabled>
          <span id="deployment-selected-label" class="deployment-option-label">Loading…</span>
          <span id="sync-target-badge" class="deployment-badge sync hidden">Attached</span>
          <span class="deployment-chevron" aria-hidden="true"></span>
        </button>
        <div id="deployment-options" class="deployment-options hidden" role="listbox" aria-label="Deployments"></div>
      </div>
      ${details}
      <p id="deployment-info" class="hint hidden" role="status"></p>
      <p id="deployment-warning" class="deployment-warning hidden" role="status"></p>
      <button id="use-workspace-environment" class="linkbtn workspace-action hidden" type="button" disabled>Configure connections in workspace environment</button>
    </div>
  </section>`;
}

export const deploymentSelectorScript = `
    function createDeploymentSelector(options) {
      const select = document.getElementById('deployment-select');
      const picker = document.getElementById('deployment-picker');
      const list = document.getElementById('deployment-options');
      const refresh = document.getElementById('refresh-deployments');
      const useWorkspace = document.getElementById('use-workspace-environment');
      let context;
      let selection = '';
      let busy = false;
      let items = [];
      let activeIndex = -1;
      let menuOpen = false;
      let typeAhead = '';
      let typeAheadTime = 0;
      let restoreFocus = false;

      function workspaceDeployments() {
        return context ? context.deployments.filter(function (deployment) {
          return deployment.environmentId.toLowerCase() === context.environmentId.toLowerCase();
        }) : [];
      }

      function isVisible() {
        return !!context && (context.deployments.length > 1
          || workspaceDeployments().length !== context.deployments.length);
      }

      function label(deployment) {
        return deployment.name + (deployment.type ? ' [' + deployment.type + ']' : '')
          + (deployment.name !== deployment.id ? ' [' + deployment.id + ']' : '');
      }

      function attachmentTitle(ambiguous) {
        return ambiguous ? 'Multiple deployments match the workspace environment.' : 'Workspace sync target, not live traffic status.';
      }

      function closeMenu() {
        menuOpen = false;
        list.classList.toggle('hidden', true);
        select.setAttribute('aria-expanded', 'false');
        select.removeAttribute('aria-activedescendant');
        typeAhead = '';
      }

      function setLoading(loading) {
        document.getElementById('deployments-section').classList.toggle('hidden', !isVisible());
        document.getElementById('deployment-loading').classList.toggle('hidden', !loading);
        document.getElementById('deployment-details').classList.toggle('hidden', loading || !context);
      }

      function setActive(index) {
        activeIndex = index;
        Array.from(list.children).forEach(function (option, optionIndex) {
          option.classList.toggle('active', optionIndex === index);
        });
        const option = list.children[index];
        select.setAttribute('aria-activedescendant', option.id);
        option.scrollIntoView({ block: 'nearest' });
      }

      function openMenu() {
        if (busy || select.disabled || menuOpen || !items.some(function (item) { return !item.disabled; })) { return; }
        menuOpen = true;
        list.classList.toggle('hidden', false);
        select.setAttribute('aria-expanded', 'true');
        let index = items.findIndex(function (item) { return item.value === selection && !item.disabled; });
        if (index < 0) { index = items.findIndex(function (item) { return !item.disabled; }); }
        setActive(index);
      }

      function moveActive(step) {
        for (let count = 0; count < items.length; count++) {
          activeIndex = (activeIndex + step + items.length) % items.length;
          if (!items[activeIndex].disabled) { setActive(activeIndex); return; }
        }
      }

      function requestSelection(next) {
        if (busy || next === selection) { return; }
        restoreFocus = true;
        options.onSelect(next);
      }

      function choose(index) {
        const item = items[index];
        if (busy || !item || item.disabled) { return; }
        closeMenu();
        select.focus();
        requestSelection(item.value);
      }

      function setContext(nextContext, nextSelection) {
        context = nextContext;
        selection = !isVisible() ? '' : nextSelection === undefined ? context.currentDeploymentId || '' : nextSelection;
        closeMenu();
        const matching = workspaceDeployments();
        items = context.deployments.map(function (deployment) {
          return {
            value: deployment.id, label: label(deployment),
            attached: matching.some(function (item) { return item.id === deployment.id; }),
            ambiguous: matching.length > 1
          };
        }).sort(function (left, right) { return Number(right.attached) - Number(left.attached); });
        if (selection && !context.deployments.some(function (deployment) { return deployment.id === selection; })) {
          items.push({ value: selection, label: 'Unavailable deployment [' + selection + ']', disabled: true });
        }
        list.replaceChildren();
        items.forEach(function (item, index) {
          const option = document.createElement('div');
          option.id = 'deployment-option-' + index;
          option.className = 'deployment-option';
          option.setAttribute('role', 'option');
          option.setAttribute('aria-selected', String(item.value === selection));
          if (item.disabled) { option.setAttribute('aria-disabled', 'true'); }
          option.title = item.label;
          const text = document.createElement('span');
          text.className = 'deployment-option-label';
          text.textContent = item.label;
          option.appendChild(text);
          if (item.attached) {
            const badge = document.createElement('span');
            badge.className = 'deployment-badge ' + (item.ambiguous ? 'ambiguous' : 'sync');
            badge.textContent = item.ambiguous ? 'Attached \\u26a0' : 'Attached';
            badge.title = attachmentTitle(item.ambiguous);
            option.appendChild(badge);
          }
          option.addEventListener('mousedown', function (event) { event.preventDefault(); });
          option.addEventListener('pointermove', function () {
            if (menuOpen && !item.disabled) { setActive(index); }
          });
          option.addEventListener('click', function () { choose(index); });
          list.appendChild(option);
        });
        select.value = selection;
        select.disabled = busy;
        const selected = items.find(function (item) { return item.value === selection; });
        select.title = selected ? selected.label : 'Select a deployment…';
        document.getElementById('deployment-selected-label').textContent = select.title;
        const current = isVisible() ? items.find(function (item) { return item.value === context.currentDeploymentId; }) : undefined;
        const attached = selected ? selected.attached : !selection && current && current.attached;
        const attachmentLabel = matching.length > 1 ? 'Attached \\u26a0' : 'Attached';
        const badge = document.getElementById('sync-target-badge');
        badge.textContent = attachmentLabel;
        badge.className = 'deployment-badge ' + (matching.length > 1 ? 'ambiguous' : 'sync');
        badge.title = attachmentTitle(matching.length > 1);
        badge.classList.toggle('hidden', !attached);
        select.setAttribute('aria-label', 'Deployment: ' + select.title + (attached ? ', ' + attachmentLabel : ''));
        useWorkspace.classList.toggle('hidden', !isVisible() || matching.length > 0 || !selection);
        picker.classList.toggle('hidden', !isVisible());
        options.onChange(context, selection);
        setLoading(false);
      }

      function setBusy(value, loading) {
        busy = value;
        if (busy) { closeMenu(); }
        if (loading) { setLoading(true); }
        else if (!busy) { setLoading(false); }
        select.disabled = busy;
        refresh.disabled = busy;
        useWorkspace.disabled = busy;
        if (!busy && restoreFocus) {
          restoreFocus = false;
          if (document.activeElement === document.body) { select.focus(); }
        }
        ['info', 'warning'].forEach(function (kind) {
          const message = document.getElementById('deployment-' + kind);
          message.textContent = !busy && isVisible() ? context[kind + 'Message'] || '' : '';
          message.classList.toggle('hidden', !message.textContent);
        });
      }

      select.addEventListener('click', function () {
        if (menuOpen) { closeMenu(); } else { openMenu(); }
      });
      select.addEventListener('keydown', function (event) {
        if (busy || select.disabled || event.ctrlKey || event.metaKey || event.isComposing) { return; }
        const key = event.key;
        if (key === 'Escape' || key === 'Tab' || (event.altKey && key === 'ArrowUp')) {
          if (menuOpen && key !== 'Tab') { event.preventDefault(); }
          closeMenu();
        } else if (key === 'Enter' || key === ' ') {
          event.preventDefault();
          if (menuOpen) { choose(activeIndex); } else { openMenu(); }
        } else if (key === 'ArrowDown' || key === 'ArrowUp') {
          event.preventDefault();
          if (menuOpen) { moveActive(key === 'ArrowDown' ? 1 : -1); } else { openMenu(); }
        } else if (key === 'Home' || key === 'End') {
          event.preventDefault();
          openMenu();
          activeIndex = key === 'Home' ? -1 : items.length;
          moveActive(key === 'Home' ? 1 : -1);
        } else if (key.length === 1 && !event.altKey) {
          event.preventDefault();
          openMenu();
          const now = Date.now();
          typeAhead = now - typeAheadTime > 700 ? key : typeAhead + key;
          typeAheadTime = now;
          if (Array.from(typeAhead).every(function (character) { return character === key; })) { typeAhead = key; }
          const prefix = typeAhead.toLowerCase();
          const start = activeIndex + (prefix.length === 1 ? 1 : 0);
          for (let count = 0; count < items.length; count++) {
            const index = (start + count) % items.length;
            if (!items[index].disabled && items[index].label.toLowerCase().startsWith(prefix)) { setActive(index); break; }
          }
        }
      });
      document.addEventListener('mousedown', function (event) {
        if (!picker.contains(event.target)) { closeMenu(); }
      });
      picker.addEventListener('focusout', function (event) {
        if (!picker.contains(event.relatedTarget)) { closeMenu(); }
      });
      useWorkspace.addEventListener('click', function () {
        if (workspaceDeployments().length === 0) { requestSelection(''); }
      });
      refresh.addEventListener('click', function () { if (!busy) { options.onRefresh(); } });

      return {
        setContext: setContext, setBusy: setBusy, isVisible: isVisible,
        cancelSelection: function () { select.value = selection; },
        get context() { return context; },
        get selection() { return selection; }
      };
    }
`;
