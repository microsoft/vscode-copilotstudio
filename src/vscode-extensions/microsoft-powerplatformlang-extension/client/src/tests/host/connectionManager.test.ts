import * as assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import * as path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createContext, runInContext } from 'node:vm';
import { loadDeploymentContext, resolveEnvironmentNames, type DeploymentContext } from '../../deployments/deploymentContext';
import { DefaultCoreServicesClusterCategory, TelemetryEventsKeys } from '../../constants';
import { HttpRequestError } from '../../utils/httpRequestError';
import type { AgentSyncInfo } from '../../types';
import { config, fixture, syncInfo } from './helpers/deploymentFixtures';
import { createConnectionManagerView } from './helpers/connectionManagerView';

async function verifyWorkspaceConnections(
  t: { after: (fn: () => Promise<void>) => void },
  contents?: unknown,
  deploymentCount = 0,
  options: {
    configurationError?: RegExp;
    matchingAgentCount?: number;
    infoMessage?: string;
    warningMessage?: string;
    missingEnvironmentIds?: string[];
    endpointRepair?: 'succeeds' | 'fails';
    connectionFailure?: 'environment' | 'agent';
    singleRemote?: boolean;
    nameLookupFailure?: boolean;
    prepare?: (workspacePath: string) => Promise<void>;
  } = {}
): Promise<void> {
  const workspacePath = await fixture(t, contents);
  await options.prepare?.(workspacePath);
  const usesDeploymentSelection = deploymentCount > 1 || !!options.singleRemote;
  const expectedMatchingAgentCount = options.matchingAgentCount ?? (contents !== undefined && !options.configurationError ? 1 : undefined);
  const missingEnvironmentIds = options.missingEnvironmentIds ?? [];
  const workspace = {
    workspaceUri: pathToFileURL(workspacePath).href,
    displayName: 'Agent',
    syncInfo: {
      ...syncInfo, environmentDisplayName: 'Test Environment',
      agentManagementEndpoint: options.endpointRepair ? undefined : syncInfo.agentManagementEndpoint
    }
  };
  const expectedWorkspace = JSON.stringify({
    ...workspace,
    syncInfo: {
      ...workspace.syncInfo,
      agentManagementEndpoint: options.endpointRepair === 'fails' ? undefined : syncInfo.agentManagementEndpoint
    }
  });
  const view = createConnectionManagerView();
  const messages: { type: string; message?: string; context?: DeploymentContext; selectedDeploymentId?: string }[] = [];
  const listed: { workspace: typeof workspace; schema?: string }[] = [];
  const configurationErrors: string[] = [];
  const configurationInfo: string[] = [];
  const environmentLookups: string[] = [];
  const nameLookupErrors: Error[] = [];
  const connectionErrors: Error[] = [];
  const connectionError = options.connectionFailure === 'environment'
    ? new HttpRequestError(404, { error: { code: 'EnvironmentNotFound', message: 'Environment lookup failed' } })
    : new Error(options.connectionFailure === 'agent'
      ? 'Request failed with status 403' : "The agent was not found in the selected deployment's environment.");
  const connectionErrorMessage = connectionError instanceof HttpRequestError ? connectionError.userMessage : connectionError.message;
  let endpointRepairs = 0;
  const endpointErrors: Error[] = [];
  const endpointErrorMessage = 'The workspace Copilot Studio endpoint is unavailable. Refresh the workspace and try again.';
  const connectionViews = [{
    connectionReferenceLogicalName: 'test_reference',
    connectorId: 'test_connector',
    connectorName: 'Test Connector',
    boundConnectionId: '',
    boundConnectionExists: false,
    candidates: [],
    usages: [],
    isDeclared: true
  }];
  const modules: Record<string, unknown> = {
    vscode: {
      Uri: { parse: (value: string) => ({ fsPath: value.startsWith('file:') ? fileURLToPath(value) : value }) }, ViewColumn: { Active: 1 },
      window: {
        createWebviewPanel: () => ({
          webview: {
            postMessage: (message: typeof messages[number]) => {
              messages.push(message);
              view.onMessage({ data: message });
            },
            onDidReceiveMessage: () => ({ dispose() {} })
          },
          onDidDispose: () => ({ dispose() {} })
        })
      }
    },
    '../services/logger': {
      logInfo: (event: unknown, message: string | undefined, details: { message: string }) => {
        assert.equal(event, TelemetryEventsKeys.LoadDeploymentsInfo);
        assert.equal(message, undefined, 'Configuration info must not trigger a notification');
        assert.equal(details.message, `Found ${deploymentCount} deployments for the agent.`);
        configurationInfo.push(details.message);
      },
      logError: (event: unknown, message: string | undefined, details: { message?: string; error?: Error }) => {
        if (event === TelemetryEventsKeys.LoadDeploymentsError) {
          assert.ok(options.configurationError, 'Unexpected configuration error');
          assert.equal(message, undefined, 'Configuration failures must not trigger a notification');
          assert.ok(details.message);
          assert.match(details.message, options.configurationError);
          assert.equal(details.error, undefined, 'Configuration failures must pass only the error message');
          configurationErrors.push(details.message);
          return;
        }
        assert.ok(details.error, 'Unexpected error without diagnostics');
        if (options.nameLookupFailure && details.message === 'Failed to load deployment environment name') {
          assert.equal(message, undefined, 'Display-name lookup errors must remain diagnostic-only');
          assert.equal(details.error.message, 'Environment name lookup denied');
          nameLookupErrors.push(details.error);
          return;
        }
        if ((options.connectionFailure || missingEnvironmentIds.length) && event === TelemetryEventsKeys.ConnectionCreationError) {
          assert.equal(message, undefined, 'Connection-load errors must not trigger notifications');
          assert.equal(details.message, `<pii>${connectionErrorMessage}</pii>`, 'Output must include the inline message with telemetry redaction');
          connectionErrors.push(details.error);
          return;
        }
        if (options.endpointRepair === 'fails' && event === TelemetryEventsKeys.ConnectionCreationError) {
          assert.equal(message, undefined, 'Connection-load errors must not trigger notifications');
          assert.equal(details.message, `<pii>${endpointErrorMessage}</pii>`);
          assert.equal(details.error.message, endpointErrorMessage);
          endpointErrors.push(details.error);
          return;
        }
        assert.fail('Unexpected error: ' + message + ': ' + details.error.message);
      }
    },
    '../constants': { TelemetryEventsKeys, DefaultCoreServicesClusterCategory: 0 },
    '../utils/httpRequestError': { HttpRequestError },
    '../sync/localWorkspaces': {
      getWorkspaceByUri: () => workspace,
      tryRepairAgentManagementEndpoint: async (target: AgentSyncInfo, workspaceUri: string) => {
        assert.ok(options.endpointRepair, 'A complete endpoint must not trigger repair');
        assert.equal(target, workspace.syncInfo);
        assert.equal(workspaceUri, workspace.workspaceUri);
        endpointRepairs++;
        if (options.endpointRepair === 'fails') {
          return false;
        }
        target.agentManagementEndpoint = syncInfo.agentManagementEndpoint;
        return true;
      }
    },
    '../types': {},
    './connectionCatalog': {
      listAgentConnections: async (target: typeof workspace, schema?: string) => {
        assert.equal(target.syncInfo.agentManagementEndpoint, syncInfo.agentManagementEndpoint);
        listed.push({ workspace: target, schema });
        if (schema && (options.connectionFailure === 'agent' || missingEnvironmentIds.includes(target.syncInfo.environmentId))) {
          throw connectionError;
        }
        return connectionViews;
      },
      listWorkflowStatus: async () => []
    },
    './connectionCreation': {}, './connectorReadiness': {}, './connectorPicker': {}, './connectionManagerHtml': {},
    '../deployments/deploymentContext': { loadDeploymentContext, resolveEnvironmentNames },
    '../clients/bapClient': {
      getEnvironmentDisplayNameByIdAsync: (_category: unknown, environmentId: string) => {
        assert.ok(environmentId === 'dev-environment' || missingEnvironmentIds.includes(environmentId));
        if (options.nameLookupFailure) {
          throw new Error('Environment name lookup denied');
        }
        return environmentId === 'dev-environment' ? 'Development Environment' : 'Missing Environment';
      },
      getEnvironmentEndpointByIdAsync: (_category: unknown, environmentId: string) => {
        assert.ok(environmentId === 'dev-environment' || missingEnvironmentIds.includes(environmentId));
        environmentLookups.push(environmentId);
        if (environmentId === 'dev-environment' && options.connectionFailure === 'environment') {
          throw connectionError;
        }
        return { dataverseUrl: `https://${environmentId}.example.com`, agentManagementUrl: syncInfo.agentManagementEndpoint };
      }
    },
    '../clients/account': { resolveAccountIdentity: (account: unknown) => account },
    '../clients/dataverseClient': {
      getSolutionVersionsAsync: () => {
        return syncInfo.solutionVersions;
      }
    }
  };
  const exports: Record<string, unknown> = {};
  const sandbox = createContext({
    exports, AbortSignal, Error,
    require: (id: string) => {
      assert.ok(id in modules, 'Unexpected dependency: ' + id);
      return modules[id];
    }
  });
  const compiled = await readFile(path.resolve(__dirname, '..', '..', 'connections', 'connectionManager.js'), 'utf8');
  runInContext(compiled, sandbox);
  const Controller = exports.ConnectionManagerController as new (context: unknown, target: unknown, html: () => string) => {
    onMessage(message: unknown): Promise<void>;
  };
  const controller = new Controller({ extensionUri: 'extension' }, workspace, () => '');
  for (const type of ['ready', 'refreshDeployments']) {
    await controller.onMessage({ type });
    if (options.endpointRepair === 'fails') {
      assert.equal(messages.at(-1)?.type, 'error');
      assert.equal(messages.at(-1)?.message, endpointErrorMessage);
      assert.equal(view.element('apply').disabled, false);
      assert.equal(view.element('search').disabled, false);
      continue;
    }
    assert.equal(messages.at(-1)?.type, 'data');
    const contextMessage = messages.filter(message => message.type === 'context').at(-1);
    const context = contextMessage?.context;
    assert.ok(context);
    assert.equal(context.deployments.length, deploymentCount);
    assert.equal(contextMessage?.selectedDeploymentId, usesDeploymentSelection ? context.currentDeploymentId || '' : '');
    assert.equal(listed.at(-1)?.workspace, workspace);
    assert.equal(listed.at(-1)?.schema, undefined);
    assert.equal(view.element('deployments-section').hiddenClasses.has('hidden'), !usesDeploymentSelection);
    assert.equal(view.element('sync-target-badge').hiddenClasses.has('hidden'), !usesDeploymentSelection || !context.currentDeploymentId);
    for (const [field, id] of [['infoMessage', 'deployment-info'], ['warningMessage', 'deployment-warning']] as const) {
      assert.equal(context[field], options[field]);
      const message = usesDeploymentSelection ? options[field] ?? '' : '';
      assert.equal(view.element(id).hiddenClasses.has('hidden'), !message);
      assert.equal(view.element(id).textContent, message);
    }
    assert.ok(!view.element('content').hiddenClasses.has('hidden'));
    assert.equal(view.element('search').disabled, false);
    assert.equal(view.element('connections-title').textContent, context.currentDeploymentId
      ? 'Connections in testdeploy [test]' : 'Connections');
    assert.equal(view.element('connected-environment').textContent, usesDeploymentSelection && context.currentDeploymentId
      ? ' \u2014 Test Environment [TEST-ENVIRONMENT]' : ' \u2014 Test Environment [test-environment]');
    if (usesDeploymentSelection) {
      const deploymentOptions = view.element('deployment-options').children;
      assert.equal(deploymentOptions.length, deploymentCount);
      for (const option of deploymentOptions) {
        assert.equal(option.getAttribute('aria-disabled'), null);
      }
      if (context.currentDeploymentId) {
        assert.equal(deploymentOptions[0].getAttribute('aria-selected'), 'true');
        assert.equal(deploymentOptions[0].children[1].textContent, 'Attached');
      }
      if (type === 'ready') {
        view.element('refresh-deployments').fire('click');
        assert.equal(view.messages.at(-1)?.type, 'refreshDeployments');
        assert.equal(view.messages.at(-1)?.hasPendingChanges, false);
      }
    }
  }
  assert.equal(listed.length, options.endpointRepair === 'fails' ? 0 : 2);
  assert.equal(endpointRepairs, options.endpointRepair === 'fails' ? 2 : options.endpointRepair ? 1 : 0);
  assert.equal(endpointErrors.length, options.endpointRepair === 'fails' ? 2 : 0);
  assert.equal(configurationErrors.length, options.configurationError ? 2 : 0);
  const context = messages.filter(message => message.type === 'context').at(-1)?.context;
  assert.equal(context?.matchingAgentCount, expectedMatchingAgentCount);
  assert.equal(context?.errorMessage, undefined, 'Configuration errors must not be sent to the webview');
  assert.equal(configurationInfo.length, expectedMatchingAgentCount !== undefined ? 2 : 0);
  assert.equal(environmentLookups.length, 0, 'discovery and workspace refresh never probe deployment endpoints');
  assert.equal(nameLookupErrors.length, options.nameLookupFailure ? 2 : 0);
  assert.equal(connectionErrors.length, 0, 'unselected deployments must not report connection errors');
  assert.equal(messages.filter(message => message.type === 'error').length, options.endpointRepair === 'fails' ? 2 : 0);
  assert.ok(!messages.some(message => message.type === 'warning'));
  assert.equal(JSON.stringify(workspace), expectedWorkspace);
  if (options.connectionFailure || missingEnvironmentIds.length || options.singleRemote) {
    const deployment = context?.deployments.find(item => missingEnvironmentIds.includes(item.environmentId))
      ?? context?.deployments.find(item => item.id === 'dev');
    assert.ok(deployment);
    const option = view.element('deployment-options').children.find(item => item.children[0].textContent.includes('[' + deployment.id + ']'));
    assert.ok(option);
    option.fire('click');
    const selection = view.messages.at(-1);
    assert.equal(selection?.type, 'selectDeployment');
    assert.equal(selection?.deploymentId, deployment.id);
    for (const message of [selection, { type: 'refreshDeployments', hasPendingChanges: false }]) {
      await controller.onMessage(message);
      const contextMessage = messages.filter(item => item.type === 'context').at(-1);
      assert.equal(contextMessage?.selectedDeploymentId, deployment.id, 'failed loads must not silently restore the workspace target');
      assert.equal(contextMessage?.context?.warningMessage, options.warningMessage);
      assert.equal(contextMessage?.context?.infoMessage, options.infoMessage);
      assert.equal(view.element('connections-title').textContent, `Connections in ${deployment.name}${deployment.type ? ' [' + deployment.type + ']' : ''}`);
      if (options.connectionFailure || missingEnvironmentIds.length) {
        assert.equal(messages.at(-1)?.type, 'error');
        assert.equal(messages.at(-1)?.message, connectionErrorMessage);
        assert.equal(view.element('banner').textContent, connectionErrorMessage);
        assert.equal(view.element('apply').disabled, false);
        if (options.connectionFailure === 'environment') {
          assert.equal(listed.length, 2, 'a failed environment lookup cannot reuse the previous connection target');
        }
        const beforeApply = view.messages.length;
        view.element('apply').fire('click');
        assert.equal(view.messages.length, beforeApply + 1);
        assert.equal(view.messages.at(-1)?.type, 'apply', 'the controller validates attempts instead of disabling Apply');
        assert.equal(view.element('banner').textContent, connectionErrorMessage, 'Apply must not clear the original load error');
        const errorsBeforeActions: number = connectionErrors.length;
        const loadsBeforeActions: number = listed.length;
        for (const action of [
          view.messages.at(-1),
          { type: 'apply', bindings: [{ connectionReferenceLogicalName: 'test_reference', connectionId: 'connection' }], creates: [] },
          { type: 'apply', bindings: [], creates: [{ connectionReferenceLogicalName: 'test_reference', connectorName: 'shared_test' }] },
          { type: 'addReference' }
        ]) {
          await controller.onMessage(action);
          assert.equal(messages.at(-1)?.message, connectionErrorMessage);
          assert.equal(view.element('banner').textContent, connectionErrorMessage);
          assert.equal(view.element('apply').disabled, false);
          assert.equal(view.element('addRef').disabled, false);
          assert.equal(view.element('refresh').disabled, false);
        }
        assert.equal(connectionErrors.length, errorsBeforeActions, 'blocked actions must not log or notify another error');
        assert.equal(listed.length, loadsBeforeActions, 'only an explicit refresh or target switch retries loading');
      } else {
        assert.equal(messages.at(-1)?.type, 'data');
        assert.equal(listed.at(-1)?.schema, 'agent_schema');
        assert.equal(listed.at(-1)?.workspace.syncInfo.environmentId, 'dev-environment');
        assert.equal(listed.at(-1)?.workspace.syncInfo.agentId, undefined);
      }
    }
    if (context?.currentDeploymentId) {
      view.element('deployment-options').children[0].fire('click');
    } else {
      assert.ok(!view.element('use-workspace-environment').hiddenClasses.has('hidden'));
      view.element('use-workspace-environment').fire('click');
    }
    const restore = view.messages.at(-1);
    assert.equal(restore?.deploymentId, context?.currentDeploymentId || '');
    await controller.onMessage(restore);
    assert.equal(messages.at(-1)?.type, 'data');
    assert.equal(listed.at(-1)?.workspace, workspace);
    assert.equal(view.element('banner').textContent, '');
    assert.equal(JSON.stringify(workspace), expectedWorkspace);
  }
}

test('scenario 1: absent hosting config silently loads and refreshes workspace connections without deployment checks', async t => {
  await verifyWorkspaceConnections(t);
});

test('scenario 2: unrelated hosting config logs output-only info while workspace connections load and refresh', async t => {
  const value = config();
  value.managedAgents[0].agentLocation = 'different-agent';
  await verifyWorkspaceConnections(t, value, 0, {
    matchingAgentCount: 0
  });
});

test('ambiguous managed-agent matching logs its count as an output-only error while workspace connections load and refresh', async t => {
  const value = config();
  value.managedAgents.push({ ...value.managedAgents[0], agentId: 'duplicate-agent' });
  await verifyWorkspaceConnections(t, value, 0, {
    matchingAgentCount: 2,
    configurationError: /^Found 2 managed agents matching this workspace in agent\.config\.json\.$/
  });
});

test('scenario 3: matching agent without deployments logs output-only info while workspace connections load and refresh', async t => {
  const value = config();
  value.agentDeployments = [];
  await verifyWorkspaceConnections(t, value);
});

test('scenario 3: unrelated deployments allow workspace fallback with output-only info for a matching agent', async t => {
  const value = config();
  value.agentDeployments = value.agentDeployments.filter(deployment => deployment.agentId === 'other-agent');
  await verifyWorkspaceConnections(t, value);
});

test('scenario 4: one attached deployment uses workspace connections even with unrelated deployment entries', async t => {
  const value = config();
  value.agentDeployments = value.agentDeployments.filter(deployment => deployment.deploymentId !== 'dev');
  await verifyWorkspaceConnections(t, value, 1);
});

test('scenario 4: one nonattached deployment offers both targets and initially loads workspace connections', async t => {
  for (const enabled of [true, false]) {
    const value = config();
    value.agentDeployments = [{ ...value.agentDeployments[0], isEnabled: enabled }];
    await verifyWorkspaceConnections(t, value, 1, {
      singleRemote: true,
      infoMessage: "No deployment targets the workspace environment."
    });
  }
});

test('a sole nonattached deployment remains selectable when its remote agent is missing', async t => {
  const value = config();
  value.agentDeployments = [value.agentDeployments[0]];
  await verifyWorkspaceConnections(t, value, 1, {
    singleRemote: true,
    missingEnvironmentIds: ['dev-environment'],
    infoMessage: "No deployment targets the workspace environment."
  });
});

test('scenario 5: malformed JSON logs an output-only diagnostic while workspace connections load and refresh', async t => {
  await verifyWorkspaceConnections(t, config(), 0, {
    configurationError: /JSON/,
    prepare: workspacePath => writeFile(path.join(workspacePath, '..', '.config', 'agent.config.json'), '{')
  });
});

test('scenario 5: invalid config structure logs an output-only diagnostic while workspace connections load and refresh', async t => {
  await verifyWorkspaceConnections(t, { managedAgents: [], agentDeployments: 'invalid' }, 0, {
    configurationError: /Invalid agent.config.json/
  });
});

test('scenario 5: a non-string deployment ID discards all deployments and logs only to Output', async t => {
  for (const index of [0, 2]) {
    const value = config();
    const contents = {
      ...value,
      agentDeployments: value.agentDeployments.map((deployment, i) => i === index
        ? { ...deployment, deploymentId: 42 } : deployment)
    };
    await verifyWorkspaceConnections(t, contents, 0, {
      configurationError: /deploymentId must be a string/
    });
  }
});

test('scenario 5: unreadable config logs an output-only diagnostic while workspace connections load and refresh', async t => {
  await verifyWorkspaceConnections(t, undefined, 0, {
    configurationError: /EISDIR|EACCES|EPERM/,
    prepare: async workspacePath => {
      await mkdir(path.join(workspacePath, '..', '.config', 'agent.config.json'), { recursive: true });
    }
  });
});

test('scenario 6: existing deployments are selectable and the uniquely attached target remains selected after refresh', async t => {
  await verifyWorkspaceConnections(t, config(), 2);
});

test('scenario 7: missing agents remain selectable and report the blocker only when selected', async t => {
  const value = config();
  value.agentDeployments.push({
    deploymentId: 'missing', agentId: 'hosting-agent', deploymentName: 'missing-target',
    deploymentType: 'prod', environmentId: 'missing-environment', isEnabled: true
  });
  await verifyWorkspaceConnections(t, value, 3, { missingEnvironmentIds: ['missing-environment'] });
});

test('scenario 8: a missing remote agent does not hide its deployment or force workspace fallback', async t => {
  const value = config();
  value.agentDeployments[0].environmentId = 'missing-environment';
  await verifyWorkspaceConnections(t, value, 2, { missingEnvironmentIds: ['missing-environment'] });
});

test('scenario 9: all missing remote agents still offer deployment selection and workspace connections', async t => {
  const value = config();
  const missingEnvironmentIds = ['first-missing-environment', 'second-missing-environment'];
  value.agentDeployments[0].environmentId = missingEnvironmentIds[0];
  value.agentDeployments[1].environmentId = missingEnvironmentIds[1];
  await verifyWorkspaceConnections(t, value, 2, {
    missingEnvironmentIds,
    infoMessage: "No deployment targets the workspace environment."
  });
});

test('scenario 10: targets remain selected after environment or agent access errors and refresh retries loading', async t => {
  for (const connectionFailure of ['environment', 'agent'] as const) {
    await verifyWorkspaceConnections(t, config(), 2, { connectionFailure });
  }
});

test('environment display-name failures leave deployments selectable without notifying about unselected targets', async t => {
  await verifyWorkspaceConnections(t, config(), 2, { nameLookupFailure: true });
});

test('repairs a missing workspace management endpoint before loading connections and reuses it on refresh', async t => {
  await verifyWorkspaceConnections(t, undefined, 0, { endpointRepair: 'succeeds' });
});

test('blocks workspace connection requests with a clear error when management endpoint repair fails and retries on refresh', async t => {
  await verifyWorkspaceConnections(t, undefined, 0, { endpointRepair: 'fails' });
});

test('cached connection panels protect workspace attachment changes', async t => {
  const compiled = await readFile(path.resolve(__dirname, '..', '..', 'connections', 'connectionManager.js'), 'utf8');
  const bindings = [{ connectionReferenceLogicalName: 'reference', connectionId: 'connection' }];
  const creates = [{ connectionReferenceLogicalName: 'reference', connectorName: 'shared_test' }];
  const gate = () => {
    let resolve!: () => void;
    const promise = new Promise<void>(complete => { resolve = complete; });
    return { promise, resolve };
  };
  const setup = async (deployments: DeploymentContext['deployments'] = []) => {
    const original = {
      workspaceUri: 'file:///agent', displayName: 'Original agent', schemaName: 'original_agent',
      syncInfo: { ...syncInfo, accountInfo: { ...syncInfo.accountInfo } }
    };
    let workspace = original;
    const view = createConnectionManagerView();
    const messages: { type: string; message?: string; selectedDeploymentId?: string }[] = [];
    const actions: { name: string; workspace: typeof original }[] = [];
    const hooks = {
      load: async () => {},
      picker: async () => {},
      browser: async () => {},
      apply: async () => {}
    };
    let discard = false;
    let panelsCreated = 0;
    let reveals = 0;
    const modules: Record<string, unknown> = {
      vscode: {
        Uri: { parse: (value: string) => ({ fsPath: value }) }, ViewColumn: { Active: 1 },
        CancellationTokenSource: class { token = {}; dispose() {} },
        window: {
          createWebviewPanel: () => {
            panelsCreated++;
            return {
              webview: {
                postMessage: (message: typeof messages[number]) => {
                  messages.push(message);
                  view.onMessage({ data: message });
                },
                onDidReceiveMessage: () => ({ dispose() {} })
              },
              reveal: () => { reveals++; },
              onDidDispose: () => ({ dispose() {} })
            };
          },
          showWarningMessage: async (_message: string, _options: unknown, ...choices: { title: string }[]) => discard ? choices[0] : undefined,
          showInformationMessage() {}
        }
      },
      '../services/logger': { logError() {}, logInfo() {} },
      '../constants': { TelemetryEventsKeys: {}, DefaultCoreServicesClusterCategory: 0 },
      '../utils/httpRequestError': { HttpRequestError },
      '../sync/localWorkspaces': { getWorkspaceByUri: () => workspace },
      '../types': {},
      './connectionCatalog': {
        listAgentConnections: async (target: typeof original) => {
          actions.push({ name: 'list', workspace: target });
          await hooks.load();
          return [];
        },
        listWorkflowStatus: async (target: typeof original) => {
          actions.push({ name: 'workflows', workspace: target });
          return [];
        },
        applyConnectionBindings: async (target: typeof original) => {
          actions.push({ name: 'apply', workspace: target });
          await hooks.apply();
          return [];
        },
        createConnectionReference: async (target: typeof original) => {
          actions.push({ name: 'reference', workspace: target });
          return { logicalName: 'reference' };
        },
        setWorkflowStates: async (target: typeof original) => {
          actions.push({ name: 'enableWorkflow', workspace: target });
          return { succeeded: true, workflows: [] };
        }
      },
      './connectionCreation': {
        awaitConnectionCreation: async (options: { environmentId: string }) => {
          assert.equal(options.environmentId, original.syncInfo.environmentId);
          actions.push({ name: 'browser', workspace: original });
          await hooks.browser();
          return { status: 'created', connectionId: 'new-connection' };
        },
        resolveCreatedConnectionId: () => 'new-connection'
      },
      './connectorReadiness': { isCustomConnectorInternalId: () => false },
      './connectorPicker': { pickConnector: async () => { await hooks.picker(); return 'shared_test'; } },
      './connectionManagerHtml': { buildConnectionManagerHtml: () => '' },
      '../deployments/deploymentContext': {
        loadDeploymentContext: async () => ({
          environmentId: workspace.syncInfo.environmentId, deployments,
          currentDeploymentId: deployments.find(deployment => deployment.environmentId === workspace.syncInfo.environmentId)?.id
        }),
        resolveEnvironmentNames: async (context: DeploymentContext) => context
      },
      '../clients/bapClient': {
        getEnvironmentEndpointByIdAsync: async () => ({
          dataverseUrl: 'https://remote.example.com', agentManagementUrl: 'https://remote-management.example.com'
        })
      },
      '../clients/account': { resolveAccountIdentity: (account: unknown) => account },
      '../clients/dataverseClient': { getSolutionVersionsAsync: async () => syncInfo.solutionVersions }
    };
    const exports: Record<string, unknown> = {};
    runInContext(compiled, createContext({
      exports, AbortSignal, Error,
      require: (id: string) => {
        assert.ok(id in modules, 'Unexpected dependency: ' + id);
        return modules[id];
      }
    }));
    const Controller = exports.ConnectionManagerController as {
      show(context: unknown, target: typeof original): Promise<void>;
      panels: Map<string, { onMessage(message: unknown): Promise<void> }>;
    };
    const show = () => Controller.show({ extensionUri: 'extension' }, workspace);
    await show();
    const controller = Controller.panels.get(original.workspaceUri);
    assert.ok(controller);
    const dispatch = (message: unknown) => controller.onMessage(message);
    const reattach = async (reopen = true) => {
      workspace = {
        ...original, displayName: 'Reattached agent', schemaName: 'reattached_agent',
        syncInfo: { ...original.syncInfo, environmentId: 'reattached-environment', dataverseEndpoint: 'https://reattached.example.com' }
      };
      if (reopen) {
        await show();
      }
    };
    await dispatch({ type: 'ready' });
    return {
      original, view, messages, actions, hooks, dispatch, show, reattach,
      get workspace() { return workspace; },
      get panelsCreated() { return panelsCreated; },
      get reveals() { return reveals; },
      acceptDiscard() { discard = true; }
    };
  };

  await t.test('reopening an unchanged attachment preserves pending edits', async () => {
    const manager = await setup();
    runInContext("groups = [{ references: [{ boundConnectionId: 'old' }] }]; views = groups[0].references;", manager.view.sandbox);
    manager.view.element('gsel-0').value = '__create__';
    const messageCount = manager.messages.length;
    await manager.show();
    assert.equal(manager.panelsCreated, 1);
    assert.equal(manager.reveals, 1);
    assert.equal(manager.messages.length, messageCount);
    assert.equal(manager.view.element('gsel-0').value, '__create__');
  });

  await t.test('reattachment blocks stale actions until a confirmed refresh reloads the entire target', async () => {
    const manager = await setup();
    runInContext("groups = [{ references: [{ boundConnectionId: 'old' }] }]; views = groups[0].references;", manager.view.sandbox);
    manager.view.element('gsel-0').value = '__create__';
    const actionCount = manager.actions.length;
    await manager.reattach();
    assert.equal(manager.panelsCreated, 1);
    assert.match(manager.messages.at(-1)?.message ?? '', /workspace.*changed.*Refresh/i);
    for (const message of [
      { type: 'apply', bindings, creates: [] },
      { type: 'apply', bindings: [], creates },
      { type: 'enableWorkflow', workflowId: 'workflow', activate: true },
      { type: 'addReference' },
      { type: 'declareReference', logicalName: 'reference' },
      { type: 'deleteReference', logicalName: 'reference' },
      { type: 'openUsage', filePath: 'topics/topic.mcs.yml' },
      { type: 'selectDeployment', deploymentId: '', hasPendingChanges: false }
    ]) {
      await manager.dispatch(message);
      assert.match(manager.messages.at(-1)?.message ?? '', /workspace.*changed.*Refresh/i);
    }
    assert.equal(manager.actions.length, actionCount);
    manager.view.element('refresh').fire('click');
    assert.equal(manager.view.messages.at(-1)?.hasPendingChanges, true);
    await manager.dispatch(manager.view.messages.at(-1));
    assert.equal(manager.actions.length, actionCount);
    assert.equal(manager.view.element('gsel-0').value, '__create__');
    manager.acceptDiscard();
    manager.view.element('refresh').fire('click');
    await manager.dispatch(manager.view.messages.at(-1));
    assert.equal(manager.messages.at(-1)?.type, 'data');
    assert.equal(manager.actions.at(-2)?.workspace, manager.workspace);
    assert.equal(manager.actions.at(-1)?.workspace, manager.workspace);
    assert.equal(manager.view.element('workspace-name').textContent, manager.workspace.displayName);
    assert.ok(manager.view.element('connected-environment').textContent.includes(manager.workspace.syncInfo.environmentId));
    await manager.dispatch({ type: 'apply', bindings, creates: [] });
    assert.equal(manager.actions.find(action => action.name === 'apply')?.workspace, manager.workspace);
  });

  await t.test('detects an in-place attachment change without reopening the panel', async () => {
    const manager = await setup();
    manager.workspace.syncInfo.accountInfo.tenantId = 'different-tenant';
    const actionCount = manager.actions.length;
    await manager.dispatch({ type: 'apply', bindings, creates: [] });
    assert.equal(manager.actions.length, actionCount);
    assert.match(manager.messages.at(-1)?.message ?? '', /workspace.*changed.*Refresh/i);
  });

  await t.test('refresh after reattachment resets the previously selected deployment', async () => {
    const manager = await setup([
      { id: 'attached', name: 'Attached', environmentId: syncInfo.environmentId },
      { id: 'remote', name: 'Remote', environmentId: 'remote-environment' }
    ]);
    await manager.dispatch({ type: 'selectDeployment', deploymentId: 'remote', hasPendingChanges: false });
    assert.equal(manager.messages.filter(message => message.type === 'context').at(-1)?.selectedDeploymentId, 'remote');
    await manager.reattach();
    await manager.dispatch({ type: 'refresh', hasPendingChanges: false });
    assert.equal(manager.messages.at(-1)?.type, 'data');
    assert.equal(manager.messages.filter(message => message.type === 'context').at(-1)?.selectedDeploymentId, '');
    assert.equal(manager.actions.at(-2)?.workspace, manager.workspace);
    assert.equal(manager.actions.at(-1)?.workspace, manager.workspace);
  });

  await t.test('reattachment while target validation yields blocks the binding request', async () => {
    const manager = await setup();
    const actionCount = manager.actions.length;
    const pending = manager.dispatch({ type: 'apply', bindings, creates: [] });
    await manager.reattach();
    await pending;
    assert.equal(manager.actions.length, actionCount);
    assert.match(manager.messages.at(-1)?.message ?? '', /workspace.*changed.*Refresh/i);
  });

  for (const operation of ['load', 'picker', 'browser', 'apply'] as const) {
    await t.test(`reattachment during ${operation} prevents stale follow-up writes or data`, async () => {
      const manager = await setup();
      const started = gate();
      const finished = gate();
      manager.hooks[operation] = async () => { started.resolve(); await finished.promise; };
      const pending = manager.dispatch(operation === 'load' ? { type: 'refresh', hasPendingChanges: false }
        : operation === 'picker' ? { type: 'addReference' }
          : { type: 'apply', bindings: operation === 'apply' ? bindings : [], creates: operation === 'browser' ? creates : [] });
      await started.promise;
      try {
        await manager.reattach();
        assert.equal(manager.view.element('apply').disabled, true, 'reopening must not unlock an active operation');
      } finally {
        finished.resolve();
      }
      const messageCount = manager.messages.length;
      const actionCount = manager.actions.length;
      await pending;
      assert.equal(manager.actions.length, actionCount, 'no follow-up writes or workflow reads may cross attachments');
      assert.ok(!manager.messages.slice(messageCount).some(message => message.type === 'data'));
      assert.match(manager.messages.at(-1)?.message ?? '', /workspace.*changed.*Refresh/i);
      manager.hooks[operation] = async () => {};
      await manager.dispatch({ type: 'refresh', hasPendingChanges: false });
      assert.equal(manager.messages.at(-1)?.type, 'data');
      assert.equal(manager.actions.at(-1)?.workspace, manager.workspace);
    });
  }
});

test('controller isolates remote bindings, confirms discards, serializes switching and blocks writes after failed loads', async t => {
  const workspace = { workspaceUri: 'file:///agent', displayName: 'Agent', syncInfo };
  const original = JSON.stringify(workspace);
  const deploymentContext: DeploymentContext = {
    ...syncInfo, agentSchemaName: 'agent_schema', currentDeploymentId: 'test',
    deployments: [
      { id: 'dev', name: 'develop', environmentId: 'dev-environment' },
      { id: 'test', name: 'testdeploy', environmentId: syncInfo.environmentId }
    ]
  };
  const listed: { workspace: typeof workspace; schema?: string }[] = [];
  const applied: { workspace: typeof workspace; schema?: string }[] = [];
  const added: typeof workspace[] = [];
  const connectorTargets: typeof workspace[] = [];
  const messages: {
    type: string; message?: string; loadingConnections?: boolean; loadingDeployments?: boolean;
    selectedDeploymentId?: string; context?: DeploymentContext;
  }[] = [];
  const notifications: { type: 'warning' | 'error'; message: string; lastMessageType?: string }[] = [];
  const informationMessages: string[] = [];
  const loggedErrors: { userMessage?: string; outputMessage?: string; error: unknown }[] = [];
  let configReads = 0;
  let delayEnvironmentNames = false;
  let namesStarted: (() => void) | undefined;
  let finishNames: (() => void) | undefined;
  let choice: string | undefined;
  let failRemote = false;
  let failEnvironment = false;
  let endpointMissing = false;
  let fallbackEndpointMissing = false;
  let fallbackError: Error | undefined;
  let fallbackCalls = 0;
  let chooseConnector: (() => Promise<string | undefined>) | undefined;
  let openDocument: (() => Promise<void>) | undefined;
  let agentExists = true;
  let failAgentAccess = false;
  let configurationLoadError: Error | undefined;
  let unavailableEnvironmentId: string | undefined;
  let applyStarted: (() => void) | undefined;
  let finishApply: (() => void) | undefined;
  const modules: Record<string, unknown> = {
    vscode: {
      Uri: {
        parse: (value: string) => ({ fsPath: value }),
        joinPath: (base: { fsPath: string }, filePath: string) => ({ fsPath: `${base.fsPath}/${filePath}` })
      }, ViewColumn: { Active: 1 },
      window: {
        createWebviewPanel: () => ({
          webview: { postMessage: (message: typeof messages[number]) => { messages.push(message); }, onDidReceiveMessage: () => ({ dispose() {} }) },
          onDidDispose: () => ({ dispose() {} })
        }),
        showWarningMessage: async (message: string, options: unknown, ...actions: { title: string }[]) => {
          if (!options) {
            notifications.push({ type: 'warning', message, lastMessageType: messages.at(-1)?.type });
          }
          return actions.find(action => action.title === choice);
        },
        showErrorMessage: async (message: string) => {
          notifications.push({ type: 'error', message, lastMessageType: messages.at(-1)?.type });
        },
        showInformationMessage: async (message: string) => { informationMessages.push(message); },
        showTextDocument: async () => {
          assert.ok(openDocument, 'Unexpected file navigation');
          await openDocument();
        }
      }
    },
    '../services/logger': {
      logError: (_event: unknown, userMessage: string | undefined, data: { message?: string; error: unknown }) => {
        loggedErrors.push({ userMessage, outputMessage: data.message, error: data.error });
      },
      logInfo() {}
    },
    '../constants': { TelemetryEventsKeys: {}, DefaultCoreServicesClusterCategory: 0 },
    '../utils/httpRequestError': { HttpRequestError },
    '../sync/localWorkspaces': { getWorkspaceByUri: () => workspace },
    '../types': {},
    './connectionCatalog': {
      listAgentConnections: async (target: typeof workspace, schema?: string) => {
        listed.push({ workspace: target, schema });
        if (failRemote && schema) { throw new Error('Remote access denied'); }
        if (failAgentAccess && schema) { throw new Error('Request failed with status 403'); }
        if (schema && (!agentExists || target.syncInfo.dataverseEndpoint === 'https://missing.example.com')) {
          throw new Error("The agent was not found in the selected deployment's environment.");
        }
        return [];
      },
      listWorkflowStatus: async () => [],
      createConnectionReference: async (target: typeof workspace) => {
        added.push(target);
        return { logicalName: 'agent.shared_test.new' };
      },
      applyConnectionBindings: async (target: typeof workspace, _bindings: unknown, schema?: string) => {
        applied.push({ workspace: target, schema });
        await new Promise<void>(resolve => { finishApply = resolve; applyStarted?.(); });
        return [];
      }
    },
    './connectionCreation': {}, './connectorReadiness': {},
    './connectorPicker': { pickConnector: async (target: typeof workspace) => {
      connectorTargets.push(target);
      return chooseConnector ? chooseConnector() : 'shared_test';
    } },
    './connectionManagerHtml': {},
    '../deployments/deploymentContext': {
      loadDeploymentContext: async () => {
        configReads++;
        if (configurationLoadError) {
          return { environmentId: syncInfo.environmentId, deployments: [], errorMessage: configurationLoadError.message };
        }
        return deploymentContext;
      },
      resolveEnvironmentNames: async (context: DeploymentContext) => {
        if (delayEnvironmentNames) {
          await new Promise<void>(resolve => { finishNames = resolve; namesStarted?.(); });
        }
        return { ...context, deployments: context.deployments.map(deployment => ({ ...deployment })) };
      }
    },
    '../clients/bapClient': {
      getEnvironmentEndpointByIdAsync: async (_category: unknown, environmentId: string) => {
        if (failEnvironment) { throw new Error('Environment access denied'); }
        return {
          dataverseUrl: environmentId === 'missing-environment' || environmentId === unavailableEnvironmentId
            ? 'https://missing.example.com' : 'https://remote.example.com',
          agentManagementUrl: endpointMissing ? undefined : 'https://remote-management.example.com'
        };
      },
      findEnvironmentEndpointInListAsync: async (
        category: unknown, environmentId: string, signal: AbortSignal, accountId: string | null, accountHint?: string
      ) => {
        fallbackCalls++;
        assert.ok(endpointMissing, 'Complete by-ID responses must not trigger the list fallback');
        assert.equal(category, syncInfo.accountInfo.clusterCategory ?? 0);
        assert.equal(environmentId, 'dev-environment');
        assert.equal(signal.aborted, false);
        assert.equal(accountId, null);
        assert.equal(accountHint, syncInfo.accountInfo.accountEmail);
        if (fallbackError) { throw fallbackError; }
        return {
          dataverseUrl: 'https://fallback.example.com',
          agentManagementUrl: fallbackEndpointMissing ? undefined : 'https://fallback-management.example.com'
        };
      }
    },
    '../clients/account': { resolveAccountIdentity: (account: unknown) => account },
    '../clients/dataverseClient': {
      getSolutionVersionsAsync: async () => syncInfo.solutionVersions
    }
  };
  const exports: Record<string, unknown> = {};
  const sandbox = createContext({
    exports, AbortSignal, Error,
    require: (id: string) => {
      assert.ok(id in modules, 'Unexpected dependency: ' + id);
      return modules[id];
    }
  });
  const compiled = await readFile(path.resolve(__dirname, '..', '..', 'connections', 'connectionManager.js'), 'utf8');
  runInContext(compiled, sandbox);
  const Controller = exports.ConnectionManagerController as new (context: unknown, target: unknown, html: () => string) => {
    onMessage(message: unknown): Promise<void>;
    createConnection(logicalName: string, connectorName: string): Promise<{ connectionReferenceLogicalName: string; connectionId: string }>;
  };
  const controller = new Controller({ extensionUri: 'extension' }, workspace, () => '');
  await controller.onMessage({ type: 'ready' });
  assert.equal(listed[0].workspace, workspace);
  assert.equal(listed[0].schema, undefined);
  assert.equal(messages.find(message => message.type === 'context')?.selectedDeploymentId, 'test', 'the controller lands on the connected deployment');
  assert.equal(messages.find(message => message.type === 'busy')?.loadingConnections, true);
  assert.equal(messages.find(message => message.type === 'busy')?.loadingDeployments, true);

  const readsBeforeRefresh = configReads;
  await controller.onMessage({ type: 'refreshDeployments', hasPendingChanges: true });
  assert.equal(messages.at(-1)?.type, 'selectionCancelled');
  assert.equal(configReads, readsBeforeRefresh, 'declining a refresh does not reload config or connections');
  assert.equal(listed.length, 1);
  deploymentContext.deployments.push({ id: 'prod', name: 'production', environmentId: 'prod-environment' });
  delayEnvironmentNames = true;
  choice = 'Discard changes and refresh';
  const start = messages.length;
  let refreshing: Promise<void> | undefined;
  await new Promise<void>(resolve => {
    namesStarted = resolve;
    refreshing = controller.onMessage({ type: 'refreshDeployments', hasPendingChanges: true });
  });
  assert.equal(messages.at(-1)?.loadingDeployments, true);
  assert.ok(!messages.slice(start).some(message => message.type === 'context'), 'deployment details wait for environment names');
  assert.ok(finishNames);
  finishNames();
  await refreshing;
  delayEnvironmentNames = false;
  assert.equal(configReads, readsBeforeRefresh + 1, 'Refresh reads the config again');
  assert.ok(messages.filter(message => message.type === 'context').at(-1)?.context?.deployments.some(deployment => deployment.id === 'prod'));
  assert.equal(listed.at(-1)?.workspace, workspace);
  assert.equal(messages.at(-1)?.type, 'data');
  const loadsAfterRefresh = listed.length;

  await controller.onMessage({ type: 'selectDeployment', deploymentId: 'dev', hasPendingChanges: true });
  assert.equal(messages.at(-1)?.type, 'selectionCancelled');
  assert.equal(listed.length, loadsAfterRefresh);
  choice = 'Discard changes and switch';
  await controller.onMessage({ type: 'selectDeployment', deploymentId: 'dev', hasPendingChanges: true });
  const remote = listed.at(-1);
  assert.equal(remote?.schema, 'agent_schema');
  assert.equal(remote?.workspace.syncInfo.environmentId, 'dev-environment');
  assert.equal(remote?.workspace.syncInfo.dataverseEndpoint, 'https://remote.example.com');
  assert.equal(remote?.workspace.syncInfo.agentId, undefined, 'the workspace agent ID must not be reused in another environment');
  assert.equal(remote?.workspace.syncInfo.accountInfo, syncInfo.accountInfo, 'deployment management reuses the workspace sign-in');

  await controller.onMessage({ type: 'addReference' });
  assert.equal(added.length, 1);
  assert.equal(added[0], workspace, 'adding from a deployment declares the reference in the original local workspace');
  assert.equal(connectorTargets[0], remote?.workspace, 'the connector picker uses the selected environment');
  assert.equal(listed.at(-1)?.schema, 'agent_schema', 'adding keeps the selected deployment');
  const targetAfterAdd = listed.at(-1)?.workspace;
  assert.equal(targetAfterAdd?.syncInfo.environmentId, remote?.workspace.syncInfo.environmentId);
  await controller.onMessage({ type: 'enableWorkflow' });
  assert.match(messages.at(-1)?.message ?? '', /workspace target/);
  let applying: Promise<void> | undefined;
  await new Promise<void>(resolve => {
    applyStarted = resolve;
    applying = controller.onMessage({ type: 'apply', bindings: [{ connectionReferenceLogicalName: 'remote_ref', connectionId: 'connection' }], creates: [] });
  });
  assert.ok(finishApply);
  await controller.onMessage({ type: 'selectDeployment', deploymentId: 'test', hasPendingChanges: false });
  finishApply();
  await applying;
  assert.equal(applied[0].workspace, targetAfterAdd);
  assert.equal(applied[0].schema, 'agent_schema');

  failRemote = true;
  await controller.onMessage({ type: 'refresh' });
  assert.equal(listed.at(-1)?.schema, 'agent_schema', 'a switch cannot race an in-flight binding update');
  assert.equal(messages.at(-1)?.type, 'error');
  await controller.onMessage({ type: 'apply', bindings: [{ connectionReferenceLogicalName: 'remote_ref', connectionId: 'connection' }], creates: [] });
  assert.equal(applied.length, 1, 'a failed load cannot write using a previous target');
  assert.equal(messages.at(-1)?.message, 'Remote access denied');

  await controller.onMessage({ type: 'selectDeployment', deploymentId: 'test', hasPendingChanges: false });
  assert.equal(listed.at(-1)?.workspace, workspace);
  assert.equal(listed.at(-1)?.schema, undefined);

  failRemote = false;
  await controller.onMessage({ type: 'selectDeployment', deploymentId: 'dev', hasPendingChanges: false });
  deploymentContext.currentDeploymentId = undefined;
  deploymentContext.deployments = [deploymentContext.deployments[0]];
  const singleDeploymentController = new Controller({ extensionUri: 'extension' }, workspace, () => '');
  await singleDeploymentController.onMessage({ type: 'ready' });
  assert.equal(listed.at(-1)?.schema, undefined);
  assert.equal(listed.at(-1)?.workspace, workspace);
  assert.equal(messages.filter(message => message.type === 'context').at(-1)?.selectedDeploymentId, '');

  await controller.onMessage({ type: 'refresh' });
  assert.equal(listed.at(-1)?.schema, 'agent_schema', 'a selected remote deployment remains selected when it becomes the sole slot');
  assert.equal(messages.filter(message => message.type === 'context').at(-1)?.selectedDeploymentId, 'dev');

  deploymentContext.deployments.push({ id: 'test', name: 'testdeploy', environmentId: syncInfo.environmentId });
  deploymentContext.currentDeploymentId = 'test';
  await singleDeploymentController.onMessage({ type: 'refresh' });
  await singleDeploymentController.onMessage({ type: 'selectDeployment', deploymentId: 'dev', hasPendingChanges: false });

  failEnvironment = true;
  await singleDeploymentController.onMessage({ type: 'refresh' });
  assert.equal(messages.at(-1)?.type, 'error');
  assert.match(messages.at(-1)?.message ?? '', /Environment access denied/);

  failEnvironment = false;
  agentExists = false;
  await singleDeploymentController.onMessage({ type: 'refreshDeployments', hasPendingChanges: false });
  let latest = messages.filter(message => message.type === 'context').at(-1);
  assert.deepEqual(Array.from(latest?.context?.deployments ?? [], deployment => deployment.id), ['dev', 'test']);
  assert.equal(latest?.selectedDeploymentId, 'dev', 'a missing agent does not remove an applicable deployment');
  assert.equal(latest?.context?.warningMessage, undefined);
  assert.equal(messages.at(-1)?.type, 'error');
  assert.match(messages.at(-1)?.message ?? '', /agent was not found/);
  agentExists = true;
  failAgentAccess = true;
  await singleDeploymentController.onMessage({ type: 'refreshDeployments', hasPendingChanges: false });
  latest = messages.filter(message => message.type === 'context').at(-1);
  assert.equal(latest?.selectedDeploymentId, 'dev');
  assert.match(messages.at(-1)?.message ?? '', /403/);
  failRemote = true;
  await singleDeploymentController.onMessage({ type: 'refresh' });
  latest = messages.filter(message => message.type === 'context').at(-1);
  assert.equal(latest?.selectedDeploymentId, 'dev');
  assert.equal(messages.at(-1)?.type, 'error');
  assert.equal(messages.at(-1)?.message, 'Remote access denied');
  const writesBeforeFailedSelection = applied.length;
  await singleDeploymentController.onMessage({ type: 'apply', bindings: [{ connectionReferenceLogicalName: 'remote_ref', connectionId: 'connection' }], creates: [] });
  assert.equal(applied.length, writesBeforeFailedSelection, 'a selectable target still cannot write after a failed connection load');
  assert.equal(messages.at(-1)?.message, 'Remote access denied');
  await singleDeploymentController.onMessage({ type: 'refreshDeployments', hasPendingChanges: false });
  latest = messages.filter(message => message.type === 'context').at(-1);
  assert.equal(latest?.selectedDeploymentId, 'dev');
  assert.equal(latest?.context?.warningMessage, undefined);
  assert.equal(messages.at(-1)?.message, 'Remote access denied');
  failRemote = false;
  failAgentAccess = false;
  await singleDeploymentController.onMessage({ type: 'refresh' });
  assert.equal(messages.at(-1)?.type, 'data');
  assert.equal(listed.at(-1)?.schema, 'agent_schema');
  await singleDeploymentController.onMessage({ type: 'refreshDeployments', hasPendingChanges: false });
  latest = messages.filter(message => message.type === 'context').at(-1);
  assert.equal(latest?.selectedDeploymentId, 'dev', 'retry retains the previously failing target');
  await singleDeploymentController.onMessage({ type: 'selectDeployment', deploymentId: 'dev', hasPendingChanges: false });
  assert.equal(listed.at(-1)?.schema, 'agent_schema');
  assert.equal(JSON.stringify(workspace), original);

  for (const outcome of ['resolved', 'missing', 'failed'] as const) {
    await t.test(`deployment endpoint list fallback: ${outcome}`, async child => {
      child.after(() => {
        endpointMissing = false;
        fallbackEndpointMissing = false;
        fallbackError = undefined;
      });
      const fallbackController = new Controller({ extensionUri: 'extension' }, workspace, () => '');
      await fallbackController.onMessage({ type: 'ready' });
      endpointMissing = true;
      fallbackEndpointMissing = outcome === 'missing';
      fallbackError = outcome === 'failed'
        ? new HttpRequestError(403, { error: { code: 'Forbidden', message: 'Deployment lookup denied' } })
        : undefined;
      const callsBefore = fallbackCalls;
      const loadsBefore = listed.length;
      const notificationsBefore = notifications.length;
      await fallbackController.onMessage({ type: 'selectDeployment', deploymentId: 'dev', hasPendingChanges: false });
      assert.equal(fallbackCalls, callsBefore + 1);
      if (outcome === 'resolved') {
        assert.equal(messages.at(-1)?.type, 'data');
        assert.equal(listed.at(-1)?.workspace.syncInfo.dataverseEndpoint, 'https://fallback.example.com');
        assert.equal(listed.at(-1)?.workspace.syncInfo.agentManagementEndpoint, 'https://fallback-management.example.com');
        assert.equal(listed.at(-1)?.schema, 'agent_schema');
      } else {
        assert.equal(listed.length, loadsBefore, 'Incomplete or inaccessible targets must not load connections');
        assert.equal(messages.at(-1)?.type, 'error');
        assert.equal(messages.at(-1)?.message, fallbackError instanceof HttpRequestError
          ? fallbackError.userMessage : 'The selected environment does not have Copilot Studio enabled.');
        if (fallbackError) {
          assert.equal(loggedErrors.at(-1)?.error, fallbackError, 'Output retains the original service diagnostics');
        }
      }
      assert.equal(notifications.length, notificationsBefore, 'Load failures remain inline and Output-only');
      assert.equal(JSON.stringify(workspace), original);
    });
  }

  await t.test('connector picker keeps controls busy until cancellation, then allows refresh and deployment switching', async child => {
    child.after(() => { chooseConnector = undefined; });
    const pickerController = new Controller({ extensionUri: 'extension' }, workspace, () => '');
    const view = createConnectionManagerView();
    let forwarded = messages.length;
    const forwardMessages = () => {
      for (const data of messages.slice(forwarded)) {
        view.onMessage({ data });
      }
      forwarded = messages.length;
    };
    await pickerController.onMessage({ type: 'ready' });
    forwardMessages();
    let finishPicker: ((value: string | undefined) => void) | undefined;
    let picking: Promise<void> | undefined;
    const referencesBefore = added.length;
    const loadsBefore = listed.length;
    await new Promise<void>(started => {
      chooseConnector = () => new Promise(resolve => {
        finishPicker = resolve;
        started();
      });
      picking = pickerController.onMessage({ type: 'addReference' });
    });
    forwardMessages();
    for (const id of ['refresh', 'refresh-deployments', 'deployment-select', 'apply', 'addRef']) {
      assert.equal(view.element(id).disabled, true, `${id} must be disabled while the picker is pending`);
    }
    const requestsBefore = view.messages.length;
    view.element('refresh').fire('click');
    view.element('refresh-deployments').fire('click');
    view.element('deployment-options').children[1].fire('click');
    assert.equal(view.messages.length, requestsBefore, 'Busy controls must not send requests the controller would drop');
    assert.ok(finishPicker);
    finishPicker(undefined);
    await picking;
    forwardMessages();
    assert.equal(added.length, referencesBefore);
    assert.equal(listed.length, loadsBefore, 'Cancelling does not reload or discard connection edits');
    for (const id of ['refresh', 'refresh-deployments', 'deployment-select', 'apply', 'addRef']) {
      assert.equal(view.element(id).disabled, false, `${id} must recover after cancellation`);
    }
    view.element('refresh').fire('click');
    await pickerController.onMessage(view.messages.at(-1));
    forwardMessages();
    assert.equal(listed.length, loadsBefore + 1);
    view.element('deployment-options').children[1].fire('click');
    assert.equal(view.messages.at(-1)?.deploymentId, 'dev');
    await pickerController.onMessage(view.messages.at(-1));
    forwardMessages();
    assert.equal(listed.at(-1)?.schema, 'agent_schema');
    assert.equal(listed.at(-1)?.workspace.syncInfo.environmentId, 'dev-environment');
    assert.equal(JSON.stringify(workspace), original);
  });

  for (const outcome of ['opened', 'failed'] as const) {
    await t.test(`usage navigation keeps controls busy and restores them when ${outcome}`, async child => {
      child.after(() => { openDocument = undefined; });
      const navigationController = new Controller({ extensionUri: 'extension' }, workspace, () => '');
      const view = createConnectionManagerView();
      let forwarded = messages.length;
      const forwardMessages = () => {
        for (const data of messages.slice(forwarded)) {
          view.onMessage({ data });
        }
        forwarded = messages.length;
      };
      await navigationController.onMessage({ type: 'ready' });
      forwardMessages();
      let finishNavigation: (() => void) | undefined;
      let navigating: Promise<void> | undefined;
      const loadsBefore = listed.length;
      await new Promise<void>(started => {
        openDocument = () => new Promise<void>((resolve, reject) => {
          finishNavigation = () => outcome === 'opened' ? resolve() : reject(new Error('File access denied'));
          started();
        });
        navigating = navigationController.onMessage({ type: 'openUsage', filePath: 'topics/topic.mcs.yml' });
      });
      forwardMessages();
      for (const id of ['refresh', 'refresh-deployments', 'deployment-select', 'apply', 'addRef']) {
        assert.equal(view.element(id).disabled, true, `${id} must be disabled during navigation`);
      }
      const requestsBefore = view.messages.length;
      view.element('refresh').fire('click');
      view.element('deployment-options').children[1].fire('click');
      assert.equal(view.messages.length, requestsBefore);
      assert.ok(finishNavigation);
      finishNavigation();
      await navigating;
      forwardMessages();
      for (const id of ['refresh', 'refresh-deployments', 'deployment-select', 'apply', 'addRef']) {
        assert.equal(view.element(id).disabled, false, `${id} must recover after navigation`);
      }
      assert.equal(listed.length, loadsBefore, 'Navigation must not reload or discard connection edits');
      assert.equal(view.element('banner').textContent, outcome === 'failed' ? 'File access denied' : '');
      view.element('refresh').fire('click');
      await navigationController.onMessage(view.messages.at(-1));
      forwardMessages();
      assert.equal(listed.length, loadsBefore + 1);
      assert.equal(listed.at(-1)?.workspace, workspace);
      assert.equal(JSON.stringify(workspace), original);
    });
  }

  for (const refreshId of ['refresh', 'refresh-deployments']) {
    await t.test(`${refreshId} safely transitions between multiple, sole remote, sole attached and zero deployments`, async child => {
      const value = config();
      const workspacePath = await fixture(child, value);
      const updateContext = async () => {
        await writeFile(path.join(workspacePath, '..', '.config', 'agent.config.json'), JSON.stringify(value));
        const context = await loadDeploymentContext(workspacePath, syncInfo);
        Object.assign(deploymentContext, context, { currentDeploymentId: context.currentDeploymentId, infoMessage: context.infoMessage, warningMessage: context.warningMessage });
      };
      await updateContext();
      const view = createConnectionManagerView();
      const transitionsController = new Controller({ extensionUri: 'extension' }, workspace, () => '');
      const dispatch = async (message: unknown) => {
        const start = messages.length;
        await transitionsController.onMessage(message);
        for (const data of messages.slice(start)) {
          view.onMessage({ data });
        }
      };
      const refresh = async () => {
        view.element(refreshId).fire('click');
        await dispatch(view.messages.at(-1));
      };
      await dispatch({ type: 'ready' });
      await dispatch({ type: 'selectDeployment', deploymentId: 'dev', hasPendingChanges: false });
      const writesBefore = applied.length;
      const notificationsBefore = notifications.length;
      runInContext("groups = [{ key: 'connector', references: [{ boundConnectionId: 'old' }] }]; views = groups[0].references;", view.sandbox);
      view.element('gsel-0').value = '__create__';
      value.agentDeployments = [value.agentDeployments[0]];
      await updateContext();
      choice = 'Stay';
      const readsBefore = configReads;
      await refresh();
      assert.equal(configReads, readsBefore);
      assert.equal(view.element('deployment-options').children.length, 2);
      assert.equal(view.element('gsel-0').value, '__create__');
      choice = 'Discard changes and refresh';
      await refresh();
      assert.equal(messages.at(-1)?.type, 'data');
      assert.equal(view.element('deployment-select').value, 'dev');
      assert.equal(view.element('deployment-options').children.length, 1);
      assert.ok(!view.element('deployments-section').hiddenClasses.has('hidden'));
      assert.ok(!view.element('use-workspace-environment').hiddenClasses.has('hidden'));
      assert.equal(runInContext('hasPendingChanges()', view.sandbox), false);
      assert.equal(notifications.length, notificationsBefore);
      assert.equal(listed.at(-1)?.schema, 'agent_schema');

      view.element('use-workspace-environment').fire('click');
      await dispatch(view.messages.at(-1));
      assert.equal(listed.at(-1)?.workspace, workspace);
      assert.equal(view.element('deployment-select').value, '');
      view.element('deployment-options').children[0].fire('click');
      await dispatch(view.messages.at(-1));
      assert.equal(listed.at(-1)?.workspace.syncInfo.environmentId, 'dev-environment');

      value.agentDeployments[0].environmentId = syncInfo.environmentId.toUpperCase();
      await updateContext();
      runInContext("groups = [{ key: 'connector', references: [{ boundConnectionId: 'old' }] }]; views = groups[0].references;", view.sandbox);
      view.element('gsel-0').value = '__create__';
      await refresh();
      assert.equal(messages.at(-1)?.type, 'data');
      assert.equal(view.element('deployment-select').value, '');
      assert.ok(view.element('deployments-section').hiddenClasses.has('hidden'));
      assert.equal(listed.at(-1)?.workspace, workspace);
      assert.equal(runInContext('hasPendingChanges()', view.sandbox), false);
      assert.equal(notifications.length, notificationsBefore + 1);
      assert.equal(notifications.at(-1)?.type, 'warning');
      assert.match(notifications.at(-1)?.message ?? '', /connection target changed/);

      const remote = { ...value.agentDeployments[0], environmentId: 'dev-environment' };
      value.agentDeployments = [];
      await updateContext();
      await refresh();
      assert.ok(view.element('deployments-section').hiddenClasses.has('hidden'));
      assert.equal(listed.at(-1)?.workspace, workspace);
      assert.equal(notifications.length, notificationsBefore + 1);
      value.agentDeployments = [remote];
      await updateContext();
      await refresh();
      assert.ok(!view.element('deployments-section').hiddenClasses.has('hidden'));
      assert.equal(view.element('deployment-select').value, '', 'new choices do not automatically switch away from workspace connections');
      assert.equal(listed.at(-1)?.workspace, workspace);
      view.element('deployment-options').children[0].fire('click');
      await dispatch(view.messages.at(-1));
      value.agentDeployments = [];
      await updateContext();
      await refresh();
      assert.equal(messages.at(-1)?.type, 'data');
      assert.ok(view.element('deployments-section').hiddenClasses.has('hidden'));
      assert.equal(view.element('deployment-select').value, '');
      assert.equal(listed.at(-1)?.workspace, workspace);
      assert.equal(notifications.length, notificationsBefore + 2);
      assert.equal(notifications.at(-1)?.type, 'error');
      assert.match(notifications.at(-1)?.message ?? '', /no longer available.*workspace's environment/);
      await refresh();
      assert.equal(notifications.length, notificationsBefore + 2);
      assert.equal(applied.length, writesBefore);
      assert.equal(JSON.stringify(workspace), original);
    });
  }

  await t.test('invalid configuration blocks stale Apply, then refresh falls back to workspace without a UI error', async child => {
    const workspacePath = await fixture(child, config());
    const context = await loadDeploymentContext(workspacePath, syncInfo);
    Object.assign(deploymentContext, context, { currentDeploymentId: context.currentDeploymentId, infoMessage: context.infoMessage, warningMessage: context.warningMessage });
    const view = createConnectionManagerView();
    const failedController = new Controller({ extensionUri: 'extension' }, workspace, () => '');
    const dispatch = async (message: unknown) => {
      const start = messages.length;
      await failedController.onMessage(message);
      for (const data of messages.slice(start)) {
        view.onMessage({ data });
      }
    };
    await dispatch({ type: 'ready' });
    await dispatch({ type: 'selectDeployment', deploymentId: 'dev', hasPendingChanges: false });
    const writesBefore = applied.length;
    const notificationsBefore = notifications.length;
    let creations = 0;
    failedController.createConnection = async logicalName => {
      creations++;
      return { connectionReferenceLogicalName: logicalName, connectionId: 'connection' };
    };
    configurationLoadError = new Error('Invalid hosting configuration');
    child.after(() => { configurationLoadError = undefined; choice = undefined; });
    const creates = [{ connectionReferenceLogicalName: 'remote_ref', connectorName: 'shared_test' }];
    await dispatch({ type: 'apply', bindings: [], creates });
    assert.equal(creations, 0, 'invalid configuration cannot apply pending deployment changes to the workspace');
    assert.equal(applied.length, writesBefore);
    assert.equal(view.element('deployment-select').value, 'dev');
    assert.equal(view.element('banner').textContent, '');
    assert.equal(notifications.length, notificationsBefore + 1, 'stale Apply still asks the user to refresh');

    choice = undefined;
    await dispatch({ type: 'refreshDeployments', hasPendingChanges: true });
    assert.equal(messages.at(-1)?.type, 'selectionCancelled');
    assert.equal(view.element('deployment-select').value, 'dev');
    const errorsBeforeRefresh = loggedErrors.length;
    choice = 'Discard changes and refresh';
    await dispatch({ type: 'refreshDeployments', hasPendingChanges: true });
    choice = undefined;
    assert.equal(messages.at(-1)?.type, 'data');
    assert.equal(view.element('deployment-select').value, '');
    assert.ok(view.element('deployments-section').hiddenClasses.has('hidden'));
    assert.equal(view.element('connections-title').textContent, 'Connections');
    assert.equal(view.element('banner').textContent, '');
    assert.equal(listed.at(-1)?.workspace, workspace);
    assert.equal(listed.at(-1)?.schema, undefined);
    assert.equal(view.element('refresh').disabled, false);
    assert.equal(view.element('refresh-deployments').disabled, false);
    assert.equal(loggedErrors.at(-1)?.userMessage, undefined, 'configuration errors are output-only');
    assert.equal(loggedErrors.at(-1)?.outputMessage, configurationLoadError.message);
    assert.equal(loggedErrors.at(-1)?.error, undefined);
    assert.equal(loggedErrors.length, errorsBeforeRefresh + 1, 'a failed configuration load is logged only once');
    const pickersBeforeActions = connectorTargets.length;
    const referencesBeforeActions = added.length;
    await dispatch({ type: 'addReference' });
    assert.equal(connectorTargets.length, pickersBeforeActions + 1, 'workspace operations continue despite invalid config');
    assert.equal(added.length, referencesBeforeActions + 1);
    assert.equal(added.at(-1), workspace);
    assert.equal(creations, 0);
    assert.equal(applied.length, writesBefore);
    assert.equal(notifications.length, notificationsBefore + 1, 'fallback does not show a configuration or removal notification');
    configurationLoadError = undefined;
    await dispatch({ type: 'refresh' });
    assert.equal(messages.at(-1)?.type, 'data');
    assert.equal(view.element('deployment-select').value, '', 'repairing config does not switch targets automatically');
    assert.equal(view.element('banner').textContent, '');
    assert.equal(listed.at(-1)?.schema, undefined);
    await dispatch({ type: 'selectDeployment', deploymentId: 'dev', hasPendingChanges: false });
    assert.equal(view.element('deployment-select').value, 'dev');
    assert.equal(listed.at(-1)?.schema, 'agent_schema');
    assert.equal(JSON.stringify(workspace), original);
  });

  await t.test('scenario 11: all unverified deployments retain a working workspace default and allow individual load attempts', async child => {
    const value = config();
    value.agentDeployments[1].environmentId = 'second-unverified-environment';
    const workspacePath = await fixture(child, value);
    const context = await loadDeploymentContext(workspacePath, syncInfo);
    assert.equal(context.currentDeploymentId, undefined);
    assert.equal(context.deployments.length, 2);
    assert.equal(context.infoMessage, 'No deployment targets the workspace environment.');
    Object.assign(deploymentContext, context);
    deploymentContext.currentDeploymentId = undefined;
    failEnvironment = true;
    const view = createConnectionManagerView();
    const allUnverifiedController = new Controller({ extensionUri: 'extension' }, workspace, () => '');
    const dispatch = async (message: unknown) => {
      const start = messages.length;
      await allUnverifiedController.onMessage(message);
      for (const data of messages.slice(start)) {
        view.onMessage({ data });
      }
    };

    await dispatch({ type: 'ready' });
    latest = messages.filter(message => message.type === 'context').at(-1);
    assert.equal(latest?.context?.deployments.length, 2);
    assert.equal(latest?.selectedDeploymentId, '');
    assert.equal(latest?.context?.infoMessage, 'No deployment targets the workspace environment.');
    assert.equal(messages.at(-1)?.type, 'data');
    assert.equal(listed.at(-1)?.workspace, workspace);
    assert.equal(listed.at(-1)?.schema, undefined);
    assert.equal(view.element('connections-title').textContent, 'Connections');
    assert.equal(view.element('deployment-selected-label').textContent, 'Select a deployment…');
    assert.equal(view.element('banner').textContent, '');
    assert.ok(view.element('sync-target-badge').hiddenClasses.has('hidden'));
    assert.equal(view.element('deployment-options').children.length, 2);

    for (const [index, deployment] of context.deployments.entries()) {
      const loadsBeforeAttempt = listed.length;
      const option = view.element('deployment-options').children[index];
      assert.equal(option.getAttribute('aria-disabled'), null);
      option.fire('click');
      assert.equal(view.messages.at(-1)?.deploymentId, deployment.id);
      await dispatch(view.messages.at(-1));
      latest = messages.filter(message => message.type === 'context').at(-1);
      assert.equal(latest?.selectedDeploymentId, deployment.id);
      assert.equal(view.element('connected-environment').textContent, ' \u2014 ' + deployment.environmentId);
      assert.equal(view.element('banner').textContent, 'Environment access denied');
      assert.equal(view.element('apply').disabled, false);
      assert.equal(listed.length, loadsBeforeAttempt, 'failed targets cannot reuse a previously loaded connection workspace');

      const refreshId = index === 0 ? 'refresh' : 'refresh-deployments';
      view.element(refreshId).fire('click');
      assert.equal(view.element('banner').textContent, '');
      assert.ok(!view.element('connection-loading').hiddenClasses.has('hidden'));
      await dispatch(view.messages.at(-1));
      latest = messages.filter(message => message.type === 'context').at(-1);
      assert.equal(latest?.selectedDeploymentId, deployment.id);
      assert.equal(view.element('banner').textContent, 'Environment access denied');
      assert.equal(listed.length, loadsBeforeAttempt);

      assert.ok(!view.element('use-workspace-environment').hiddenClasses.has('hidden'));
      view.element('use-workspace-environment').fire('click');
      assert.equal(view.messages.at(-1)?.deploymentId, '');
      await dispatch(view.messages.at(-1));
      assert.equal(messages.at(-1)?.type, 'data');
      assert.equal(view.element('deployment-selected-label').textContent, 'Select a deployment…');
      assert.ok(view.element('use-workspace-environment').hiddenClasses.has('hidden'));
      assert.equal(view.element('banner').textContent, '');
      assert.equal(listed.at(-1)?.workspace, workspace);
      assert.equal(listed.at(-1)?.schema, undefined);
    }
    assert.equal(JSON.stringify(workspace), original);
  });

  await t.test('scenario 12: existing deployments without an attachment match keep workspace default and load either target', async child => {
    const value = config();
    value.agentDeployments[1].environmentId = 'second-remote-environment';
    const workspacePath = await fixture(child, value);
    const context = await loadDeploymentContext(workspacePath, syncInfo);
    assert.equal(context.currentDeploymentId, undefined);
    assert.equal(context.infoMessage, 'No deployment targets the workspace environment.');
    Object.assign(deploymentContext, context);
    deploymentContext.currentDeploymentId = undefined;
    failEnvironment = false;
    const view = createConnectionManagerView();
    const noAttachmentController = new Controller({ extensionUri: 'extension' }, workspace, () => '');
    const dispatch = async (message: unknown) => {
      const start = messages.length;
      await noAttachmentController.onMessage(message);
      for (const data of messages.slice(start)) {
        view.onMessage({ data });
      }
    };

    await dispatch({ type: 'ready' });
    latest = messages.filter(message => message.type === 'context').at(-1);
    assert.equal(latest?.context?.deployments.length, 2);
    assert.equal(latest?.selectedDeploymentId, '');
    assert.equal(view.element('connections-title').textContent, 'Connections');
    assert.equal(view.element('deployment-selected-label').textContent, 'Select a deployment…');
    assert.equal(view.element('deployment-options').children.length, 2);
    assert.ok(view.element('sync-target-badge').hiddenClasses.has('hidden'));
    assert.equal(view.element('deployment-info').textContent, 'No deployment targets the workspace environment.');
    assert.equal(listed.at(-1)?.workspace, workspace);

    for (const [index, deployment] of context.deployments.entries()) {
      view.element('deployment-options').children[index].fire('click');
      await dispatch(view.messages.at(-1));
      assert.equal(messages.at(-1)?.type, 'data');
      assert.equal(listed.at(-1)?.workspace.syncInfo.environmentId, deployment.environmentId);
      assert.equal(listed.at(-1)?.schema, 'agent_schema');
      assert.equal(view.element('connections-title').textContent, `Connections in ${deployment.name} [${deployment.type}]`);
      assert.equal(view.element('banner').textContent, '');
      assert.ok(view.element('sync-target-badge').hiddenClasses.has('hidden'));
      view.element(index === 0 ? 'refresh' : 'refresh-deployments').fire('click');
      await dispatch(view.messages.at(-1));
      latest = messages.filter(message => message.type === 'context').at(-1);
      assert.equal(latest?.selectedDeploymentId, deployment.id);
      assert.equal(listed.at(-1)?.workspace.syncInfo.environmentId, deployment.environmentId);

      view.element('use-workspace-environment').fire('click');
      await dispatch(view.messages.at(-1));
      assert.equal(messages.at(-1)?.type, 'data');
      assert.equal(listed.at(-1)?.workspace, workspace);
      assert.equal(listed.at(-1)?.schema, undefined);
      assert.equal(view.element('connections-title').textContent, 'Connections');
      assert.equal(view.element('deployment-selected-label').textContent, 'Select a deployment…');
    }
    assert.equal(JSON.stringify(workspace), original);
  });

  await t.test('scenario 13: duplicate attachment matches are tagged without selecting a unique deployment', async child => {
    const value = config();
    value.agentDeployments.push({
      ...value.agentDeployments[1], deploymentId: 'duplicate', deploymentName: 'alternate'
    });
    const workspacePath = await fixture(child, value);
    const context = await loadDeploymentContext(workspacePath, syncInfo);
    assert.equal(context.currentDeploymentId, undefined);
    assert.equal(context.warningMessage, "Multiple deployments target the workspace environment.");
    Object.assign(deploymentContext, context);
    deploymentContext.infoMessage = context.infoMessage;
    deploymentContext.currentDeploymentId = undefined;
    const view = createConnectionManagerView();
    const ambiguousController = new Controller({ extensionUri: 'extension' }, workspace, () => '');
    const dispatch = async (message: unknown) => {
      const start = messages.length;
      await ambiguousController.onMessage(message);
      for (const data of messages.slice(start)) {
        view.onMessage({ data });
      }
    };

    await dispatch({ type: 'ready' });
    latest = messages.filter(message => message.type === 'context').at(-1);
    assert.equal(latest?.selectedDeploymentId, '');
    assert.equal(latest?.context?.currentDeploymentId, undefined);
    assert.equal(view.element('connections-title').textContent, 'Connections');
    assert.equal(view.element('deployment-selected-label').textContent, 'Select a deployment…');
    assert.equal(view.element('deployment-warning').textContent, context.warningMessage);
    assert.equal(listed.at(-1)?.workspace, workspace);
    const options = view.element('deployment-options').children;
    assert.equal(options.length, 3);
    assert.deepEqual(options.map(option => option.children[0].textContent),
      ['testdeploy [test] [test]', 'alternate [test] [duplicate]', 'develop [dev] [dev]'],
      'all environment matches come first, preserving their configured order');
    assert.equal(options[2].children.length, 1, 'another environment has no attachment badge');
    for (const option of options.slice(0, 2)) {
      assert.equal(option.children[1].textContent, 'Attached \u26a0');
      assert.equal(option.children[1].className, 'deployment-badge ambiguous');
    }
    assert.ok(options.every(option => option.getAttribute('aria-selected') === 'false'));
    assert.equal(view.element('connection-scope').textContent, '');
    assert.ok(view.element('sync-target-badge').hiddenClasses.has('hidden'));
    assert.ok(view.element('use-workspace-environment').hiddenClasses.has('hidden'));

    const orderedDeployments = [context.deployments[1], context.deployments[2], context.deployments[0]];
    for (const [index, deployment] of orderedDeployments.entries()) {
      view.element('deployment-options').children[index].fire('click');
      await dispatch(view.messages.at(-1));
      assert.equal(messages.at(-1)?.type, 'data');
      assert.equal(listed.at(-1)?.workspace.syncInfo.environmentId, deployment.environmentId);
      assert.equal(listed.at(-1)?.schema, 'agent_schema');
      assert.equal(view.element('deployment-select').value, deployment.id);
      const matchesWorkspace = deployment.environmentId.toLowerCase() === syncInfo.environmentId.toLowerCase();
      assert.equal(view.element('sync-target-badge').hiddenClasses.has('hidden'), !matchesWorkspace);
      assert.equal(view.element('connection-scope').textContent, matchesWorkspace ? 'This deployment targets the workspace environment. Connection changes here also apply to the workspace.' : '');
      if (matchesWorkspace) {
        assert.equal(view.element('sync-target-badge').textContent, 'Attached \u26a0');
        assert.equal(view.element('sync-target-badge').className, 'deployment-badge ambiguous');
        assert.match(view.element('deployment-select').getAttribute('aria-label') ?? '', /Attached \u26a0/);
      }
      assert.ok(!view.element('use-workspace-environment').hiddenClasses.has('hidden'), 'ambiguous matches must retain an explicit workspace return path');
      view.element('refresh-deployments').fire('click');
      await dispatch(view.messages.at(-1));
      assert.equal(view.element('deployment-select').value, deployment.id, 'refresh retains an explicit choice without inventing an attachment');
      assert.equal(view.element('deployment-warning').textContent, context.warningMessage);
      assert.equal(view.element('connection-scope').hiddenClasses.has('hidden'), !matchesWorkspace);
      assert.equal(view.element('sync-target-badge').hiddenClasses.has('hidden'), !matchesWorkspace);
      assert.ok(!view.element('use-workspace-environment').hiddenClasses.has('hidden'));
      const workspaceAction = view.element('use-workspace-environment');
      runInContext("groups = [{ key: 'connector', references: [{ boundConnectionId: 'old' }] }]; views = groups[0].references;", view.sandbox);
      view.element('gsel-0').value = '__create__';
      choice = undefined;
      workspaceAction.fire('click');
      assert.equal(view.messages.at(-1)?.hasPendingChanges, true);
      await dispatch(view.messages.at(-1));
      assert.equal(view.element('deployment-select').value, deployment.id, 'declining preserves the deployment and edits');
      assert.equal(view.element('gsel-0').value, '__create__');
      choice = 'Discard changes and switch';
      workspaceAction.fire('click');
      assert.equal(view.messages.at(-1)?.deploymentId, '');
      await dispatch(view.messages.at(-1));
      choice = undefined;
      assert.equal(listed.at(-1)?.workspace, workspace);
      assert.equal(listed.at(-1)?.schema, undefined, 'workspace mode must not retain deployment scoping');
      assert.equal(view.element('connections-title').textContent, 'Connections');
      assert.equal(view.element('deployment-select').value, '');
      assert.ok(workspaceAction.hiddenClasses.has('hidden'));
    }
    assert.equal(JSON.stringify(workspace), original);
  });

  for (const { attached, filterMissing } of [
    { attached: false, filterMissing: false },
    { attached: true, filterMissing: false },
    { attached: false, filterMissing: true }
  ]) {
    await t.test(`scenario 14: duplicate ${attached ? 'attached' : 'remote'} slots remain selectable${filterMissing ? ' alongside a missing agent' : ''}`, async child => {
      const value = config();
      const workspacePath = await fixture(child, value);
      const initialContext = await loadDeploymentContext(workspacePath, syncInfo);
      Object.assign(deploymentContext, initialContext);
      deploymentContext.infoMessage = initialContext.infoMessage;
      deploymentContext.warningMessage = initialContext.warningMessage;
      const view = createConnectionManagerView();
      const duplicateEnvironmentController = new Controller({ extensionUri: 'extension' }, workspace, () => '');
      const dispatch = async (message: unknown) => {
        const start = messages.length;
        await duplicateEnvironmentController.onMessage(message);
        for (const data of messages.slice(start)) {
          view.onMessage({ data });
        }
      };
      await dispatch({ type: 'ready' });
      const target = value.agentDeployments[attached ? 1 : 0];
      await dispatch({ type: 'selectDeployment', deploymentId: target.deploymentId, hasPendingChanges: false });
      value.agentDeployments = [
        target,
        { ...target, deploymentId: 'duplicate', deploymentName: 'alternate', environmentId: target.environmentId.toLowerCase() === target.environmentId
          ? target.environmentId.toUpperCase() : target.environmentId.toLowerCase() }
      ];
      if (filterMissing) {
        value.agentDeployments.push({ ...target, deploymentId: 'missing', environmentId: 'missing-environment' });
      }
      await writeFile(path.join(workspacePath, '..', '.config', 'agent.config.json'), JSON.stringify(value));
      const context = await loadDeploymentContext(workspacePath, syncInfo);
      Object.assign(deploymentContext, context);
      deploymentContext.currentDeploymentId = undefined;
      deploymentContext.infoMessage = context.infoMessage;
      deploymentContext.warningMessage = context.warningMessage;
      await dispatch({ type: 'refreshDeployments', hasPendingChanges: false });
      latest = messages.filter(message => message.type === 'context').at(-1);
      assert.equal(latest?.selectedDeploymentId, target.deploymentId, 'a valid selection survives duplicate-only configuration');
      assert.equal(latest?.context?.deployments.length, filterMissing ? 3 : 2);
      assert.equal(latest?.context?.warningMessage, context.warningMessage);
      assert.equal(latest?.context?.infoMessage, context.infoMessage);
      assert.equal(listed.at(-1)?.schema, 'agent_schema');
      assert.equal(messages.at(-1)?.type, 'data');
      assert.ok(!view.element('deployments-section').hiddenClasses.has('hidden'));
      assert.ok(!view.element('deployment-picker').hiddenClasses.has('hidden'));
      assert.equal(view.element('deployment-options').children.length, filterMissing ? 3 : 2);
      const firstTarget = listed.at(-1);
      for (const [index, deployment] of context.deployments.slice(0, 2).entries()) {
        const option = view.element('deployment-options').children[index];
        assert.equal(option.getAttribute('aria-disabled'), null);
        if (attached) {
          assert.equal(option.children[1].textContent, 'Attached \u26a0');
        } else {
          assert.equal(option.children.length, 1);
        }
        if (view.element('deployment-select').value !== deployment.id) {
          option.fire('click');
          await dispatch(view.messages.at(-1));
        }
        assert.equal(view.element('deployment-select').value, deployment.id);
        assert.equal(view.element('connections-title').textContent, `Connections in ${deployment.name} [${deployment.type}]`);
        assert.equal(view.element('connected-environment').textContent, ' \u2014 ' + deployment.environmentId);
        assert.equal(view.element('use-workspace-environment').hiddenClasses.has('hidden'), false);
        assert.equal(listed.at(-1)?.workspace.syncInfo.environmentId.toLowerCase(), target.environmentId.toLowerCase());
        assert.equal(listed.at(-1)?.workspace.syncInfo.dataverseEndpoint, firstTarget?.workspace.syncInfo.dataverseEndpoint);
        assert.equal(listed.at(-1)?.schema, firstTarget?.schema);
        let applying: Promise<void> | undefined;
        await new Promise<void>(resolve => {
          applyStarted = resolve;
          applying = dispatch({
            type: 'apply', bindings: [{ connectionReferenceLogicalName: 'remote_ref', connectionId: 'connection' }], creates: []
          });
        });
        assert.ok(finishApply);
        finishApply();
        await applying;
        assert.equal(applied.at(-1)?.workspace.syncInfo.environmentId.toLowerCase(), target.environmentId.toLowerCase());
        assert.equal(applied.at(-1)?.workspace.syncInfo.dataverseEndpoint, firstTarget?.workspace.syncInfo.dataverseEndpoint);
        assert.equal(applied.at(-1)?.schema, 'agent_schema');
        assert.equal(view.element('banner').textContent, '');
        view.element('refresh-deployments').fire('click');
        await dispatch(view.messages.at(-1));
        assert.equal(view.element('deployment-select').value, deployment.id);
      }
      assert.equal(JSON.stringify(workspace), original);
    });
  }

  for (const refreshId of ['refresh', 'refresh-deployments']) {
    await t.test(`scenario 15: ${refreshId} protects edits and updates config without losing a valid selection`, async child => {
      const value = config();
      const workspacePath = await fixture(child, value);
      const initialContext = await loadDeploymentContext(workspacePath, syncInfo);
      Object.assign(deploymentContext, initialContext);
      deploymentContext.infoMessage = initialContext.infoMessage;
      deploymentContext.warningMessage = initialContext.warningMessage;
      const view = createConnectionManagerView();
      const refreshController = new Controller({ extensionUri: 'extension' }, workspace, () => '');
      const dispatch = async (message: unknown) => {
        const start = messages.length;
        await refreshController.onMessage(message);
        for (const data of messages.slice(start)) {
          view.onMessage({ data });
        }
      };
      await dispatch({ type: 'ready' });
      await dispatch({ type: 'selectDeployment', deploymentId: 'dev', hasPendingChanges: false });
      runInContext("groups = [{ key: 'connector', references: [{ boundConnectionId: 'old' }] }]; views = groups[0].references;", view.sandbox);
      view.element('gsel-0').value = '__create__';
      runInContext("setBusy(false, '');", view.sandbox);
      assert.equal(view.element('apply').disabled, false);

      value.agentDeployments[0].deploymentName = 'renamed';
      value.agentDeployments.push({
        ...value.agentDeployments[0], deploymentId: 'prod', deploymentName: 'production', environmentId: 'prod-environment'
      });
      await writeFile(path.join(workspacePath, '..', '.config', 'agent.config.json'), JSON.stringify(value));
      Object.assign(deploymentContext, await loadDeploymentContext(workspacePath, syncInfo));
      const readsBeforeRefresh = configReads;
      const loadsBeforeRefresh = listed.length;
      const writesBeforeRefresh = applied.length;
      choice = 'Stay';
      view.element(refreshId).fire('click');
      assert.equal(view.messages.at(-1)?.hasPendingChanges, true);
      assert.ok(!view.element('content').hiddenClasses.has('hidden'), 'pending edits stay visible during confirmation');
      assert.equal(runInContext('hasPendingChanges()', view.sandbox), true);
      await dispatch(view.messages.at(-1));
      assert.equal(messages.at(-1)?.type, 'selectionCancelled');
      assert.equal(configReads, readsBeforeRefresh);
      assert.equal(listed.length, loadsBeforeRefresh);
      assert.equal(view.element('deployment-options').children.length, 2);
      assert.equal(view.element('connections-title').textContent, 'Connections in develop [dev]');
      assert.equal(view.element('deployment-select').value, 'dev');
      assert.equal(view.element('gsel-0').value, '__create__');
      assert.equal(view.element('apply').disabled, false);
      assert.equal(runInContext('hasPendingChanges()', view.sandbox), true);

      choice = 'Discard changes and refresh';
      view.element(refreshId).fire('click');
      await dispatch(view.messages.at(-1));
      assert.equal(configReads, readsBeforeRefresh + 1);
      assert.equal(listed.length, loadsBeforeRefresh + 1);
      assert.equal(applied.length, writesBeforeRefresh, 'discarding never applies a binding or creates a connection');
      assert.equal(messages.at(-1)?.type, 'data');
      assert.equal(view.element('deployment-options').children.length, 3);
      assert.equal(view.element('deployment-select').value, 'dev');
      assert.equal(view.element('connections-title').textContent, 'Connections in renamed [dev]');
      assert.equal(listed.at(-1)?.workspace.syncInfo.environmentId, 'dev-environment');
      assert.equal(listed.at(-1)?.schema, 'agent_schema');
      assert.equal(runInContext('hasPendingChanges()', view.sandbox), false);
      assert.equal(view.element('apply').disabled, false);
      assert.equal(JSON.stringify(workspace), original);
    });
  }

  for (const change of ['removed', 'retargeted', 'schema-retargeted', 'retargeted-unavailable', 'unavailable', 'unverified'] as const) {
    await t.test(`scenario 16: ${change} selection cannot silently reuse stale bindings`, async child => {
      const value = config();
      value.agentDeployments.push({
        ...value.agentDeployments[0], deploymentId: 'prod', deploymentName: 'production', environmentId: 'prod-environment'
      });
      const workspacePath = await fixture(child, value);
      const initialContext = await loadDeploymentContext(workspacePath, syncInfo);
      Object.assign(deploymentContext, initialContext);
      deploymentContext.infoMessage = initialContext.infoMessage;
      deploymentContext.warningMessage = initialContext.warningMessage;
      const view = createConnectionManagerView();
      const staleController = new Controller({ extensionUri: 'extension' }, workspace, () => '');
      const dispatch = async (message: unknown) => {
        const start = messages.length;
        await staleController.onMessage(message);
        for (const data of messages.slice(start)) {
          view.onMessage({ data });
        }
      };
      await dispatch({ type: 'ready' });
      await dispatch({ type: 'selectDeployment', deploymentId: 'dev', hasPendingChanges: false });
      runInContext("groups = [{ key: 'connector', references: [{ boundConnectionId: 'old' }] }]; views = groups[0].references;", view.sandbox);
      view.element('gsel-0').value = '__create__';
      runInContext("setBusy(false, '');", view.sandbox);

      if (change === 'removed') {
        value.agentDeployments = value.agentDeployments.filter(deployment => deployment.deploymentId !== 'dev');
      } else if (change === 'retargeted' || change === 'retargeted-unavailable') {
        value.agentDeployments[0].environmentId = 'retargeted-environment';
        if (change === 'retargeted-unavailable') {
          unavailableEnvironmentId = 'retargeted-environment';
        }
      } else if (change === 'schema-retargeted') {
        value.managedAgents[0].mcsSchemaName = 'retargeted_schema';
      } else if (change === 'unavailable') {
        unavailableEnvironmentId = 'dev-environment';
      } else {
        failAgentAccess = true;
      }
      await writeFile(path.join(workspacePath, '..', '.config', 'agent.config.json'), JSON.stringify(value));
      Object.assign(deploymentContext, await loadDeploymentContext(workspacePath, syncInfo));
      if (change === 'retargeted' || change === 'schema-retargeted') {
        deploymentContext.deployments[0].environmentName = change === 'retargeted' ? 'Retargeted Environment' : 'Development Environment';
      }
      const readsBeforeRefresh = configReads;
      const loadsBeforeRefresh = listed.length;
      const writesBeforeRefresh = applied.length;
      const notificationsBeforeRefresh = notifications.length;
      choice = 'Stay';
      view.element('refresh-deployments').fire('click');
      await dispatch(view.messages.at(-1));
      assert.equal(messages.at(-1)?.type, 'selectionCancelled');
      assert.equal(configReads, readsBeforeRefresh);
      assert.equal(listed.length, loadsBeforeRefresh);
      assert.equal(view.element('deployment-select').value, 'dev');
      assert.equal(view.element('gsel-0').value, '__create__');
      assert.equal(view.element('apply').disabled, false);
      assert.equal(notifications.length, notificationsBeforeRefresh, 'declining does not announce a target change');

      if (change === 'removed' || change === 'retargeted' || change === 'schema-retargeted' || change === 'retargeted-unavailable') {
        for (const creates of [true, false]) {
          const notificationsBeforeApply = notifications.length;
          const messagesBeforeApply = messages.length;
          await dispatch({
            type: 'apply',
            bindings: creates ? [] : [{ connectionReferenceLogicalName: 'remote_ref', connectionId: 'connection' }],
            creates: creates ? [{ connectionReferenceLogicalName: 'remote_ref', connectorName: 'shared_test' }] : []
          });
          assert.equal(messages.at(-1)?.type, 'busy');
          assert.equal(notifications.length, notificationsBeforeApply + 1);
          assert.equal(notifications.at(-1)?.type, 'error');
          assert.equal(notifications.at(-1)?.message,
            'The selected deployment was not found. Refresh deployments and try again.');
          assert.equal(loggedErrors.at(-1)?.userMessage, undefined, 'diagnostic logging must not produce a second notification');
          assert.ok(!messages.slice(messagesBeforeApply).some(message => message.type === 'error'));
          assert.equal(applied.length, writesBeforeRefresh);
          assert.equal(view.element('apply').disabled, false);
          assert.equal(view.element('gsel-0').value, '__create__');
          assert.equal(runInContext('hasPendingChanges()', view.sandbox), true);
          assert.equal(view.element('banner').textContent, '');
        }
      }
      const notificationsBeforeReload = notifications.length;
      choice = 'Discard changes and refresh';
      view.element('refresh-deployments').fire('click');
      await dispatch(view.messages.at(-1));
      const loadFailed = change === 'unverified' || change === 'unavailable' || change === 'retargeted-unavailable';
      assert.equal(messages.at(-1)?.type, loadFailed ? 'error' : 'data');
      assert.equal(applied.length, writesBeforeRefresh);
      assert.equal(runInContext('hasPendingChanges()', view.sandbox), false);
      const latest = messages.filter(message => message.type === 'context').at(-1);
      assert.equal(latest?.context?.warningMessage, undefined);
      assert.equal(view.element('deployment-warning').textContent, '');
      if (loadFailed) {
        assert.equal(latest?.selectedDeploymentId, 'dev');
        assert.equal(view.element('deployment-select').value, 'dev');
        assert.match(messages.at(-1)?.message ?? '', change === 'unverified' ? /403/ : /agent was not found/);
        assert.equal(notifications.length, notificationsBeforeReload);
        assert.equal(listed.at(-1)?.workspace.syncInfo.environmentId,
          change === 'retargeted-unavailable' ? 'retargeted-environment' : 'dev-environment');
        assert.equal(listed.at(-1)?.schema, 'agent_schema');
      } else if (change === 'retargeted' || change === 'schema-retargeted') {
        assert.equal(latest?.selectedDeploymentId, 'dev');
        assert.equal(view.element('deployment-select').value, 'dev');
        assert.equal(listed.at(-1)?.workspace.syncInfo.environmentId, change === 'retargeted' ? 'retargeted-environment' : 'dev-environment');
        assert.equal(listed.at(-1)?.schema, change === 'retargeted' ? 'agent_schema' : 'retargeted_schema');
        assert.equal(notifications.length, notificationsBeforeReload + 1);
        assert.equal(notifications.at(-1)?.type, 'warning');
        assert.equal(notifications.at(-1)?.message,
          "The selected deployment's connection target changed. Connections have been reloaded for "
          + (change === 'retargeted' ? 'Retargeted Environment.' : 'Development Environment.'));
        assert.equal(notifications.at(-1)?.lastMessageType, 'data', 'only announce a successful reload');
      } else {
        assert.equal(latest?.selectedDeploymentId, 'test');
        assert.equal(notifications.length, notificationsBeforeReload + 1);
        assert.equal(notifications.at(-1)?.type, 'error');
        assert.equal(notifications.at(-1)?.message,
          "The selected deployment is no longer available. Connections have been reloaded for the workspace's environment.");
        assert.equal(listed.at(-1)?.workspace, workspace);
        assert.equal(listed.at(-1)?.schema, undefined);
        assert.equal(view.element('deployment-select').value, 'test');
        assert.ok(!view.element('deployments-section').hiddenClasses.has('hidden'));
      }
      const notificationsAfterRefresh = notifications.length;
      view.element('refresh-deployments').fire('click');
      await dispatch(view.messages.at(-1));
      assert.equal(notifications.length, notificationsAfterRefresh, 'target-change notifications do not repeat on another refresh');
      if (change === 'removed' || change === 'retargeted' || change === 'schema-retargeted') {
        const refreshedTarget = listed.at(-1);
        applyStarted = () => finishApply?.();
        await dispatch({
          type: 'apply', bindings: [{ connectionReferenceLogicalName: 'remote_ref', connectionId: 'connection' }], creates: []
        });
        assert.equal(applied.length, writesBeforeRefresh + 1, 'a successful refresh permits a new explicit Apply');
        assert.equal(applied.at(-1)?.workspace.syncInfo.environmentId, refreshedTarget?.workspace.syncInfo.environmentId);
        assert.equal(applied.at(-1)?.schema, refreshedTarget?.schema);
      }
      assert.equal(JSON.stringify(workspace), original);
      unavailableEnvironmentId = undefined;
      failAgentAccess = false;
    });
  }

  await t.test('retargeted deployment stays selected on load failure without announcing a successful reload', async child => {
    const workspacePath = await fixture(child, config());
    const initialContext = await loadDeploymentContext(workspacePath, syncInfo);
    Object.assign(deploymentContext, initialContext);
    deploymentContext.infoMessage = initialContext.infoMessage;
    deploymentContext.warningMessage = initialContext.warningMessage;
    const failedController = new Controller({ extensionUri: 'extension' }, workspace, () => '');
    await failedController.onMessage({ type: 'ready' });
    await failedController.onMessage({ type: 'selectDeployment', deploymentId: 'dev', hasPendingChanges: false });
    deploymentContext.deployments = deploymentContext.deployments.map(deployment =>
      deployment.id === 'dev' ? { ...deployment, environmentId: 'retargeted-environment' } : deployment);
    const notificationsBeforeRefresh = notifications.length;
    const writesBeforeRefresh = applied.length;
    failRemote = true;
    await failedController.onMessage({ type: 'refresh', hasPendingChanges: false });
    const latest = messages.filter(message => message.type === 'context').at(-1);
    assert.equal(latest?.selectedDeploymentId, 'dev');
    assert.equal(latest?.context?.warningMessage, undefined);
    assert.equal(listed.at(-1)?.workspace.syncInfo.environmentId, 'retargeted-environment');
    assert.equal(messages.at(-1)?.type, 'error');
    assert.equal(messages.at(-1)?.message, 'Remote access denied');
    assert.equal(notifications.length, notificationsBeforeRefresh);
    await failedController.onMessage({
      type: 'apply', bindings: [{ connectionReferenceLogicalName: 'remote_ref', connectionId: 'connection' }], creates: []
    });
    assert.equal(applied.length, writesBeforeRefresh);
    assert.equal(messages.at(-1)?.message, 'Remote access denied');
    assert.equal(JSON.stringify(workspace), original);
    failRemote = false;
  });

  for (const change of ['environment', 'schema'] as const) {
    await t.test(`scenario 16: ${change} retargeting during connection creation blocks the subsequent binding write`, async child => {
      const value = config();
      const workspacePath = await fixture(child, value);
      const initialContext = await loadDeploymentContext(workspacePath, syncInfo);
      Object.assign(deploymentContext, initialContext);
      deploymentContext.infoMessage = initialContext.infoMessage;
      deploymentContext.warningMessage = initialContext.warningMessage;
      const creatingController = new Controller({ extensionUri: 'extension' }, workspace, () => '');
      await creatingController.onMessage({ type: 'ready' });
      await creatingController.onMessage({ type: 'selectDeployment', deploymentId: 'dev', hasPendingChanges: false });
      let creations = 0;
      creatingController.createConnection = async logicalName => {
        creations++;
        if (change === 'environment') {
          value.agentDeployments[0].environmentId = 'retargeted-environment';
        } else {
          value.managedAgents[0].mcsSchemaName = 'retargeted_schema';
        }
        await writeFile(path.join(workspacePath, '..', '.config', 'agent.config.json'), JSON.stringify(value));
        Object.assign(deploymentContext, await loadDeploymentContext(workspacePath, syncInfo));
        return { connectionReferenceLogicalName: logicalName, connectionId: 'new-connection' };
      };
      applyStarted = () => finishApply?.();
      const writesBefore = applied.length;
      await creatingController.onMessage({
        type: 'apply', bindings: [], creates: [{ connectionReferenceLogicalName: 'remote_ref', connectorName: 'shared_test' }]
      });
      assert.equal(creations, 1);
      assert.equal(applied.length, writesBefore);
      assert.equal(messages.at(-1)?.type, 'busy');
      assert.equal(notifications.at(-1)?.type, 'error');
      assert.match(notifications.at(-1)?.message ?? '', /deployment was not found/);
      Object.assign(deploymentContext, initialContext);
      const notificationsBeforeRetry = notifications.length;
      await creatingController.onMessage({
        type: 'apply', bindings: [], creates: [{ connectionReferenceLogicalName: 'remote_ref', connectorName: 'shared_test' }]
      });
      assert.equal(creations, 1, 'a stale creation attempt cannot open another browser before refresh');
      assert.equal(applied.length, writesBefore);
      assert.equal(notifications.length, notificationsBeforeRetry + 1);
      assert.match(notifications.at(-1)?.message ?? '', /Refresh deployments and try again/);
      assert.equal(JSON.stringify(workspace), original);
    });
  }

  await t.test('Apply without pending changes gives feedback without creating connections or writing bindings', async child => {
    const workspacePath = await fixture(child, config());
    const initialContext = await loadDeploymentContext(workspacePath, syncInfo);
    Object.assign(deploymentContext, initialContext);
    deploymentContext.infoMessage = initialContext.infoMessage;
    deploymentContext.warningMessage = initialContext.warningMessage;
    const idleController = new Controller({ extensionUri: 'extension' }, workspace, () => '');
    await idleController.onMessage({ type: 'ready' });
    const writesBefore = applied.length;
    await idleController.onMessage({ type: 'apply', bindings: [], creates: [] });
    assert.equal(applied.length, writesBefore);
    assert.equal(informationMessages.at(-1), 'There are no connection changes to apply.');
    assert.equal(messages.at(-1)?.type, 'busy');
  });
});
