import { readFile } from 'node:fs/promises';
import * as path from 'node:path';

interface ManagedAgentConfig {
  agentId: string;
  agentLocation: string;
  mcsAgentId?: string;
  mcsSchemaName?: string;
  tenantId?: string;
}

interface AgentDeploymentConfig {
  agentId: string;
  tenantId?: string;
  deploymentId: string;
  environmentId: string;
  deploymentName?: string;
  deploymentType?: string;
}

export interface AgentHostingConfig {
  directory: string;
  managedAgents: ManagedAgentConfig[];
  agentDeployments: AgentDeploymentConfig[];
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

function readString(record: Record<string, unknown>, key: string, optional: true): string | undefined;
function readString(record: Record<string, unknown>, key: string): string;
function readString(record: Record<string, unknown>, key: string, optional = false): string | undefined {
  const value = record[key];
  if (typeof value === 'string' || (optional && value === undefined)) {
    return value;
  }
  throw new Error(`Invalid agent.config.json: ${key} must be a string.`);
}

function readManagedAgent(value: unknown): ManagedAgentConfig {
  if (!isRecord(value)) {
    throw new Error('Invalid agent.config.json: each managed agent must be an object.');
  }
  return {
    agentId: readString(value, 'agentId'),
    agentLocation: readString(value, 'agentLocation'),
    mcsAgentId: readString(value, 'mcsAgentId', true),
    mcsSchemaName: readString(value, 'mcsSchemaName', true),
    tenantId: readString(value, 'tenantId', true)
  };
}

function readDeployment(value: unknown): AgentDeploymentConfig {
  if (!isRecord(value)) {
    throw new Error('Invalid agent.config.json: each deployment must be an object.');
  }
  return {
    agentId: readString(value, 'agentId'),
    tenantId: readString(value, 'tenantId', true),
    deploymentId: readString(value, 'deploymentId'),
    environmentId: readString(value, 'environmentId'),
    deploymentName: readString(value, 'deploymentName', true),
    deploymentType: readString(value, 'deploymentType', true)
  };
}

export async function findAgentHostingConfig(workspacePath: string): Promise<AgentHostingConfig | undefined> {
  let directory = path.resolve(workspacePath);
  while (true) {
    let contents: string;
    try {
      contents = await readFile(path.join(directory, '.config', 'agent.config.json'), 'utf8');
    } catch (error) {
      if (!isRecord(error) || error.code !== 'ENOENT') {
        throw error;
      }
      const parent = path.dirname(directory);
      if (parent === directory) {
        return undefined;
      }
      directory = parent;
      continue;
    }

    let config: unknown;
    try {
      config = JSON.parse(contents.replace(/^\uFEFF/, ''));
    } catch {
      throw new SyntaxError('Invalid agent.config.json: malformed JSON.');
    }
    if (!isRecord(config) || !Array.isArray(config.managedAgents) || !Array.isArray(config.agentDeployments)) {
      throw new Error('Invalid agent.config.json: managedAgents and agentDeployments must be arrays.');
    }
    return {
      directory,
      managedAgents: config.managedAgents.map(readManagedAgent),
      agentDeployments: config.agentDeployments.map(readDeployment)
    };
  }
}
