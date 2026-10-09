import * as vscode from 'vscode';
import * as path from 'path';
import { spawnSync } from 'child_process';
import * as fs from 'fs';
import { ServerOptions, TransportKind, LanguageClient, LanguageClientOptions, State, LogMessageNotification, Trace } from "vscode-languageclient/node";
import { TELEMETRY_CONNECTION_STRING, TelemetryEventsKeys } from '../constants';
import { AccountInfo, AgentSyncInfo, EnvironmentInfo, RemoteApiRequest } from '../types';
import { getAccessTokenByAccountId, getCopilotStudioAccessTokenByAccountId, isIdentityUnbound, resolveAccountIdentity, resolveTenantId } from '../clients/account';
import { getSolutionVersionsAsync } from '../clients/dataverseClient';
import { getClusterCategory } from '../utils/genericUtils';
import { onWorkspaceChange } from '../sync/workspaceScm';
import { isTelemetryEnabled } from './telemetry';
import logger, { sanitizeErrorDetails } from './logger';

let currentContext: vscode.ExtensionContext | null = null;
let currentOutputChannel: vscode.LogOutputChannel | null = null;
let currentSessionId: string | null = null;

/**
 * Mirrors the .NET IsBuiltInLspMethod logic.
 * Built-in LSP methods are standard protocol methods that don't need custom logging.
 * Custom methods (powerplatformls/*, workspace/listWorkspaces, etc.) are logged.
 */
function isBuiltInLspMethod(method: string): boolean {
  return method.startsWith('textDocument/')
    || method.startsWith('$/')
    || method.startsWith('initialize')
    || method.startsWith('shutdown')
    || method.startsWith('exit')
    || method.startsWith('workspace/didChange')
    || method.startsWith('workspace/didRename');
}

const MINIMUM_SUPPORTED_MACOS_VERSION = '27.0.0';

/**
 * Detects the macOS "Bad CPU type in executable" spawn failure (errno 86 / EBADARCH)
 * that occurs when the bundled language server binary targets a different CPU
 * architecture than the host and no translation layer (Rosetta 2) is available.
 */
function isArchitectureSpawnError(error: unknown): boolean {
  if (!error) {
    return false;
  }
  const { errno, code, message } = error as { errno?: number; code?: string; message?: string };
  if (errno === -86 || errno === 86) {
    return true;
  }
  const text = `${code ?? ''} ${message ?? ''}`;
  return /error -86\b/i.test(text) || /EBADARCH/i.test(text) || /Bad CPU type/i.test(text);
}

/**
 * Reads the macOS product version (e.g. "26.6.2") via `sw_vers -productVersion`.
 * Returns undefined when not on macOS or when the version cannot be determined.
 */
function getMacOsProductVersion(): string | undefined {
  if (process.platform !== 'darwin') {
    return undefined;
  }
  try {
    const result = spawnSync('sw_vers', ['-productVersion'], { encoding: 'utf8' });
    if (result.status === 0 && typeof result.stdout === 'string') {
      const version = result.stdout.trim();
      return version.length > 0 ? version : undefined;
    }
  } catch {
    // Ignore – treated as an unknown version.
  }
  return undefined;
}

/**
 * Compares dotted numeric versions. Returns true when `version` is strictly lower
 * than `target` (e.g. "26.6.2" is lower than "27.0.0").
 */
function isVersionLowerThan(version: string, target: string): boolean {
  const toParts = (value: string) => value.split('.').map((part) => parseInt(part, 10) || 0);
  const a = toParts(version);
  const b = toParts(target);
  const length = Math.max(a.length, b.length);
  for (let i = 0; i < length; i++) {
    const left = a[i] ?? 0;
    const right = b[i] ?? 0;
    if (left !== right) {
      return left < right;
    }
  }
  return false;
}

/**
 * When the language server fails to start with a CPU-architecture mismatch on
 * macOS, warns users whose macOS version is lower than
 * {@link MINIMUM_SUPPORTED_MACOS_VERSION}.
 */
function warnIfIncompatibleMacOsVersion(error: unknown): void {
  if (process.platform !== 'darwin' || !isArchitectureSpawnError(error)) {
    return;
  }
  const version = getMacOsProductVersion();
  if (version && isVersionLowerThan(version, MINIMUM_SUPPORTED_MACOS_VERSION)) {
    void vscode.window.showWarningMessage(
      `Copilot Studio Language Server failed to start. Your macOS version (${version}) is lower than ${MINIMUM_SUPPORTED_MACOS_VERSION}. ` +
      `Update the Copilot Studio extension to the latest version; if the problem persists, ensure Rosetta 2 is installed ` +
      `(run "softwareupdate --install-rosetta --agree-to-license" in Terminal) and reload the window.`
    );
  }
}

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

  public async initializeAndStart(context: vscode.ExtensionContext, outputChannel: vscode.LogOutputChannel, sessionId: string): Promise<void> {
    currentContext = context;
    currentOutputChannel = outputChannel;
    currentSessionId = sessionId;

    const lspOutDir = path.join(context.extensionPath, 'lspOut');
    // macOS VSIXes ship both architecture binaries in arch subfolders
    // (osx-x64 / osx-arm64) so the extension can select the one matching the
    // host architecture (Apple Silicon vs Intel/Rosetta). Windows and Linux
    // ship a single binary at the lspOut root, so fall back to that path.
    const macArchDir = process.arch === 'arm64' ? 'osx-arm64' : 'osx-x64';
    const macArchHostPath = path.join(lspOutDir, macArchDir, 'LanguageServerHost');
    const flatHostPath = path.join(lspOutDir, 'LanguageServerHost');
    const lspHostPath =
      process.platform === 'darwin' && fs.existsSync(macArchHostPath)
        ? macArchHostPath
        : flatHostPath;
    const cwd = path.dirname(lspHostPath);
  
    // On Linux, ensure the LanguageServerHost is executable
    if (process.platform === 'linux' || process.platform === 'darwin') {
      const response = spawnSync('chmod', ['+x', lspHostPath]);
      if (response.status !== 0) {
        const errorMessage = new TextDecoder().decode(response.stderr);
        logger.logError(
          TelemetryEventsKeys.UnixPlatformError,
          sanitizeErrorDetails(errorMessage),
        );
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
          vscode.workspace.createFileSystemWatcher('**/workflow.json'),
          vscode.workspace.createFileSystemWatcher('**/metadata.yml'),
          vscode.workspace.createFileSystemWatcher('**/prompt.json')
        ]
      },
      middleware: {
        handleDiagnostics: (uri, diagnostics, next) => {
          try {
            next(uri, diagnostics);
          } catch (error) {
            logger.logError(TelemetryEventsKeys.LanguageServerError, undefined, {
              message: 'Diagnostics error',
              error,
            });
            throw error;
          }
        },
        sendNotification: async (type, next, params) => {
          const method = typeof type === 'string' ? type : type.method;
          const isCustom = !isBuiltInLspMethod(method);
          // Using :: instead of / so it is not flagged as PII in telemetry.
          const lspMethod = method.replace(/[./\\]/g, "::");

          if (isCustom) {
            logger.logTrace('LSP', `Sending notification: ${method}`);
          }

          try {
            await next(type, params);
            if (isCustom) {
              logger.logInfo(TelemetryEventsKeys.LanguageServerInfo, undefined, {
                message: `Notification completed: ${lspMethod}`,
                lspMethod,
              });
            }
          } catch (error) {
            logger.logError(TelemetryEventsKeys.LanguageServerError, undefined, {
              message: `Notification failed: ${lspMethod}`,
              lspMethod,
              error,
            });
            throw error;
          }
        },
        sendRequest: async (type, param, token, next) => {
          const method = typeof type === 'string' ? type : type.method;
          const isCustom = !isBuiltInLspMethod(method);
          // Using :: instead of / so it is not flagged as PII in telemetry.
          const lspMethod = method.replace(/[./\\]/g, "::");

          if (isCustom) {
            logger.logTrace('LSP', `Sending request: ${method}`);
          }

          const startTime = Date.now();
          try {
            const result = await next(type, param, token);
            const durationMs = Date.now() - startTime;
            if (result && typeof result === 'object' && 'code' in result && (result as any).code !== 200) {
              throw new Error((result as any).message ?? `Request failed with code ${(result as any).code}`);
            } else {
              if (isCustom) {
                logger.logInfo(TelemetryEventsKeys.LanguageServerInfo, undefined, {
                  message: `Request completed: ${lspMethod}`,
                  lspMethod,
                  durationMs,
                });
              }
              return result;
            }
          } catch (error) {
            const durationMs = Date.now() - startTime;
            logger.logError(TelemetryEventsKeys.LanguageServerError, undefined, {
              message: `Request failed: ${lspMethod}`,
              lspMethod,
              durationMs,
              error,
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
        message: `State changed from ${State[event.oldState]} to ${State[event.newState]}`,
      });
    });

    // Route window/logMessage into the LogOutputChannel for native
    // [error]/[warning]/[info] color + timestamp. The .NET side gates sending on
    // the client's "initialized" notification (see LspWindowLogMessageLoggerProvider)
    // so this handler is wired first and takes precedence over vscode-languageclient's
    // built-in default, which would otherwise prepend a duplicate
    // "[Error|Warning|Info - h:mm:ss AM/PM]" prefix.
    //
    // Log level filtering is handled natively by VS Code's LogOutputChannel.
    // The user controls visibility via the output panel dropdown (defaults to Info).
    const logChannel = outputChannel as Partial<vscode.LogOutputChannel>;
    const writeLog = (level: 'error' | 'warn' | 'info' | 'trace' | 'debug', message: string) => {
      const fn = logChannel[level];
      if (typeof fn === 'function') {
        fn.call(logChannel, message);
      } else {
        outputChannel.appendLine(message);
      }
    };

    this._client.onNotification(LogMessageNotification.type, (params: { type: number; message: string }) => {
      const message = params?.message ?? '';
      switch (params?.type) {
        case 1: writeLog('error', message); break;
        case 2: writeLog('warn', message); break;
        case 3: writeLog('info', message); break;
        case 4: writeLog('trace', message); break;
        case 5: writeLog('debug', message); break;
        default: outputChannel.appendLine(message); break;
      }
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
      logger.logError(TelemetryEventsKeys.LanguageServerError, 'Copilot Studio Language Server failed to start', { error });
      warnIfIncompatibleMacOsVersion(error);
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
export const buildLspRequestPayload = async (syncInfo?: AgentSyncInfo, environmentInfo?: EnvironmentInfo, account?: Partial<AccountInfo>, interactive: boolean = false): Promise<RemoteApiRequest> => {
  let payload: RemoteApiRequest;

  if (syncInfo) {
    const { accountInfo, agentManagementEndpoint, dataverseEndpoint, environmentId, solutionVersions } = syncInfo;
    const resolvedIdentity = resolveAccountIdentity(accountInfo);
    if (isIdentityUnbound(resolvedIdentity.accountId, resolvedIdentity.accountEmail)) {
      throw new Error('Could not determine which account this agent belongs to. Select the account that owns it and try again.');
    }

    const copilotStudioAccessToken = await getCopilotStudioAccessTokenByAccountId(getClusterCategory(accountInfo), resolvedIdentity.accountId, resolvedIdentity.accountEmail, interactive);
    const dataverseAccessToken = await getAccessTokenByAccountId(vscode.Uri.parse(dataverseEndpoint), resolvedIdentity.accountId, resolvedIdentity.accountEmail, interactive);

    payload = {
      accountInfo: { ...accountInfo, ...resolvedIdentity, tenantId: resolveTenantId(resolvedIdentity.tenantId, dataverseAccessToken.tenantId) },
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
    const copilotStudioAccessToken = await getCopilotStudioAccessTokenByAccountId(clusterCategory, account?.accountId, account?.accountEmail, interactive);
    const dataverseAccessToken = await getAccessTokenByAccountId(parsedDataverseUrl, account?.accountId, account?.accountEmail, interactive);
    const solutionVersions = await getSolutionVersionsAsync(parsedDataverseUrl, null, account?.accountId, account?.accountEmail, interactive);

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
