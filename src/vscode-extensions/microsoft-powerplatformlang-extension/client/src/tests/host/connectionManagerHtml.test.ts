import * as assert from 'node:assert/strict';
import { test } from 'node:test';
import { runInContext } from 'node:vm';
import { syncInfo } from './helpers/deploymentFixtures';
import { Element } from './helpers/dom';
import { createConnectionManagerView } from './helpers/connectionManagerView';

test('keeps environment identity in connections and scope guidance independent of the selected environment', () => {
  const { element, onMessage } = createConnectionManagerView();
  const context = {
    ...syncInfo,
    environmentName: 'Test Environment',
    deployments: [
      { id: 'dev', name: 'develop', environmentId: 'dev-environment', environmentName: 'Development Environment' },
      { id: 'prod', name: 'production', environmentId: 'prod-environment', environmentName: 'Production Environment' }
    ]
  };
  const expected = 'Deployment selection only affects the connections configured below. Sync operations (Pull, Push, and Preview) always use the workspace environment.';
  onMessage({ data: { type: 'context', context } });
  assert.equal(element('deployment-select').title, 'Select a deployment…');
  assert.equal(element('workspace-target').textContent, expected);
  assert.equal(element('connected-environment').textContent, ' \u2014 Test Environment [test-environment]');
  assert.ok(element('sync-target-badge').hiddenClasses.has('hidden'));

  onMessage({ data: { type: 'context', context, selectedDeploymentId: 'dev' } });
  assert.equal(element('connections-title').textContent, 'Connections in develop');
  assert.equal(element('workspace-target').textContent, expected);
  assert.equal(element('connected-environment').textContent, ' \u2014 Development Environment [dev-environment]');
  assert.ok(element('sync-target-badge').hiddenClasses.has('hidden'));

  onMessage({ data: { type: 'context', context, selectedDeploymentId: '' } });
  assert.equal(element('connected-environment').textContent, ' \u2014 Test Environment [test-environment]');
  assert.equal(element('workspace-target').textContent, expected);

  onMessage({ data: {
    type: 'context', selectedDeploymentId: 'dev',
    context: { ...context, deployments: context.deployments.map(deployment => ({ ...deployment, environmentName: undefined })) }
  } });
  assert.equal(element('connected-environment').textContent, ' \u2014 dev-environment');
});

test('shows deployment-scoped connections and attached status, hiding only zero or sole attached choices', () => {
  const { element, messages, html, sandbox, onMessage } = createConnectionManagerView();
  assert.ok(element('deployments-section').hiddenClasses.has('hidden'), 'the section stays hidden until deployment choices are known');
  assert.ok(!element('deployment-loading').hiddenClasses.has('hidden'));
  assert.ok(element('deployment-details').hiddenClasses.has('hidden'), 'no deployment information is shown before config and names load');
  assert.equal(element('refresh-deployments').disabled, true);
  assert.ok(!element('connection-loading').hiddenClasses.has('hidden'));
  assert.ok(element('content').hiddenClasses.has('hidden'));
  assert.equal(element('loading-message').textContent, 'Loading connections…');
  assert.equal(element('status').textContent, '');
  const context = {
      ...syncInfo,
      environmentName: 'Test Environment',
      accountEmail: syncInfo.accountInfo.accountEmail,
      deployments: [
        { id: 'dev', name: 'develop', type: 'dev', environmentId: 'dev-environment', environmentName: 'Development Environment', enabled: true },
        { id: 'test', name: '<testdeploy>', type: 'test', environmentId: 'test-environment', environmentName: 'Test Environment', enabled: true }
      ],
      currentDeploymentId: 'test'
    };
  onMessage({ data: { type: 'context', context } });
  assert.ok(!element('deployments-section').hiddenClasses.has('hidden'));
  assert.equal(element('connected-environment').textContent, ' \u2014 Test Environment [test-environment]');
  assert.ok(element('deployment-loading').hiddenClasses.has('hidden'));
  assert.ok(!element('deployment-details').hiddenClasses.has('hidden'));
  onMessage({ data: { type: 'data', views: [], workflows: [] } });
  assert.equal(element('deployment-select').value, 'test', 'landing selects the connected deployment, not the first option');
  assert.equal(element('deployment-select').title, '<testdeploy> [test] [test]');
  assert.equal(element('connections-title').textContent, 'Connections in <testdeploy> [test]');
  const syncTargetMessage = 'Deployment selection only affects the connections configured below. Sync operations (Pull, Push, and Preview) always use the workspace environment.';
  assert.equal(element('workspace-target').textContent, syncTargetMessage);
  assert.equal(element('connection-scope').textContent, 'This deployment targets the workspace environment. Connection changes here also apply to the workspace.');
  assert.ok(!element('connection-scope').hiddenClasses.has('hidden'));
  assert.ok(!element('sync-target-badge').hiddenClasses.has('hidden'));
  assert.ok(element('connection-loading').hiddenClasses.has('hidden'));
  assert.ok(!element('content').hiddenClasses.has('hidden'));
  const options = element('deployment-options').children;
  assert.equal(options[0].children[0].textContent, '<testdeploy> [test] [test]');
  assert.equal(options[1].children[0].textContent, 'develop [dev] [dev]');
  assert.doesNotMatch(html, /Deployment \(preview\)|Preview only|All deployments|Back to workspace|<details>|<summary>|selected-target-badge|connection-dataverse|connection-account|Signed-in account|Agent Changes target|deployment-footer|addRef-help|apply-help/);
  assert.equal((html.match(/<h1\b/g) || []).length, 1);
  assert.doesNotMatch(html, /deployment-card-title|<label for="deployment-select">/);
  assert.equal((html.match(/<h2 id="deployments-title">Deployments<\/h2>/g) || []).length, 1);
  assert.doesNotMatch(html, /deployment-type/);
  assert.doesNotMatch(html, /retry-deployments|deployment-availability-note|Could not check|Retry checks|Environment name unavailable/);
  assert.doesNotMatch(html, /deployment-heading|class="detail-label">Deployment:/);
  assert.match(html, /id="deployment-select"[^>]*role="combobox"/);
  assert.match(html, /id="sync-target-badge"[^>]*>Attached<\/span>/);
  assert.doesNotMatch(html, /id="connection-target"|class="detail-label">Environment:</);
  assert.match(html, /id="deployment-options"[^>]*role="listbox"/);
  assert.match(html, /id="apply"[^>]* disabled>Apply</);
  assert.match(html, /id="refresh"[^>]*title="Reload connections\./);
  const toolbar = /<div class="toolbar">([\s\S]*?)<\/div>/.exec(html)?.[1];
  assert.ok(toolbar);
  assert.deepEqual(Array.from(toolbar.matchAll(/<button id="([^"]+)"/g), match => match[1]), ['apply', 'refresh', 'addRef']);
  assert.ok(toolbar.indexOf('id="addRef"') < toolbar.indexOf('id="status"'));
  assert.ok(toolbar.indexOf('id="status"') < toolbar.indexOf('id="search"'));
  assert.match(toolbar, /<button id="addRef"[^>]* disabled>Add connection reference<\/button>/);
  assert.doesNotMatch(html, /add-row/);
  assert.ok(html.indexOf('id="workspace-target"') < html.indexOf('id="use-workspace-environment"'));
  assert.match(html, /\.workspace-action\.linkbtn \{ margin-top: 12px; font-size: inherit; \}/);
  assert.doesNotMatch(html, /id="unsaved-changes"/);
  assert.doesNotMatch(html, /<h2[^>]*>Connections/);
  const connectionsSection = html.slice(html.indexOf('<section id="connection-management"'));
  assert.ok(html.indexOf('id="deployment-warning"') < html.indexOf('<section id="connection-management"'));
  assert.doesNotMatch(connectionsSection, /id="connection-warning"/, 'deployment warnings stay outside the connections section');
  for (const id of ['connections-title', 'apply', 'addRef', 'refresh']) {
    assert.ok(connectionsSection.includes('id="' + id + '"'), id + ' belongs in the connections section');
  }
  assert.ok(connectionsSection.indexOf('connections-title') < connectionsSection.indexOf('id="workspace-name"'));

  element('refresh-deployments').fire('click');
  assert.equal(messages.at(-1)?.type, 'refreshDeployments');
  assert.equal(messages.at(-1)?.hasPendingChanges, false);
  onMessage({ data: { type: 'busy', busy: true, loadingDeployments: true, loadingConnections: true } });
  assert.ok(!element('deployment-loading').hiddenClasses.has('hidden'));
  assert.ok(element('deployment-details').hiddenClasses.has('hidden'), 'a config refresh hides all stale deployment details');
  assert.equal(element('refresh-deployments').disabled, true);
  onMessage({ data: { type: 'context', context } });
  assert.ok(element('deployment-loading').hiddenClasses.has('hidden'));
  assert.ok(!element('deployment-details').hiddenClasses.has('hidden'));
  onMessage({ data: { type: 'data', views: [], workflows: [] } });
  assert.equal(element('refresh-deployments').disabled, false);

  element('content').appendChild(new Element());
  element('refresh').fire('click');
  assert.equal(messages.at(-1)?.type, 'refresh');
  assert.equal(element('content').children.length, 0);
  assert.ok(element('content').hiddenClasses.has('hidden'));
  assert.ok(!element('connection-loading').hiddenClasses.has('hidden'));
  onMessage({ data: { type: 'busy', busy: true, message: 'Loading connections…', loadingConnections: true } });
  assert.equal(element('loading-message').textContent, 'Loading connections…');
  assert.equal(element('status').textContent, '');
  onMessage({ data: { type: 'data', views: [], workflows: [] } });
  assert.ok(element('connection-loading').hiddenClasses.has('hidden'));
  assert.ok(!element('content').hiddenClasses.has('hidden'));

  runInContext("groups = [{ key: 'connector', references: [{ boundConnectionId: 'old' }] }]; views = groups[0].references;", sandbox);
  element('gsel-0').value = '__create__';
  assert.equal(element('apply').disabled, false);
  element('content').appendChild(new Element());
  element('refresh-deployments').fire('click');
  assert.equal(messages.at(-1)?.type, 'refreshDeployments');
  assert.equal(messages.at(-1)?.hasPendingChanges, true);
  onMessage({ data: { type: 'selectionCancelled' } });
  assert.equal(element('gsel-0').value, '__create__');
  assert.equal(element('content').children.length, 1, 'cancelling a config refresh preserves unsaved controls');
  assert.ok(!element('deployment-details').hiddenClasses.has('hidden'));
  assert.ok(element('deployment-loading').hiddenClasses.has('hidden'));
  element('deployment-options').children[1].fire('click');
  assert.equal(messages.at(-1)?.type, 'selectDeployment');
  assert.equal(messages.at(-1)?.deploymentId, 'dev');
  assert.equal(messages.at(-1)?.hasPendingChanges, true);
  assert.equal(element('deployment-select').disabled, true);
  assert.equal(element('deployment-select').value, 'test', 'old target remains visible until switching is confirmed');
  assert.equal(element('deployment-select').title, '<testdeploy> [test] [test]');
  assert.equal(element('connections-title').textContent, 'Connections in <testdeploy> [test]');

  onMessage({ data: { type: 'selectionCancelled' } });
  assert.equal(element('deployment-select').value, 'test');
  assert.equal(element('gsel-0').value, '__create__');
  assert.equal(element('content').children.length, 1, 'staying preserves pending controls');
  assert.equal(element('apply').disabled, false);
  assert.ok(element('connection-loading').hiddenClasses.has('hidden'), 'a discard prompt does not replace the connection editor');

  element('deployment-options').children[1].fire('click');
  onMessage({ data: { type: 'context', context, selectedDeploymentId: 'dev' } });
  assert.equal(element('connected-environment').textContent, ' \u2014 Development Environment [dev-environment]', 'the connections subtitle follows the selected deployment environment');
  assert.equal(element('content').children.length, 0, 'old connections are removed before the selected target is loaded');
  assert.equal(element('deployment-select').title, 'develop [dev] [dev]');
  assert.equal(element('workspace-target').textContent, syncTargetMessage);
  assert.equal(element('connection-scope').textContent, '');
  assert.ok(element('connection-scope').hiddenClasses.has('hidden'));
  assert.ok(!element('connection-loading').hiddenClasses.has('hidden'));
  assert.equal(element('connections-title').textContent, 'Connections in develop [dev]');
  assert.ok(element('sync-target-badge').hiddenClasses.has('hidden'));
  assert.equal(element('apply').disabled, true);
  assert.ok(!element('addRef').hiddenClasses.has('hidden'));
  onMessage({ data: { type: 'error', message: 'Access denied', loadFailed: true } });
  assert.equal(element('apply').disabled, false);
  assert.equal(element('addRef').disabled, false);
  assert.equal(element('deployment-select').disabled, false, 'another deployment can still be selected after an error');
  assert.ok(element('connection-loading').hiddenClasses.has('hidden'));
  assert.equal(element('content').children.length, 0);
  assert.equal(element('connections-title').textContent, 'Connections in develop [dev]', 'failed loads keep the selected target explicit');
  const before = messages.length;
  element('apply').fire('click');
  assert.equal(messages.at(-1)?.type, 'apply');
  assert.equal(element('banner').textContent, 'Access denied', 'Apply must not clear a connection-load error while awaiting the controller');
  onMessage({ data: { type: 'error', message: 'Access denied', loadFailed: true } });
  element('addRef').fire('click');
  assert.equal(messages.length, before + 2, 'buttons remain actionable after load errors; the controller rejects unsafe operations');
  assert.equal(element('banner').textContent, 'Access denied');

  onMessage({ data: { type: 'data', views: [], workflows: [] } });
  assert.equal(element('addRef').disabled, false, 'an empty remote deployment still allows adding a reference');
  element('addRef').fire('click');
  assert.equal(messages.at(-1)?.type, 'addReference');
  runInContext("groups = [{ key: 'connector', connectorName: 'shared_test', references: [{ connectionReferenceLogicalName: 'remote_ref', boundConnectionId: 'old', connectorName: 'shared_test' }], candidates: [] }]; views = groups[0].references;", sandbox);
  element('gsel-0').value = '__create__';
  element('apply').fire('click');
  assert.equal(messages.at(-1)?.type, 'apply', 'another deployment has a functioning binding editor, not a preview');
  assert.equal(element('status').textContent, 'Applying connection bindings…');
  assert.equal(element('deployment-select').title, 'develop [dev] [dev]');

  element('content').appendChild(new Element());
  onMessage({ data: { type: 'context', context: { ...syncInfo, deployments: [], warningMessage: 'Deployment unavailable' } } });
  assert.ok(element('deployments-section').hiddenClasses.has('hidden'));
  assert.equal(element('connected-environment').textContent, ' \u2014 test-environment');
  assert.equal(element('deployment-select').title, 'Select a deployment…');
  assert.equal(element('connections-title').textContent, 'Connections');
  assert.equal(element('workspace-target').textContent, syncTargetMessage.trimEnd());
  assert.ok(element('sync-target-badge').hiddenClasses.has('hidden'));
  assert.equal(element('deployment-warning').textContent, '', 'warnings are hidden while connections load');
  assert.equal(element('content').children.length, 0);
  assert.ok(element('deployment-picker').hiddenClasses.has('hidden'));
  onMessage({ data: { type: 'data', views: [], workflows: [] } });
  assert.equal(element('deployment-warning').textContent, '');
  assert.ok(element('deployment-warning').hiddenClasses.has('hidden'), 'zero deployments do not show a deployment warning');

  onMessage({ data: { type: 'context', context: { ...syncInfo, deployments: [] }, selectedDeploymentId: 'dev' } });
  assert.equal(element('deployment-select').title, 'Select a deployment…');
  assert.equal(element('connections-title').textContent, 'Connections');
  assert.equal(element('deployment-select').value, '');
  assert.ok(element('deployment-picker').hiddenClasses.has('hidden'));
  assert.ok(element('deployments-section').hiddenClasses.has('hidden'), 'removing config cannot leave an invisible remote target selected');

  onMessage({ data: { type: 'context', context: { ...context, currentDeploymentId: undefined, deployments: [context.deployments[0]] } } });
  assert.equal(element('deployment-select').value, '');
  assert.equal(element('deployment-select').title, 'Select a deployment…');
  assert.ok(!element('deployments-section').hiddenClasses.has('hidden'), 'a sole nonattached deployment offers a choice while initially retaining workspace connections');
  assert.ok(!element('deployment-picker').hiddenClasses.has('hidden'));
  assert.equal(element('deployment-options').children.length, 1);

  onMessage({ data: { type: 'context', context: { ...context, deployments: [context.deployments[1]], warningMessage: 'Deployment warning' } } });
  onMessage({ data: { type: 'data', views: [], workflows: [] } });
  assert.equal(element('deployment-select').title, 'Select a deployment…');
  assert.equal(element('connections-title').textContent, 'Connections in <testdeploy> [test]', 'a known attached slot names the heading even when its dropdown is hidden');
  assert.ok(element('deployments-section').hiddenClasses.has('hidden'));
  assert.ok(element('deployment-picker').hiddenClasses.has('hidden'));
  assert.equal(element('workspace-target').textContent, syncTargetMessage.trimEnd());
  assert.ok(element('sync-target-badge').hiddenClasses.has('hidden'));
  assert.equal(element('deployment-warning').textContent, '');
  assert.ok(element('deployment-warning').hiddenClasses.has('hidden'), 'a sole attached deployment does not show a deployment warning');

  const third = { id: 'prod', name: 'production', type: 'prod', environmentId: 'prod-environment', environmentName: 'Production Environment', enabled: true };
  onMessage({ data: { type: 'context', context: { ...context, deployments: [...context.deployments, third] } } });
  onMessage({ data: { type: 'data', views: [], workflows: [] } });
  assert.equal(element('deployment-options').children.length, 3);
  assert.ok(!element('deployment-picker').hiddenClasses.has('hidden'));
  assert.ok(!element('deployments-section').hiddenClasses.has('hidden'), 'adding more deployments shows the section again');
  element('deployment-options').children[2].fire('click');
  assert.equal(messages.at(-1)?.deploymentId, 'prod');
  onMessage({ data: { type: 'context', context: { ...context, deployments: [...context.deployments, third] }, selectedDeploymentId: 'prod' } });
  assert.equal(element('deployment-select').title, 'production [prod] [prod]');
  assert.equal(element('connections-title').textContent, 'Connections in production [prod]');
  assert.equal(element('connected-environment').textContent, ' \u2014 Production Environment [prod-environment]');
  assert.ok(element('sync-target-badge').hiddenClasses.has('hidden'));
});

const dropdownContext = {
  ...syncInfo, environmentName: 'Test Environment', currentDeploymentId: 'test',
  deployments: [
    { id: 'dev', name: 'develop', type: 'dev', environmentId: 'dev-environment' },
    { id: 'test', name: '<testdeploy>', type: 'test', environmentId: syncInfo.environmentId },
    { id: 'prod', name: 'production', type: 'prod', environmentId: 'prod-environment' }
  ]
};

test('deployment information matches detail text while warnings retain their styling', () => {
  const { element, onMessage, html } = createConnectionManagerView();
  const infoMessage = 'No deployment targets the workspace environment.';
  const warningMessage = 'Multiple deployments target the workspace environment.';
  assert.match(html, /<p id="workspace-target" class="hint"><\/p>/);
  assert.match(html, /<p id="connection-scope" class="hint hidden"><\/p>/);
  assert.match(html, /<p id="deployment-info" class="hint hidden" role="status"><\/p>/);
  assert.doesNotMatch(html, /workspace-match/);
  assert.match(html, /<p id="deployment-warning" class="deployment-warning hidden" role="status"><\/p>/);
  for (const [context, expectedInfo, expectedWarning] of [
    [{ ...dropdownContext, currentDeploymentId: undefined, deployments: [dropdownContext.deployments[0]], infoMessage }, infoMessage, ''],
    [{ ...dropdownContext, warningMessage }, '', warningMessage],
    [{ ...dropdownContext, deployments: [], infoMessage }, '', ''],
    [{ ...dropdownContext, deployments: [dropdownContext.deployments[1]], infoMessage }, '', '']
  ] as const) {
    onMessage({ data: { type: 'context', context } });
    onMessage({ data: { type: 'data', views: [], workflows: [] } });
    assert.equal(element('deployment-info').textContent, expectedInfo);
    assert.equal(element('deployment-info').hiddenClasses.has('hidden'), !expectedInfo);
    assert.equal(element('deployment-warning').textContent, expectedWarning);
    assert.equal(element('deployment-warning').hiddenClasses.has('hidden'), !expectedWarning);
    onMessage({ data: { type: 'busy', busy: true, loadingDeployments: true } });
    assert.ok(element('deployment-info').hiddenClasses.has('hidden'));
    assert.ok(element('deployment-warning').hiddenClasses.has('hidden'));
  }
});

test('workspace fallback stays outside the deployment dropdown and protects pending edits when returning', () => {
  const { element, messages, sandbox, onMessage } = createConnectionManagerView();
  const context = { ...dropdownContext, currentDeploymentId: undefined, deployments: dropdownContext.deployments.filter(deployment => deployment.id !== 'test') };
  const workspaceButton = element('use-workspace-environment');
  const trigger = element('deployment-select');
  const load = (selectedDeploymentId: string) => {
    onMessage({ data: { type: 'context', context, selectedDeploymentId } });
    onMessage({ data: { type: 'data', views: [], workflows: [] } });
  };
  load('');
  assert.equal(element('deployment-selected-label').textContent, 'Select a deployment…');
  assert.equal(trigger.getAttribute('aria-label'), 'Deployment: Select a deployment…');
  assert.equal(element('connections-title').textContent, 'Connections');
  assert.ok(workspaceButton.hiddenClasses.has('hidden'));
  const options = element('deployment-options').children;
  assert.equal(options.length, context.deployments.length);
  assert.ok(options.every(option => option.getAttribute('aria-selected') === 'false'));
  trigger.fire('keydown', { key: 'Enter' });
  assert.equal(trigger.getAttribute('aria-activedescendant'), options[0].id);
  trigger.fire('keydown', { key: 'Enter' });
  assert.equal(messages.at(-1)?.deploymentId, 'dev', 'the placeholder is not a selectable slot');

  load('dev');
  assert.ok(!workspaceButton.hiddenClasses.has('hidden'));
  runInContext("groups = [{ key: 'connector', references: [{ boundConnectionId: 'old' }] }]; views = groups[0].references;", sandbox);
  element('gsel-0').value = '__create__';
  element('content').appendChild(new Element());
  workspaceButton.fire('click');
  assert.equal(messages.at(-1)?.type, 'selectDeployment');
  assert.equal(messages.at(-1)?.deploymentId, '');
  assert.equal(messages.at(-1)?.hasPendingChanges, true);
  assert.equal(workspaceButton.disabled, true);
  const messageCount = messages.length;
  workspaceButton.fire('click');
  assert.equal(messages.length, messageCount, 'returning cannot race an operation');
  onMessage({ data: { type: 'selectionCancelled' } });
  assert.equal(workspaceButton.disabled, false);
  assert.ok(!workspaceButton.hiddenClasses.has('hidden'));
  assert.equal(trigger.value, 'dev');
  assert.equal(element('gsel-0').value, '__create__');
  assert.equal(element('content').children.length, 1);
  assert.ok(element('connection-loading').hiddenClasses.has('hidden'));

  workspaceButton.fire('click');
  load('');
  assert.equal(trigger.value, '');
  assert.ok(workspaceButton.hiddenClasses.has('hidden'));
  assert.equal(element('connections-title').textContent, 'Connections');
  onMessage({ data: { type: 'context', context: dropdownContext, selectedDeploymentId: 'dev' } });
  onMessage({ data: { type: 'data', views: [], workflows: [] } });
  assert.ok(workspaceButton.hiddenClasses.has('hidden'), 'a matching deployment in the dropdown replaces the workspace action');
  const countBeforeHiddenAction = messages.length;
  workspaceButton.fire('click');
  assert.equal(messages.length, countBeforeHiddenAction, 'the hidden workspace action cannot bypass deployment selection');
  element('deployment-options').children[0].fire('click');
  assert.equal(messages.at(-1)?.type, 'selectDeployment');
  assert.equal(messages.at(-1)?.deploymentId, 'test');
  assert.equal(messages.at(-1)?.hasPendingChanges, false);
  onMessage({ data: { type: 'context', context: dropdownContext, selectedDeploymentId: 'test' } });
  onMessage({ data: { type: 'data', views: [], workflows: [] } });
  assert.equal(trigger.value, 'test');
  assert.equal(element('deployment-selected-label').textContent, '<testdeploy> [test] [test]');
  assert.ok(!element('sync-target-badge').hiddenClasses.has('hidden'));
  assert.ok(workspaceButton.hiddenClasses.has('hidden'));
});

test('custom dropdown retains Attached styling and commits keyboard choices only on confirmation', () => {
  const { element, messages, onMessage } = createConnectionManagerView();
  onMessage({ data: { type: 'context', context: dropdownContext } });
  onMessage({ data: { type: 'data', views: [], workflows: [] } });
  const trigger = element('deployment-select');
  const options = element('deployment-options').children;
  const key = (value: string) => trigger.fire('keydown', { key: value });
  assert.deepEqual(options.map(option => option.children[0].textContent),
    ['<testdeploy> [test] [test]', 'develop [dev] [dev]', 'production [prod] [prod]'],
    'the matching deployment comes first and the other deployments retain their order');
  assert.deepEqual(dropdownContext.deployments.map(deployment => deployment.id), ['dev', 'test', 'prod'], 'rendering does not reorder the configuration');
  assert.equal(element('deployment-selected-label').textContent, '<testdeploy> [test] [test]');
  assert.equal(trigger.getAttribute('aria-label'), 'Deployment: <testdeploy> [test] [test], Attached');
  assert.match(trigger.getAttribute('aria-label') ?? '', /Attached/);
  assert.equal(options[0].children[1].textContent, 'Attached');
  assert.equal(options[0].children[1].className, 'deployment-badge sync');
  assert.equal(options[1].children.length, 1, 'non-attached options have no badge');
  assert.equal(options[0].getAttribute('aria-selected'), 'true');

  key('Enter');
  assert.equal(trigger.getAttribute('aria-expanded'), 'true');
  assert.equal(trigger.getAttribute('aria-activedescendant'), options[0].id);
  key('ArrowDown');
  assert.equal(trigger.getAttribute('aria-activedescendant'), options[1].id);
  assert.equal(trigger.value, 'test');
  assert.equal(messages.length, 1, 'highlighting an option does not change connections');
  key('Escape');
  assert.equal(trigger.getAttribute('aria-expanded'), 'false');
  assert.equal(trigger.getAttribute('aria-activedescendant'), null);
  key(' ');
  key('Home');
  assert.equal(trigger.getAttribute('aria-activedescendant'), options[0].id);
  key('End');
  assert.equal(trigger.getAttribute('aria-activedescendant'), options[2].id);
  key('ArrowDown');
  assert.equal(trigger.getAttribute('aria-activedescendant'), options[0].id);
  key('ArrowUp');
  assert.equal(trigger.getAttribute('aria-activedescendant'), options[2].id);
  key('d');
  key('e');
  assert.equal(trigger.getAttribute('aria-activedescendant'), options[1].id, 'typing matches option labels');
  key('Escape');
  key('Enter');
  key('p');
  key('Enter');
  assert.equal(messages.at(-1)?.deploymentId, 'prod');
  assert.equal(messages.at(-1)?.hasPendingChanges, false);
  assert.equal(trigger.value, 'test', 'the selected value remains until the controller confirms the switch');
  assert.equal(trigger.getAttribute('aria-expanded'), 'false');
  assert.equal(trigger.disabled, true);
  assert.equal(trigger.focusCount, 1);
  onMessage({ data: { type: 'selectionCancelled' } });
  assert.equal(trigger.value, 'test');
  assert.equal(trigger.focusCount, 2, 'focus returns after cancellation re-enables the trigger');
  assert.ok(!element('sync-target-badge').hiddenClasses.has('hidden'));
  onMessage({ data: { type: 'context', context: dropdownContext, selectedDeploymentId: 'prod' } });
  onMessage({ data: { type: 'data', views: [], workflows: [] } });
  assert.equal(element('deployment-selected-label').textContent, 'production [prod] [prod]');
  assert.ok(element('sync-target-badge').hiddenClasses.has('hidden'));
  assert.equal(element('deployment-options').children[0].children[1].textContent, 'Attached', 'the attached badge remains first in the list when another deployment is selected');
});

test('custom dropdown dismisses without switching, blocks busy or unavailable options and preserves discard confirmation', () => {
  const { element, messages, sandbox, onMessage, documentEvents } = createConnectionManagerView();
  onMessage({ data: { type: 'context', context: dropdownContext } });
  onMessage({ data: { type: 'data', views: [], workflows: [] } });
  const trigger = element('deployment-select');
  const picker = element('deployment-picker');
  trigger.fire('click');
  documentEvents.fire('mousedown', { target: trigger });
  assert.equal(trigger.getAttribute('aria-expanded'), 'true');
  documentEvents.fire('mousedown', { target: new Element() });
  assert.equal(trigger.getAttribute('aria-expanded'), 'false');
  trigger.fire('click');
  picker.fire('focusout', { relatedTarget: trigger });
  assert.equal(trigger.getAttribute('aria-expanded'), 'true');
  picker.fire('focusout', { relatedTarget: new Element() });
  assert.equal(trigger.getAttribute('aria-expanded'), 'false');
  trigger.fire('click');
  let prevented = false;
  trigger.fire('keydown', { key: 'Tab', preventDefault: () => { prevented = true; } });
  assert.equal(prevented, false, 'Tab keeps its normal focus navigation');
  assert.equal(trigger.getAttribute('aria-expanded'), 'false');
  assert.equal(messages.length, 1, 'dismissal never changes connections');

  runInContext("groups = [{ key: 'connector', references: [{ boundConnectionId: 'old' }] }];", sandbox);
  element('gsel-0').value = '__create__';
  trigger.fire('click');
  element('deployment-options').children[1].fire('click');
  assert.equal(messages.at(-1)?.deploymentId, 'dev');
  assert.equal(messages.at(-1)?.hasPendingChanges, true);
  onMessage({ data: { type: 'selectionCancelled' } });
  assert.equal(element('gsel-0').value, '__create__');
  assert.equal(trigger.value, 'test');
  trigger.fire('click');
  onMessage({ data: { type: 'busy', busy: true } });
  const before = messages.length;
  trigger.fire('click');
  element('deployment-options').children[1].fire('click');
  assert.equal(messages.length, before);
  assert.equal(trigger.getAttribute('aria-expanded'), 'false');

  onMessage({ data: { type: 'context', context: dropdownContext, selectedDeploymentId: 'removed' } });
  onMessage({ data: { type: 'data', views: [], workflows: [] } });
  const options = element('deployment-options').children;
  assert.equal(options[3].getAttribute('aria-disabled'), 'true');
  assert.equal(element('deployment-selected-label').textContent, 'Unavailable deployment [removed]');
  trigger.fire('click');
  options[3].fire('click');
  assert.equal(messages.length, before);
  trigger.fire('keydown', { key: 'End' });
  assert.equal(trigger.getAttribute('aria-activedescendant'), options[2].id, 'keyboard navigation skips the unavailable entry');
  trigger.fire('keydown', { key: 'Enter' });
  assert.equal(messages.at(-1)?.deploymentId, 'prod');
});

test('all configured targets have plain selectable labels and deployment refresh preserves discard confirmation', () => {
  const { element, messages, onMessage, sandbox } = createConnectionManagerView();
  const context = dropdownContext;
  onMessage({ data: { type: 'context', context } });
  onMessage({ data: { type: 'data', views: [], workflows: [] } });
  const options = element('deployment-options').children;
  assert.equal(options.length, 3);
  assert.equal(options[1].getAttribute('aria-disabled'), null);
  assert.equal(options[1].children[0].textContent, 'develop [dev] [dev]');
  assert.equal(options[1].title, 'develop [dev] [dev]');
  assert.equal(options[2].getAttribute('aria-disabled'), null);
  assert.equal(options[2].children[0].textContent, 'production [prod] [prod]');
  assert.equal(options[2].title, 'production [prod] [prod]');
  element('deployment-select').fire('click');
  element('deployment-select').fire('keydown', { key: 'End' });
  assert.equal(element('deployment-select').getAttribute('aria-activedescendant'), options[2].id);
  options[1].fire('click');
  assert.equal(messages.at(-1)?.deploymentId, 'dev');
  onMessage({ data: { type: 'selectionCancelled' } });
  options[2].fire('click');
  assert.equal(messages.at(-1)?.type, 'selectDeployment');
  assert.equal(messages.at(-1)?.deploymentId, 'prod');
  onMessage({ data: { type: 'selectionCancelled' } });
  runInContext("groups = [{ key: 'connector', references: [{ boundConnectionId: 'old' }] }];", sandbox);
  element('gsel-0').value = '__create__';
  element('refresh-deployments').fire('click');
  assert.equal(messages.at(-1)?.type, 'refreshDeployments');
  assert.equal(messages.at(-1)?.hasPendingChanges, true, 'deployment refresh uses the existing discard confirmation');
  onMessage({ data: { type: 'context', context: dropdownContext } });
  onMessage({ data: { type: 'data', views: [], workflows: [] } });
  assert.equal(element('deployment-options').children[1].getAttribute('aria-disabled'), null);
});

test('both refresh actions clear previous errors immediately and show only loading until the request finishes', () => {
  for (const id of ['refresh', 'refresh-deployments']) {
    const { element, onMessage, messages } = createConnectionManagerView();
    onMessage({ data: { type: 'context', context: dropdownContext } });
    onMessage({ data: { type: 'error', message: 'Environment not found' } });
    assert.equal(element('banner').textContent, 'Environment not found');
    element(id).fire('click');
    assert.equal(messages.at(-1)?.type, id === 'refresh' ? 'refresh' : 'refreshDeployments');
    assert.equal(element('banner').textContent, '');
    assert.equal(element('banner').className, 'banner hidden');
    assert.ok(!element('connection-loading').hiddenClasses.has('hidden'));
    assert.ok(element('content').hiddenClasses.has('hidden'));
    assert.equal(element('status').textContent, '');
    assert.equal(element('apply').disabled, true);
    onMessage({ data: { type: 'busy', busy: true, loadingConnections: true, loadingDeployments: id === 'refresh-deployments' } });
    assert.equal(element('banner').textContent, '');
    assert.ok(!element('connection-loading').hiddenClasses.has('hidden'));
    onMessage({ data: { type: 'error', message: 'Still unavailable' } });
    assert.equal(element('banner').textContent, 'Still unavailable');
    assert.ok(element('connection-loading').hiddenClasses.has('hidden'));
    assert.equal(element('deployment-select').value, 'test');
  }
});

test('deployment or connection loading disables every action and clears connection errors and warnings', () => {
  for (const loading of [
    { loadingConnections: true, loadingDeployments: false },
    { loadingConnections: false, loadingDeployments: true },
    { loadingConnections: true, loadingDeployments: true }
  ]) {
    for (const kind of ['error', 'warning']) {
      const { element, onMessage, sandbox, messages } = createConnectionManagerView();
      const context = { ...dropdownContext, warningMessage: 'Configuration warning' };
      onMessage({ data: { type: 'context', context } });
      onMessage({ data: { type: 'data', views: [], workflows: [] } });
      runInContext("groups = [{ key: 'connector', references: [{ boundConnectionId: 'old' }] }]; views = groups[0].references;", sandbox);
      element('gsel-0').value = '__create__';
      runInContext("setBusy(false, ''); showBanner('" + kind + "', 'Previous failure');", sandbox);
      const actions = ['apply', 'refresh', 'addRef', 'refresh-deployments'];
      for (const id of [...actions, 'search']) {
        assert.equal(element(id).disabled, false, id + ' is enabled before loading');
      }
      assert.equal(element('deployment-warning').textContent, 'Configuration warning');
      onMessage({ data: { type: 'busy', busy: true, ...loading } });
      for (const id of [...actions, 'search', 'deployment-select', 'use-workspace-environment']) {
        assert.equal(element(id).disabled, true, id + ' is disabled during loading');
      }
      assert.equal(element('banner').textContent, '');
      assert.equal(element('banner').className, 'banner hidden');
      assert.equal(element('deployment-warning').textContent, '');
      assert.ok(element('deployment-warning').hiddenClasses.has('hidden'));
      const count = messages.length;
      for (const id of actions) {
        element(id).fire('click');
      }
      assert.equal(messages.length, count, 'loading controls cannot trigger a second operation');
      onMessage({ data: { type: 'error', message: 'New load failure' } });
      assert.equal(element('banner').textContent, 'New load failure');
      assert.equal(element('refresh').disabled, false);
      assert.equal(element('refresh-deployments').disabled, false);
      assert.equal(element('apply').disabled, false);
      assert.equal(element('addRef').disabled, false);
      assert.equal(element('search').disabled, false);
    }
  }
});

test('Apply and filtering remain enabled while idle, including empty connections and errors', () => {
  const { element, onMessage, sandbox, messages } = createConnectionManagerView();
  onMessage({ data: { type: 'context', context: dropdownContext } });
  onMessage({ data: { type: 'data', views: [], workflows: [] } });
  assert.equal(element('apply').disabled, false);
  assert.equal(element('search').disabled, false);
  element('apply').fire('click');
  assert.equal(messages.at(-1)?.type, 'apply', 'an empty Apply reaches the controller for feedback');
  assert.equal(element('apply').disabled, true, 'Apply is disabled only while its request is running');
  onMessage({ data: { type: 'busy', busy: false } });
  runInContext("groups = [{ key: 'connector', references: [{ boundConnectionId: 'old' }] }]; views = groups[0].references;", sandbox);
  element('gsel-0').value = '__create__';
  runInContext("setBusy(false, '');", sandbox);
  assert.equal(element('apply').disabled, false);
  assert.equal(element('search').disabled, false);
  onMessage({ data: { type: 'error', message: 'Connection update failed' } });
  assert.equal(element('apply').disabled, false);
  assert.equal(element('search').disabled, false);
  const beforeRetry = messages.length;
  element('apply').fire('click');
  assert.equal(messages.length, beforeRetry + 1, 'errors do not prevent another attempt');
  assert.equal(messages.at(-1)?.type, 'apply');
  assert.equal(element('apply').disabled, true);
  onMessage({ data: { type: 'data', views: [], workflows: [] } });
  assert.equal(element('apply').disabled, false);
  assert.equal(element('search').disabled, false);
  runInContext("views = [{}]; setBusy(false, '');", sandbox);
  assert.equal(element('search').disabled, false, 'a successful reload re-enables filtering');
  element('search').value = 'no matches';
  element('search').fire('input');
  assert.equal(element('search').disabled, false, 'a filter with no matching rows can still be cleared');
});

test('a blocked workflow action stays clickable and explains its prerequisite without enabling it', () => {
  const { element, onMessage, sandbox, messages } = createConnectionManagerView();
  onMessage({ data: { type: 'context', context: dropdownContext } });
  onMessage({ data: { type: 'data', views: [], workflows: [] } });
  const usage = runInContext(
    "buildWorkflowUsage({ filePath: 'metadata.yml' }, { state: 0, canEnable: false, displayName: 'Blocked workflow' })",
    sandbox
  ) as Element;
  const button = usage.children[2];
  assert.equal(button.disabled, false);
  const beforeClick = messages.length;
  button.fire('click');
  assert.match(element('banner').textContent, /Connect this workflow to its connections/);
  assert.equal(messages.length, beforeClick, 'blocked workflows never send an enable request');
  element('content').querySelectorAll = () => [button];
  onMessage({ data: { type: 'busy', busy: true } });
  assert.equal(button.disabled, true);
  button.fire('click');
  assert.equal(messages.length, beforeClick);
  onMessage({ data: { type: 'busy', busy: false } });
  assert.equal(button.disabled, false);
});
