import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import * as path from 'node:path';
import type { AgentSyncInfo } from '../../../types';

export const syncInfo: AgentSyncInfo = {
  environmentId: 'test-environment',
  dataverseEndpoint: 'https://test.example.com',
  agentManagementEndpoint: 'https://management.example.com',
  agentId: 'mcs-agent',
  accountInfo: { tenantId: 'tenant', accountEmail: 'user@example.com' },
  solutionVersions: { solutionVersions: {}, copilotStudioSolutionVersion: '' }
};

export const config = () => ({
  schemaVersion: 3,
  managedAgents: [
    { agentId: 'hosting-agent', agentLocation: 'agent', mcsAgentId: 'mcs-agent', mcsSchemaName: 'agent_schema', tenantId: 'tenant' },
    { agentId: 'other-agent', agentLocation: 'other', mcsAgentId: 'other-mcs-agent', tenantId: 'tenant' }
  ],
  agentDeployments: [
    { deploymentId: 'dev', agentId: 'hosting-agent', deploymentName: 'develop', deploymentType: 'dev', environmentId: 'dev-environment', isEnabled: true },
    { deploymentId: 'test', agentId: 'hosting-agent', deploymentName: 'testdeploy', deploymentType: 'test', environmentId: 'TEST-ENVIRONMENT', isEnabled: true },
    { deploymentId: 'other', agentId: 'other-agent', deploymentName: 'unrelated', environmentId: 'test-environment', isEnabled: true }
  ]
});

export async function fixture(t: { after: (fn: () => Promise<void>) => void }, contents?: unknown): Promise<string> {
  const root = await mkdtemp(path.join(tmpdir(), 'mcs-deployment-context-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  await mkdir(path.join(root, 'agent'));
  if (contents !== undefined) {
    await mkdir(path.join(root, '.config'));
    await writeFile(path.join(root, '.config', 'agent.config.json'), JSON.stringify(contents));
  }
  return path.join(root, 'agent');
}
