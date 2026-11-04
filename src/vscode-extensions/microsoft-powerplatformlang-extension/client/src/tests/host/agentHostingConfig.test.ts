import * as assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import * as path from 'node:path';
import { test } from 'node:test';
import { findAgentHostingConfig } from '../../deployments/agentHostingConfig';
import { loadDeploymentContext } from '../../deployments/deploymentContext';
import { config, fixture, syncInfo } from './helpers/deploymentFixtures';

test('hosting configuration lookup accepts a BOM and uses the nearest config without hiding parse failures', async t => {
  const workspacePath = await fixture(t);
  const parentConfig = path.join(workspacePath, '..', '.config', 'agent.config.json');
  await mkdir(path.dirname(parentConfig));
  await writeFile(parentConfig, '\uFEFF' + JSON.stringify(config()));
  assert.equal((await findAgentHostingConfig(workspacePath))?.directory, path.dirname(workspacePath));
  assert.equal((await loadDeploymentContext(workspacePath, syncInfo)).deployments.length, 2);

  const nearestConfig = path.join(workspacePath, '.config', 'agent.config.json');
  await mkdir(path.dirname(nearestConfig));
  await writeFile(nearestConfig, JSON.stringify({ managedAgents: [], agentDeployments: [] }));
  assert.equal((await findAgentHostingConfig(workspacePath))?.directory, workspacePath);
  assert.deepEqual((await loadDeploymentContext(workspacePath, syncInfo)).deployments, []);
  await writeFile(nearestConfig, '{');
  await assert.rejects(findAgentHostingConfig(workspacePath), SyntaxError);
  assert.deepEqual((await loadDeploymentContext(workspacePath, syncInfo)).deployments, []);
});
