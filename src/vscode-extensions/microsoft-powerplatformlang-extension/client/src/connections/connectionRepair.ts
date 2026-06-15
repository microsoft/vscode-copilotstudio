import * as vscode from 'vscode';
import { ConnectionBinding, ConnectionNeeded, EnvironmentInfo } from '../types';
import { CoreServicesClusterCategory, TelemetryEventsKeys } from '../constants';
import logger from '../services/logger';
import { awaitConnectionCreation } from './connectionCreation';
import { connectionExists, isCustomConnectorInternalId, waitForConnectorAvailable } from './connectionExistence';

export type ConnectionRepairAccount = { accountId?: string; accountEmail?: string };

export interface ConnectionRepairResult {
  bindings: ConnectionBinding[];
  unfinished: string[];
}

const resolveConnectionLogicalName = (connectionName?: string, connectionId?: string): string => {
  if (connectionName) {
    return connectionName;
  }
  if (connectionId) {
    const segments = connectionId.split('/').filter(s => s.length > 0);
    if (segments.length > 0) {
      return segments[segments.length - 1];
    }
  }
  return '';
};

const resolveConnectionsNeeding = async (
  agentConnections: ConnectionNeeded[],
  environmentInfo: EnvironmentInfo,
  clusterCategory: CoreServicesClusterCategory,
  account: ConnectionRepairAccount | undefined
): Promise<{ needed: ConnectionNeeded[]; unverified: ConnectionNeeded[] }> => {
  const needed: ConnectionNeeded[] = [];
  const unverified: ConnectionNeeded[] = [];
  for (const connection of agentConnections) {
    if (!connection.boundConnectionId) {
      needed.push(connection);
      continue;
    }

    const exists = await connectionExists({
      connectorName: connection.connectorName || connection.connectorId,
      connectionId: connection.boundConnectionId,
      environmentId: environmentInfo.environmentId,
      clusterCategory,
      accountId: account?.accountId ?? null,
      accountHint: account?.accountEmail
    });

    if (exists === false) {
      needed.push(connection);
    } else if (exists === undefined) {
      unverified.push(connection);
    }
  }
  return { needed, unverified };
};

const createConnections = async (
  connectionsNeeded: ConnectionNeeded[],
  environmentInfo: EnvironmentInfo,
  clusterCategory: CoreServicesClusterCategory
): Promise<ConnectionRepairResult> => {
  const count = connectionsNeeded.length;
  if (count === 0) {
    return { bindings: [], unfinished: [] };
  }

  return vscode.window.withProgress(
    {
      location: vscode.ProgressLocation.Notification,
      title: 'Creating connections',
      cancellable: true
    },
    async (progress, token) => {
      const bindings: ConnectionBinding[] = [];
      const unfinished: string[] = [];

      for (let i = 0; i < connectionsNeeded.length; i++) {
        const connection = connectionsNeeded[i];
        const label = connection.connectorName || connection.connectorId || connection.connectionReferenceLogicalName;

        if (token.isCancellationRequested) {
          for (let j = i; j < connectionsNeeded.length; j++) {
            unfinished.push(connectionsNeeded[j].connectionReferenceLogicalName);
          }
          break;
        }

        progress.report({ message: `${i + 1} of ${count}: ${label} — complete it in your browser...` });

        const result = await awaitConnectionCreation({
          connectorName: connection.connectorName || connection.connectorId,
          environmentId: environmentInfo.environmentId,
          clusterCategory,
          cancellationToken: token
        });

        if (result.status === 'cancelled') {
          logger.logInfo(TelemetryEventsKeys.ConnectionCreationInfo, `Connection creation cancelled for <pii>${connection.connectionReferenceLogicalName}</pii>.`);
          unfinished.push(connection.connectionReferenceLogicalName);
          continue;
        }

        if (result.status === 'error') {
          logger.logError(TelemetryEventsKeys.ConnectionCreationError, `Connection creation failed for <pii>${connection.connectionReferenceLogicalName}</pii>: ${result.errorMessage ?? 'Unknown error'}`);
          unfinished.push(connection.connectionReferenceLogicalName);
          continue;
        }

        const connectionLogicalName = resolveConnectionLogicalName(result.connectionName, result.connectionId);
        if (!connectionLogicalName) {
          logger.logError(TelemetryEventsKeys.ConnectionCreationError, `Connection created but no connection identifier was returned for <pii>${connection.connectionReferenceLogicalName}</pii>.`);
          unfinished.push(connection.connectionReferenceLogicalName);
          continue;
        }

        bindings.push({
          connectionReferenceLogicalName: connection.connectionReferenceLogicalName,
          connectionLogicalName,
          connectionDisplayName: result.displayName || connection.connectorName || undefined
        });
      }

      return { bindings, unfinished };
    }
  );
};

const waitForCustomConnectorsAvailable = async (
  connectionsNeeded: ConnectionNeeded[],
  environmentInfo: EnvironmentInfo,
  clusterCategory: CoreServicesClusterCategory,
  account: ConnectionRepairAccount | undefined
): Promise<void> => {
  const customConnectors = new Set<string>();
  for (const connection of connectionsNeeded) {
    const connectorName = connection.connectorName || connection.connectorId;
    if (connectorName && isCustomConnectorInternalId(connectorName)) {
      customConnectors.add(connectorName);
    }
  }

  if (customConnectors.size === 0) {
    return;
  }

  await vscode.window.withProgress(
    {
      location: vscode.ProgressLocation.Notification,
      title: 'Waiting for custom connectors to be ready...',
      cancellable: true
    },
    async (_progress, token) => {
      await Promise.all(
        Array.from(customConnectors, (connectorName) =>
          waitForConnectorAvailable({
            connectorName,
            environmentId: environmentInfo.environmentId,
            clusterCategory,
            accountId: account?.accountId ?? null,
            accountHint: account?.accountEmail,
            cancellationToken: token
          })
        )
      );
    }
  );
};

export const createAgentConnections = async (
  agentConnections: ConnectionNeeded[],
  environmentInfo: EnvironmentInfo,
  clusterCategory: CoreServicesClusterCategory,
  account: ConnectionRepairAccount | undefined
): Promise<ConnectionRepairResult> => {
  const { needed, unverified } = await resolveConnectionsNeeding(agentConnections, environmentInfo, clusterCategory, account);

  const unverifiedNames = unverified.map(c => c.connectionReferenceLogicalName);
  for (const connection of unverified) {
    logger.logWarning(TelemetryEventsKeys.ConnectionCreationError, `Could not verify whether the connection for <pii>${connection.connectionReferenceLogicalName}</pii> still exists; skipping re-creation.`);
  }

  if (needed.length === 0) {
    return { bindings: [], unfinished: unverifiedNames };
  }

  await waitForCustomConnectorsAvailable(needed, environmentInfo, clusterCategory, account);
  const result = await createConnections(needed, environmentInfo, clusterCategory);
  return { bindings: result.bindings, unfinished: [...result.unfinished, ...unverifiedNames] };
};
