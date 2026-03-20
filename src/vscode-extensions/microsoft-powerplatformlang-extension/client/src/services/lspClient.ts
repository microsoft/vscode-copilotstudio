import * as vscode from 'vscode';
import * as path from 'path';
import { spawnSync } from 'child_process';
import { ServerOptions, TransportKind, LanguageClient, LanguageClientOptions, Trace, State } from "vscode-languageclient/node";
import { TELEMETRY_CONNECTION_STRING, TelemetryEventsKeys } from '../constants';
import { AccountInfo, AgentSyncInfo, EnvironmentInfo, RemoteApiRequest } from '../types';
import { getAccessTokenByAccountId, getCopilotStudioAccessTokenByAccountId } from '../clients/account';
import { getSolutionVersionsAsync } from '../clients/dataverseClient';
import { getClusterCategory } from '../utils/genericUtils';
import { onWorkspaceChange } from '../sync/workspaceScm';
import { isTelemetryEnabled } from './telemetry';
import logger from './logger';

let currentContext: vscode.ExtensionContext | null = null;
let currentOutputChannel: vscode.OutputChannel | null = null;
let currentSessionId: string | null = null;

class LspClientService {
  private static instance: LspClientService | null = null;
  private _client: LanguageClient | null = null;

  private constructor() { }

  public static getInstance(): LspClientService {
    if (!LspClientService.instance) {
      LspClientService.instance = new LspClientService();
    }
    return LspClientService.instance!;
  }

  public get client(): LanguageClient | null {
    return this._client;
  }

  public async dispose(): Promise<void> {
    if (this._client) {
      try {
        await this._client.stop();
      } catch (error) {
        this._client.dispose();
      }

      // Clear internal client reference
      this._client = null;

      // Reset singleton so next getInstance() creates a fresh instance
      LspClientService.instance = null;
    }
  }

  public async initializeAndStart(context: vscode.ExtensionContext, outputChannel: vscode.OutputChannel, sessionId: string): Promise<void> {
    currentContext = context;
    currentOutputChannel = outputChannel;
    currentSessionId = sessionId;

    const cwd = path.join(context.extensionPath, 'lspOut');
    const lspHostPath = path.join(cwd, "LanguageServerHost");
  
    // On Linux, ensure the LanguageServerHost is executable
    if (process.platform === 'linux' || process.platform === 'darwin') {
      const response = spawnSync('chmod', ['+x', lspHostPath]);
      if (response.status !== 0) {
        const errorMessage = new TextDecoder().decode(response.stderr);
        logger.logError(TelemetryEventsKeys.UnixPlatformError, errorMessage);
      }
    }

    const serverArgs = [`--sessionid=${sessionId}`, `--enabletelemetry=${isTelemetryEnabled()}`];
    const serverOptions: ServerOptions = {
      args: process.env.VSCODE_DEBUG === 'true' ? ["--debugger=true", ...serverArgs] : serverArgs,
      command: process.env.LSP_PATH || lspHostPath,
      transport: TransportKind.pipe,
      options: {
        cwd,
        encoding: "utf8",
        env: {
          ...process.env,
          TELEMETRY_CONNECTION_STRING, // Pass to C#
        },
      }
    };

    const clientOptions: LanguageClientOptions = {
      outputChannel,
      stdioEncoding: "utf8",
      diagnosticCollectionName: "PowerPlatform" + sessionId,
      documentSelector: [
        { scheme: 'file', language: 'PowerFx' },
        { scheme: 'file', language: 'CopilotStudio' },
        { scheme: 'file', language: 'Yaml' }
      ],
      connectionOptions: {
        cancellationStrategy: {
          receiver: {
            kind: "id",
            createCancellationTokenSource: (id) => {
              return new vscode.CancellationTokenSource();
            }
          },
          sender: {
            sendCancellation(conn, id) {
              logger.logInfo(TelemetryEventsKeys.LanguageServerInfo, undefined, {
                message: `[LSP] sendCancellation: ${id}`
              });
              return Promise.resolve();
            },
            enableCancellation(request) {},
            cleanup(id) {} 
          }
        }
      },
      synchronize: {
        fileEvents: [
          vscode.workspace.createFileSystemWatcher('**/*.mcs.yml'),
          vscode.workspace.createFileSystemWatcher('**/*.mcs.yaml'),
          vscode.workspace.createFileSystemWatcher('**/botdefinition.json'),
          vscode.workspace.createFileSystemWatcher('**/*.fx1'),
          vscode.workspace.createFileSystemWatcher('**/icon.png'),
          vscode.workspace.createFileSystemWatcher('**/agents/**', false, true, false),          
          vscode.workspace.createFileSystemWatcher('**/workflow.json')
        ]
      },
      middleware: {
        handleDiagnostics: (uri, diagnostics, next) => {
          try {
            next(uri, diagnostics);
          } catch (error) {
            logger.logError(TelemetryEventsKeys.LanguageServerError, undefined, {
              message: `[LSP] Diagnostics error: ${(error as Error).message}`,
            });
            throw error;
          }
        },
        sendNotification: async (type, next, params) => {
          // Using :: instead of / so it is not flagged as PII in telemetry.
          const notificationType = JSON.stringify(typeof type === 'string' ? type : type.method).replace(/[./\\]/g, "::");

          logger.logInfo(TelemetryEventsKeys.LanguageServerInfo, undefined, {
            message: `[LSP] Sending notification: ${notificationType}`,
          });

          try {
            await next(type, params);
            logger.logInfo(TelemetryEventsKeys.LanguageServerInfo, undefined, {
              message: `[LSP] Notification ${notificationType} sent successfully`,
            });
          } catch (error) {
            logger.logError(TelemetryEventsKeys.LanguageServerError, undefined, {
              message: `[LSP] Notification ${notificationType} failed: ${(error as Error).message}`,
            });
            throw error;
          }
        },
        sendRequest: async (type, param, token, next) => {
          // Using :: instead of / so it is not flagged as PII in telemetry.
          const requestType = JSON.stringify(typeof type === 'string' ? type : type.method).replace(/[./\\]/g, "::");
          logger.logInfo(TelemetryEventsKeys.LanguageServerInfo, undefined, {
            message: `[LSP] Sending request: ${requestType}`,
          });

          try {
            const result = await next(type, param, token);
            if (result && typeof result === 'object' && 'code' in result && (result as any).code !== 200) {
              throw new Error((result as any).message ?? `Request ${requestType} failed with code ${result.code}`);
            } else {
              logger.logInfo(TelemetryEventsKeys.LanguageServerInfo, undefined, {
                message: `[LSP] Request ${requestType} completed successfully`,
              });
              return result;
            }
          } catch (error) {
            logger.logError(TelemetryEventsKeys.LanguageServerError, undefined, {
              message: `[LSP] Request ${requestType} failed: ${(error as Error).message}`
            });
            throw error;
          }
        },
        workspace: {
          async didChangeWatchedFile(event, next) {
            await next(event);
            // Trigger post LSP workspace change event to ensure the workspace is updated
            onWorkspaceChange(event.uri);
          },
        }
      }
    };

    this._client = new LanguageClient("Copilot Studio Language Server" + sessionId, serverOptions, clientOptions);
    this._client.setTrace(Trace.Verbose);
    this._client.onDidChangeState((event) => {
      logger.logInfo(TelemetryEventsKeys.LanguageServerInfo, undefined, {
        message: `[LSP] State changed from ${State[event.oldState]} to ${State[event.newState]}`,
      });
    });

    try {
      await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: 'Starting Copilot Studio Language Server. Please wait...',
          cancellable: false
        },
        async () => {
          await this._client!.start();
        }
      );
      logger.logInfo(TelemetryEventsKeys.LanguageServerInfo, "Copilot Studio Language Server has started");
      context.subscriptions.push(this._client);
    } catch (error) {
      logger.logError(TelemetryEventsKeys.LanguageServerError, `Copilot Studio Language Server failed to start: ${(error as Error).message}`);
      throw error;
    }
  }
}

export const lspClient = new Proxy({} as LanguageClient, {
  get(target, property, receiver) {
    const clientInstance = LspClientService.getInstance().client;

    if (!clientInstance) {
      throw new Error("LSP client is not initialized");
    }
    
    const value = clientInstance[property as keyof LanguageClient];
    
    // If it's a function, bind it to the client instance
    if (typeof value === 'function') {
      return value.bind(clientInstance);
    }
    
    return value;
  }
});

export const restartLspClient = async (): Promise<void> => {
  if (!currentContext || !currentSessionId || !currentOutputChannel) {
    return;
  }

  await LspClientService.getInstance().dispose();
  await LspClientService.getInstance().initializeAndStart(currentContext, currentOutputChannel, currentSessionId);
};

/**
 * Builds the LSP request payload for a given set of parameters.
 * Either `syncInfo` or `environmentInfo` must be provided.
 * @param syncInfo - Information about the agent.
 * @param environmentInfo - Information about the environment.
 * @param account - Information about the user account. It will be used to retrieve access tokens and cluster category if `syncInfo` is not provided.
 * @returns A promise that resolves to the LSP request payload.
 */
export const buildLspRequestPayload = async (syncInfo?: AgentSyncInfo, environmentInfo?: EnvironmentInfo, account?: Partial<AccountInfo>): Promise<RemoteApiRequest> => {
  let payload: RemoteApiRequest;

  if (syncInfo) {
    const { accountInfo, agentManagementEndpoint, dataverseEndpoint, environmentId, solutionVersions } = syncInfo;
    const copilotStudioAccessToken = await getCopilotStudioAccessTokenByAccountId(getClusterCategory(accountInfo), accountInfo.accountId);
    const dataverseAccessToken = await getAccessTokenByAccountId(vscode.Uri.parse(dataverseEndpoint), accountInfo.accountId);

    payload = {
      accountInfo,
      copilotStudioAccessToken: copilotStudioAccessToken.accessToken,
      dataverseAccessToken: dataverseAccessToken.accessToken,
      environmentInfo: {
        agentManagementUrl: agentManagementEndpoint,
        dataverseUrl: dataverseEndpoint,
        displayName: "",
        environmentId
      },
      solutionVersions,
    };
  } else if (environmentInfo) {
    const clusterCategory = getClusterCategory(account);
    const parsedDataverseUrl = vscode.Uri.parse(environmentInfo.dataverseUrl);
    const copilotStudioAccessToken = await getCopilotStudioAccessTokenByAccountId(clusterCategory, account?.accountId);
    const dataverseAccessToken = await getAccessTokenByAccountId(parsedDataverseUrl, account?.accountId);
    const solutionVersions = await getSolutionVersionsAsync(parsedDataverseUrl, null);

    payload = {
      accountInfo: {
        accountEmail: dataverseAccessToken.accountEmail,
        accountId: dataverseAccessToken.accountId,
        clusterCategory,
        tenantId: dataverseAccessToken.tenantId
      },
      copilotStudioAccessToken: copilotStudioAccessToken.accessToken,
      dataverseAccessToken: dataverseAccessToken.accessToken,
      environmentInfo,
      solutionVersions,
    };
  } else {
    throw new Error("Either 'syncInfo' or 'environmentInfo' must be provided to build LSP request payload.");
  }

  return payload;
};

export default LspClientService.getInstance();
