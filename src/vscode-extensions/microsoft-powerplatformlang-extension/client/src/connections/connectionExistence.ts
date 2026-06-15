import { CancellationToken, Uri } from 'vscode';
import { FetchAccessToken } from '../clients/account';
import { getTokenScopeHostName } from '../clients/bapClient';
import { CoreServicesClusterCategory, TelemetryEventsKeys } from '../constants';
import logger from '../services/logger';

const CONNECTIONS_API_VERSION = '2016-11-01';

const getConnectionsApiHost = (clusterCategory: CoreServicesClusterCategory): string => {
  switch (clusterCategory) {
    case CoreServicesClusterCategory.Gov:
    case CoreServicesClusterCategory.GovFR:
      return 'gov.api.powerapps.us';
    case CoreServicesClusterCategory.High:
      return 'high.api.powerapps.us';
    case CoreServicesClusterCategory.DoD:
      return 'api.apps.appsplatform.us';
    case CoreServicesClusterCategory.Mooncake:
      return 'api.powerapps.cn';
    case CoreServicesClusterCategory.Ex:
      return 'api.powerapps.eaglex.ic.gov';
    case CoreServicesClusterCategory.Rx:
      return 'api.powerapps.microsoft.scloud';
    case CoreServicesClusterCategory.Exp:
    case CoreServicesClusterCategory.Dev:
    case CoreServicesClusterCategory.Test:
    case CoreServicesClusterCategory.Preprod:
    case CoreServicesClusterCategory.Prv:
    case CoreServicesClusterCategory.Prod:
    case CoreServicesClusterCategory.FirstRelease:
    default:
      return 'api.powerapps.com';
  }
};

const lastSegment = (value: string): string => {
  const trimmed = value.trim().replace(/\/+$/, '');
  return trimmed.includes('/') ? trimmed.substring(trimmed.lastIndexOf('/') + 1) : trimmed;
};

const escapeODataString = (value: string): string => value.replace(/'/g, "''");

interface ConnectionListItem {
  name?: string;
}

interface ConnectionListResponse {
  value?: ConnectionListItem[];
}

export interface ConnectionExistenceOptions {
  connectorName: string;
  connectionId: string;
  environmentId: string;
  clusterCategory: CoreServicesClusterCategory;
  accountId: string | null;
  accountHint?: string;
}

export const connectionExists = async (
  options: ConnectionExistenceOptions
): Promise<boolean | undefined> => {
  const { connectorName, connectionId, environmentId, clusterCategory, accountId, accountHint } = options;

  const normalizedConnector = lastSegment(connectorName);
  const targetName = lastSegment(connectionId);
  if (!normalizedConnector || !targetName) {
    return undefined;
  }

  const filter = `environment eq '${escapeODataString(environmentId)}'`;
  const requestUri = Uri.from({
    scheme: 'https',
    authority: getConnectionsApiHost(clusterCategory),
    path: `/providers/Microsoft.PowerApps/apis/${normalizedConnector}/connections`,
    query: `api-version=${CONNECTIONS_API_VERSION}&$filter=${encodeURIComponent(filter)}`
  });

  const resource = Uri.from({
    scheme: 'https',
    authority: getTokenScopeHostName(clusterCategory)
  });

  try {
    const { response } = await FetchAccessToken(resource, requestUri, accountId, null, accountId === null, accountHint);
    if (!response.ok) {
      logger.logInfo(
        TelemetryEventsKeys.ConnectionCreationInfo,
        `Connection existence check returned status ${response.status} for connector <pii>${normalizedConnector}</pii>.`
      );
      return undefined;
    }

    const body = (await response.json()) as ConnectionListResponse;
    const connections = body.value ?? [];
    return connections.some((c) => (c.name ?? '').localeCompare(targetName, undefined, { sensitivity: 'accent' }) === 0);
  } catch (error) {
    logger.logInfo(
      TelemetryEventsKeys.ConnectionCreationInfo,
      `Connection existence check failed for connector <pii>${normalizedConnector}</pii>: ${(error as Error).message}`
    );
    return undefined;
  }
};

const CUSTOM_CONNECTOR_INTERNAL_ID_REGEX = /-5f[0-9a-f]{16}$/i;

export const isCustomConnectorInternalId = (connectorName: string): boolean => {
  return CUSTOM_CONNECTOR_INTERNAL_ID_REGEX.test(lastSegment(connectorName));
};

export interface ConnectorAvailabilityOptions {
  connectorName: string;
  environmentId: string;
  clusterCategory: CoreServicesClusterCategory;
  accountId: string | null;
  accountHint?: string;
}

const connectorAvailable = async (
  options: ConnectorAvailabilityOptions
): Promise<boolean | undefined> => {
  const { connectorName, environmentId, clusterCategory, accountId, accountHint } = options;

  const normalizedConnector = lastSegment(connectorName);
  if (!normalizedConnector) {
    return undefined;
  }

  const filter = `environment eq '${escapeODataString(environmentId)}'`;
  const requestUri = Uri.from({
    scheme: 'https',
    authority: getConnectionsApiHost(clusterCategory),
    path: `/providers/Microsoft.PowerApps/apis/${normalizedConnector}/connections`,
    query: `api-version=${CONNECTIONS_API_VERSION}&$filter=${encodeURIComponent(filter)}`
  });

  const resource = Uri.from({
    scheme: 'https',
    authority: getTokenScopeHostName(clusterCategory)
  });

  try {
    const { response } = await FetchAccessToken(resource, requestUri, accountId, null, accountId === null, accountHint);
    if (response.ok) {
      return true;
    }
    if (response.status === 404) {
      return false;
    }
    return undefined;
  } catch (error) {
    logger.logInfo(
      TelemetryEventsKeys.ConnectionCreationInfo,
      `Connector availability check failed for connector <pii>${normalizedConnector}</pii>: ${(error as Error).message}`
    );
    return undefined;
  }
};

export interface WaitForConnectorAvailableOptions extends ConnectorAvailabilityOptions {
  timeoutMs?: number;
  cancellationToken?: CancellationToken;
}

const delay = (ms: number, cancellationToken?: CancellationToken): Promise<void> =>
  new Promise((resolve) => {
    const timer = setTimeout(() => {
      listener?.dispose();
      resolve();
    }, ms);
    const listener = cancellationToken?.onCancellationRequested(() => {
      clearTimeout(timer);
      resolve();
    });
  });

export const waitForConnectorAvailable = async (
  options: WaitForConnectorAvailableOptions
): Promise<boolean> => {
  const timeoutMs = options.timeoutMs ?? 30_000;
  const normalizedConnector = lastSegment(options.connectorName);
  const deadline = Date.now() + timeoutMs;
  let nextDelayMs = 1_000;
  const maxDelayMs = 3_000;

  while (!options.cancellationToken?.isCancellationRequested) {
    const available = await connectorAvailable(options);
    if (available === true) {
      return true;
    }

    if (Date.now() >= deadline) {
      logger.logInfo(
        TelemetryEventsKeys.ConnectionCreationInfo,
        `Connector <pii>${normalizedConnector}</pii> was not available in the API Hub within ${timeoutMs} ms; proceeding anyway.`
      );
      return false;
    }

    await delay(Math.min(nextDelayMs, Math.max(0, deadline - Date.now())), options.cancellationToken);
    nextDelayMs = Math.min(Math.floor(nextDelayMs * 1.5), maxDelayMs);
  }

  return false;
};
