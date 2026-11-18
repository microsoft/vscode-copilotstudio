import * as assert from 'node:assert/strict';
import { writeFile } from 'node:fs/promises';
import * as path from 'node:path';
import { test } from 'node:test';
import { loadDeploymentContext, resolveEnvironmentNames } from '../../deployments/deploymentContext';
import { config, fixture, syncInfo } from './helpers/deploymentFixtures';

test('matches the connection environment, not the first or merely enabled deployment', async t => {
  const workspacePath = await fixture(t, config());
  const result = await loadDeploymentContext(workspacePath, syncInfo);

  assert.equal(result.currentDeploymentId, 'test');
  assert.deepEqual(result.deployments.map(deployment => deployment.name), ['develop', 'testdeploy']);
  assert.equal(result.warningMessage, undefined);
  assert.equal(result.environmentId, syncInfo.environmentId);
  assert.equal(result.agentSchemaName, 'agent_schema');
  assert.equal(result.matchingAgentCount, 1);
});

test('does not claim a unique deployment when multiple deployments share the environment', async t => {
  const value = config();
  value.agentDeployments[0].environmentId = syncInfo.environmentId;
  const workspacePath = await fixture(t, value);
  const result = await loadDeploymentContext(workspacePath, syncInfo);

  assert.equal(result.currentDeploymentId, undefined);
  assert.equal(result.warningMessage, "Multiple deployments target the workspace environment.");
});

test('retains the actual connection target when no deployment matches or no config exists', async t => {
  const workspacePath = await fixture(t, config());
  const result = await loadDeploymentContext(workspacePath, { ...syncInfo, environmentId: 'another-environment' });
  assert.equal(result.currentDeploymentId, undefined);
  assert.equal(result.environmentId, 'another-environment');
  assert.equal(result.deployments.length, 2);
  assert.equal(result.infoMessage, "No deployment targets the workspace environment.");
  assert.equal(result.warningMessage, undefined);
  assert.equal(result.matchingAgentCount, 1);

  const unconfigured = await fixture(t);
  const withoutConfig = await loadDeploymentContext(unconfigured, syncInfo);
  assert.deepEqual(withoutConfig.deployments, []);
  assert.equal(withoutConfig.environmentId, syncInfo.environmentId);
  assert.equal(withoutConfig.warningMessage, undefined);
  assert.equal(withoutConfig.matchingAgentCount, undefined);
});

test('does not associate a different tenant or MCS agent with the same folder', async t => {
  const workspacePath = await fixture(t, config());
  for (const identity of [
    { ...syncInfo, agentId: 'different-mcs-agent' },
    { ...syncInfo, accountInfo: { tenantId: 'different-tenant' } }
  ]) {
    const result = await loadDeploymentContext(workspacePath, identity);
    assert.deepEqual(result.deployments, []);
    assert.equal(result.currentDeploymentId, undefined);
    assert.equal(result.warningMessage, undefined);
    assert.equal(result.agentSchemaName, undefined);
    assert.equal(result.matchingAgentCount, 0);
  }
});

test('does not combine deployments when multiple managed agents match the workspace', async t => {
  const value = config();
  value.managedAgents.push({ ...value.managedAgents[0], agentId: 'duplicate-agent' });
  value.agentDeployments.push({ ...value.agentDeployments[0], agentId: 'duplicate-agent', deploymentId: 'duplicate-dev' });
  const workspacePath = await fixture(t, value);
  const result = await loadDeploymentContext(workspacePath, syncInfo);
  assert.deepEqual(result.deployments, []);
  assert.equal(result.currentDeploymentId, undefined);
  assert.equal(result.warningMessage, undefined);
  assert.equal(result.errorMessage, 'Found 2 managed agents matching this workspace in agent.config.json.');
  assert.equal(result.matchingAgentCount, 2);
});

test('invalid configuration returns zero deployments and a diagnostic instead of rejecting', async t => {
  const workspacePath = await fixture(t, { managedAgents: [], agentDeployments: 'invalid' });
  const invalid = await loadDeploymentContext(workspacePath, syncInfo);
  assert.deepEqual(invalid.deployments, []);
  assert.match(invalid.errorMessage ?? '', /Invalid agent.config.json/);
  assert.equal(invalid.warningMessage, undefined);

  await writeFile(path.join(workspacePath, '..', '.config', 'agent.config.json'), '{');
  const malformed = await loadDeploymentContext(workspacePath, syncInfo);
  assert.deepEqual(malformed.deployments, []);
  assert.match(malformed.errorMessage ?? '', /JSON/);
  assert.equal(malformed.agentSchemaName, undefined);
});

test('invalid records discard the whole configuration, including malformed unrelated deployments', async t => {
  for (const contents of [
    { ...config(), managedAgents: [null] },
    { ...config(), managedAgents: [{ ...config().managedAgents[0], mcsSchemaName: 42 }] },
    { ...config(), agentDeployments: [null] },
    { ...config(), agentDeployments: [{ ...config().agentDeployments[0], deploymentId: 42 }] },
    { ...config(), agentDeployments: [{ ...config().agentDeployments[0], environmentId: 42 }] },
    { ...config(), agentDeployments: [...config().agentDeployments, { agentId: 'unrelated', deploymentId: false }] }
  ]) {
    const workspacePath = await fixture(t, contents);
    const result = await loadDeploymentContext(workspacePath, syncInfo);
    assert.deepEqual(result.deployments, []);
    assert.match(result.errorMessage ?? '', /Invalid agent.config.json/);
    assert.equal(result.agentSchemaName, undefined);
    assert.equal(result.warningMessage, undefined);
  }
});

test('resolves environment names once per environment and reuses a known workspace name', async t => {
  const workspacePath = await fixture(t, config());
  const context = await loadDeploymentContext(workspacePath, syncInfo);
  const calls: string[] = [];
  const result = await resolveEnvironmentNames({ ...context, environmentName: 'Test Environment' }, async id => {
    calls.push(id);
    return 'Development Environment';
  });
  assert.deepEqual(calls, ['dev-environment']);
  assert.equal(result.environmentName, 'Test Environment');
  assert.deepEqual(result.deployments.map(deployment => deployment.environmentName), ['Development Environment', 'Test Environment']);

  calls.length = 0;
  await resolveEnvironmentNames(context, async id => { calls.push(id.toLowerCase()); return 'Environment'; });
  assert.deepEqual(calls.sort(), ['dev-environment', 'test-environment']);

  const unavailable = await resolveEnvironmentNames(context, async () => undefined);
  assert.equal(unavailable.environmentId, syncInfo.environmentId);
  assert.equal(unavailable.environmentName, undefined);
  assert.equal(unavailable.deployments[0].environmentId, 'dev-environment');
});

test('single-deployment discovery preserves the configured target and workspace association', async t => {
  for (const index of [0, 1]) {
    const value = config();
    const deployment = value.agentDeployments[index];
    value.agentDeployments = [deployment];
    const workspacePath = await fixture(t, value);
    const result = await loadDeploymentContext(workspacePath, syncInfo);
    assert.deepEqual(result.deployments, [{
      id: deployment.deploymentId,
      name: deployment.deploymentName,
      type: deployment.deploymentType,
      environmentId: deployment.environmentId
    }]);
    assert.equal(result.currentDeploymentId, index === 1 ? deployment.deploymentId : undefined);
    assert.equal(result.environmentId, syncInfo.environmentId);
  }
});

test('scenario 14: retains duplicate-environment entries regardless of workspace attachment', async t => {
  for (const attached of [false, true]) {
    const deployment = config().agentDeployments[attached ? 1 : 0];
    const value = config();
    value.agentDeployments = [
      deployment,
      { ...deployment, deploymentId: 'duplicate', deploymentName: 'alternate', environmentId: deployment.environmentId.toLowerCase() === deployment.environmentId
        ? deployment.environmentId.toUpperCase() : deployment.environmentId.toLowerCase() }
    ];
    const workspacePath = await fixture(t, value);
    const context = await loadDeploymentContext(workspacePath, syncInfo);
    assert.deepEqual(context.deployments.map(item => item.id), [deployment.deploymentId, 'duplicate']);
    assert.equal(context.currentDeploymentId, undefined);
    assert.equal(context.warningMessage, attached ? "Multiple deployments target the workspace environment." : undefined);
    assert.equal(context.infoMessage, attached ? undefined : "No deployment targets the workspace environment.");
  }
});
