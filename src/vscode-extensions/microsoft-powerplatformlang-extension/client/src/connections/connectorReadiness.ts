import * as vscode from 'vscode';
import { TelemetryEventsKeys } from '../constants';
import logger from '../services/logger';
import { CopilotStudioWorkspace } from '../sync/localWorkspaces';
import { listConnectors } from './connectionCatalog';

const CUSTOM_CONNECTOR_INTERNAL_ID_REGEX = /-5f[0-9a-f]{16}$/i;
const DEFAULT_READINESS_TIMEOUT_MS = 30_000;
const INITIAL_POLL_DELAY_MS = 1_000;
const MAX_POLL_DELAY_MS = 3_000;

export interface ConnectorReadinessOptions {
  timeoutMs?: number;
  cancellationToken?: vscode.CancellationToken;
}

const lastSegment = (value: string): string => {
  const trimmed = value.trim().replace(/\/+$/, '');
  const slash = trimmed.lastIndexOf('/');
  return slash >= 0 ? trimmed.substring(slash + 1) : trimmed;
};

export const isCustomConnectorInternalId = (connectorName: string): boolean =>
  CUSTOM_CONNECTOR_INTERNAL_ID_REGEX.test(lastSegment(connectorName));

const delay = (ms: number, token?: vscode.CancellationToken): Promise<void> =>
  new Promise<void>((resolve) => {
    const timer = setTimeout(() => {
      registration?.dispose();
      resolve();
    }, ms);
    const registration = token?.onCancellationRequested(() => {
      clearTimeout(timer);
      registration?.dispose();
      resolve();
    });
  });

export const waitForCustomConnectorReady = async (
  workspace: CopilotStudioWorkspace,
  connectorInternalId: string,
  options?: ConnectorReadinessOptions
): Promise<void> => {
  if (!isCustomConnectorInternalId(connectorInternalId)) {
    return;
  }

  const target = lastSegment(connectorInternalId).toLowerCase();
  const timeoutMs = options?.timeoutMs ?? DEFAULT_READINESS_TIMEOUT_MS;
  const deadline = Date.now() + timeoutMs;
  let nextDelay = INITIAL_POLL_DELAY_MS;

  while (!options?.cancellationToken?.isCancellationRequested) {
    try {
      const connectors = await listConnectors(workspace);
      if (connectors.some((connector) => lastSegment(connector.internalId).toLowerCase() === target)) {
        return;
      }
    } catch (error) {
      logger.logInfo(
        TelemetryEventsKeys.ConnectionCreationInfo,
        `Custom connector readiness check failed for <pii>${target}</pii>: ${(error as Error).message}`
      );
    }

    const remaining = deadline - Date.now();
    if (remaining <= 0) {
      logger.logInfo(
        TelemetryEventsKeys.ConnectionCreationInfo,
        `Custom connector <pii>${target}</pii> was not available within ${timeoutMs} ms; proceeding anyway.`
      );
      return;
    }

    await delay(Math.min(nextDelay, remaining), options?.cancellationToken);
    nextDelay = Math.min(Math.floor(nextDelay * 1.5), MAX_POLL_DELAY_MS);
  }
};
