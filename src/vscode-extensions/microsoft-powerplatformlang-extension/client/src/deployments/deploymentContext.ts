import * as path from 'node:path';
import type { AgentSyncInfo } from '../types';
import { findAgentHostingConfig, type AgentHostingConfig } from './agentHostingConfig';

export interface Deployment {
  id: string;
  name: string;
  type?: string;
  environmentId: string;
  environmentName?: string;
}

export interface DeploymentContext {
  deployments: Deployment[];
  environmentId: string;
  environmentName?: string;
  currentDeploymentId?: string;
  infoMessage?: string;
  warningMessage?: string;
  errorMessage?: string;
  matchingAgentCount?: number;
  agentSchemaName?: string;
}

const sameId = (left: string, right: string): boolean => left.toLowerCase() === right.toLowerCase();

const samePath = (left: string, right: string): boolean =>
  process.platform === 'win32' ? sameId(left, right) : left === right;

export async function resolveEnvironmentNames(
  context: DeploymentContext,
  lookup: (environmentId: string) => Promise<string | undefined>
): Promise<DeploymentContext> {
  const environments = new Map(
    [context.environmentId, ...context.deployments.map(deployment => deployment.environmentId)]
      .map(id => [id.toLowerCase(), id])
  );
  const names = new Map<string, string | undefined>();
  if (context.environmentName?.trim()) {
    names.set(context.environmentId.toLowerCase(), context.environmentName);
  }
  await Promise.all([...environments].map(async ([key, id]) => {
    if (!names.has(key)) {
      names.set(key, (await lookup(id))?.trim() || undefined);
    }
  }));
  return {
    ...context,
    environmentName: names.get(context.environmentId.toLowerCase()),
    deployments: context.deployments.map(deployment => ({
      ...deployment,
      environmentName: names.get(deployment.environmentId.toLowerCase())
    }))
  };
}

export async function loadDeploymentContext(
  workspacePath: string,
  syncInfo: Pick<AgentSyncInfo, 'environmentId' | 'environmentDisplayName' | 'agentId' | 'accountInfo'>
): Promise<DeploymentContext> {
  const context: DeploymentContext = {
    environmentId: syncInfo.environmentId,
    environmentName: syncInfo.environmentDisplayName,
    deployments: []
  };
  const workspaceRoot = path.resolve(workspacePath);
  let config: AgentHostingConfig | undefined;
  try {
    config = await findAgentHostingConfig(workspaceRoot);
  } catch (error) {
    return { ...context, errorMessage: error instanceof Error ? error.message : String(error) };
  }
  if (!config) {
    return context;
  }
  const agents = config.managedAgents.filter(agent =>
    samePath(path.resolve(config.directory, agent.agentLocation), workspaceRoot)
    && (agent.mcsAgentId === undefined || (!!syncInfo.agentId && sameId(agent.mcsAgentId, syncInfo.agentId)))
    && (agent.tenantId === undefined || sameId(agent.tenantId, syncInfo.accountInfo.tenantId)));

  context.matchingAgentCount = agents.length;
  if (agents.length === 0) {
    return context;
  }
  if (agents.length > 1) {
    context.errorMessage = `Found ${agents.length} managed agents matching this workspace in agent.config.json.`;
    return context;
  }
  const agent = agents[0];
  context.agentSchemaName = agent.mcsSchemaName;

  for (const deployment of config.agentDeployments) {
    if (!sameId(deployment.agentId, agent.agentId)
      || (deployment.tenantId !== undefined && !sameId(deployment.tenantId, syncInfo.accountInfo.tenantId))) {
      continue;
    }
    context.deployments.push({
      id: deployment.deploymentId,
      name: deployment.deploymentName ?? deployment.deploymentId,
      type: deployment.deploymentType,
      environmentId: deployment.environmentId
    });
  }

  if (context.deployments.length === 0) {
    return context;
  }

  const matching = context.deployments.filter(deployment => sameId(deployment.environmentId, syncInfo.environmentId));
  if (matching.length === 1) {
    context.currentDeploymentId = matching[0].id;
  } else if (matching.length > 1) {
    context.warningMessage = 'Multiple deployments target the workspace environment.';
  } else {
    context.infoMessage = 'No deployment targets the workspace environment.';
  }
  return context;
}
