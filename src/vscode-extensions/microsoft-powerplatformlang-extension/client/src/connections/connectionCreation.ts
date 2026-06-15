import * as http from 'http';
import * as crypto from 'crypto';
import * as process from 'process';
import { AddressInfo } from 'net';
import * as vscode from 'vscode';
import { CoreServicesClusterCategory } from '../constants';

const PROTOCOL_VERSION = '1';
const TIMEOUT_MS = 5 * 60 * 1000;

const openInBrowser = async (targetUrl: string): Promise<boolean> => {
  if (process.platform !== 'linux') {
    try {
      const { default: open } = await import('open');
      await open(targetUrl);
      return true;
    } catch {
    }
  }

  try {
    return await vscode.env.openExternal(vscode.Uri.parse(targetUrl));
  } catch {
    return false;
  }
};

export type ConnectionCreationResult =
  | { status: 'created'; connectionId?: string; connectionName?: string; displayName?: string }
  | { status: 'cancelled' }
  | { status: 'error'; errorMessage?: string };

export interface ConnectionCreationOptions {
  connectorName: string;
  environmentId: string;
  clusterCategory: CoreServicesClusterCategory;
  cancellationToken?: vscode.CancellationToken;
}

const getPlayerBaseUrl = (clusterCategory: CoreServicesClusterCategory): string => {
  switch (clusterCategory) {
    case CoreServicesClusterCategory.Gov:
    case CoreServicesClusterCategory.GovFR:
      return 'https://apps.gov.powerapps.us';
    case CoreServicesClusterCategory.High:
      return 'https://apps.high.powerapps.us';
    case CoreServicesClusterCategory.DoD:
      return 'https://play.apps.appsplatform.us';
    case CoreServicesClusterCategory.Mooncake:
      return 'https://apps.powerapps.cn';
    case CoreServicesClusterCategory.Ex:
      return 'https://apps.powerapps.eaglex.ic.gov';
    case CoreServicesClusterCategory.Rx:
      return 'https://apps.powerapps.microsoft.scloud';
    case CoreServicesClusterCategory.Exp:
    case CoreServicesClusterCategory.Dev:
    case CoreServicesClusterCategory.Test:
    case CoreServicesClusterCategory.Preprod:
    case CoreServicesClusterCategory.Prv:
      return 'https://apps.preview.powerapps.com';
    case CoreServicesClusterCategory.Prod:
    case CoreServicesClusterCategory.FirstRelease:
    default:
      return 'https://apps.powerapps.com';
  }
};

const normalizeConnector = (connector: string): string => {
  const trimmed = connector.trim().replace(/\/+$/, '');
  return trimmed.includes('/') ? trimmed.substring(trimmed.lastIndexOf('/') + 1) : trimmed;
};

const buildPlayerUrl = (
  clusterCategory: CoreServicesClusterCategory,
  environmentId: string,
  connector: string,
  callbackUrl: string,
  nonce: string
): string => {
  const url = new URL(`/appframework/e/${environmentId}/connections/new`, getPlayerBaseUrl(clusterCategory));
  url.searchParams.set('connector', connector);
  url.searchParams.set('callbackUrl', callbackUrl);
  url.searchParams.set('nonce', nonce);
  url.searchParams.set('v', PROTOCOL_VERSION);
  return url.toString();
};

const escapeHtml = (s: string): string => s.replace(/[&<>"']/g, c => `&#${c.charCodeAt(0)};`);

const renderResultPage = (status: string | null, message?: string): string => {
  const safeMessage = message ? escapeHtml(message) : '';
  const heading =
    status === 'created'
      ? 'Connection created. You can close this tab and return to VS Code.'
      : status === 'error'
        ? 'Connection creation failed.'
        : 'Connection creation was cancelled.';
  return `<!DOCTYPE html><html><head><meta charset="utf-8"><title>Copilot Studio</title></head>`
    + `<body style="font-family: sans-serif; padding: 2rem; font-size: 0.9rem;"><p style="font-size: 1rem; font-weight: 600; margin: 0 0 0.5rem;">${escapeHtml(heading)}</p>`
    + (safeMessage ? `<p style="margin: 0;">${safeMessage}</p>` : '')
    + `</body></html>`;
};

export const awaitConnectionCreation = (options: ConnectionCreationOptions): Promise<ConnectionCreationResult> => {
  const { connectorName, environmentId, clusterCategory, cancellationToken } = options;
  const nonce = crypto.randomUUID();
  const connector = normalizeConnector(connectorName);

  return new Promise<ConnectionCreationResult>((resolve) => {
    let settled = false;
    let timeoutId: NodeJS.Timeout | undefined;
    let cancellationListener: vscode.Disposable | undefined;

    const server = http.createServer((req, res) => {
      const requestUrl = new URL(req.url ?? '/', 'http://127.0.0.1');

      if (requestUrl.pathname !== '/callback' || req.method !== 'GET') {
        res.writeHead(404);
        res.end('Not found');
        return;
      }

      if (requestUrl.searchParams.get('nonce') !== nonce) {
        res.writeHead(403, { 'Content-Type': 'text/html; charset=utf-8' });
        res.end('<h2>Verification failed. Please run the command again.</h2>');
        return;
      }

      const status = requestUrl.searchParams.get('status');
      const message = requestUrl.searchParams.get('message') ?? undefined;

      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      res.end(renderResultPage(status, message));

      if (status === 'created') {
        finish({
          status: 'created',
          connectionId: requestUrl.searchParams.get('connectionId') ?? undefined,
          connectionName: requestUrl.searchParams.get('connectionName') ?? undefined,
          displayName: requestUrl.searchParams.get('displayName') ?? undefined,
        });
      } else if (status === 'error') {
        finish({ status: 'error', errorMessage: message });
      } else {
        finish({ status: 'cancelled' });
      }
    });

    const cleanup = () => {
      if (timeoutId) {
        clearTimeout(timeoutId);
        timeoutId = undefined;
      }
      cancellationListener?.dispose();
      cancellationListener = undefined;
      if (server.listening) {
        server.close();
      }
    };

    const finish = (result: ConnectionCreationResult) => {
      if (settled) {
        return;
      }
      settled = true;
      cleanup();
      resolve(result);
    };

    server.on('error', (err) => {
      if (settled) {
        return;
      }
      settled = true;
      cleanup();
      resolve({ status: 'error', errorMessage: err.message });
    });

    server.listen(0, '127.0.0.1', async () => {
      const { port } = server.address() as AddressInfo;
      const callbackUrl = `http://127.0.0.1:${port}/callback`;
      const playerUrl = buildPlayerUrl(clusterCategory, environmentId, connector, callbackUrl, nonce);

      try {
        const opened = await openInBrowser(playerUrl);
        if (!opened) {
          await vscode.window.showWarningMessage(
            `Could not open the browser automatically. Open this URL to create the connection:\n${playerUrl}`
          );
        }
      } catch (error) {
        finish({ status: 'error', errorMessage: (error as Error).message });
      }
    });

    timeoutId = setTimeout(() => finish({ status: 'cancelled' }), TIMEOUT_MS);

    if (cancellationToken) {
      cancellationListener = cancellationToken.onCancellationRequested(() => finish({ status: 'cancelled' }));
    }
  });
};
