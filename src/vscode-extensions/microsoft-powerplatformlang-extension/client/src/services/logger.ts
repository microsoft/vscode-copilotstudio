import * as vscode from 'vscode';
import { TelemetryEventMeasurements, TelemetryEventProperties, TelemetryReporter } from "@vscode/extension-telemetry";
import { LogLevel, TELEMETRY_CONNECTION_STRING, TelemetryEventsKeys, type TelemetryEventType } from '../constants';
import { isTelemetryEnabled } from './telemetry';

type TelemetryEventProps = {
  properties: TelemetryEventProperties,
  measurements: TelemetryEventMeasurements,
};

/**
 * Type definitions for telemetry event parameters:
 * - properties: Record<string, string> (string key-value pairs for event metadata)
 * - measurements: Record<string, number> (string key-number pairs for numeric metrics)
 */
type TelemetryEventData = Record<string, string | number | undefined>;

export enum PiiRedactionType {
  AccountIdentifier = 'ACCOUNT IDENTIFIER',
  AgentName = 'AGENT NAME',
  EmailAddress = 'EMAIL ADDRESS',
  FileUri = 'FILE URI',
  McsYamlFileName = '.MCS.YML FILE NAME',
  AiBuilderPromptDetails = 'AI BUILDER PROMPT DETAILS',
  WorkflowErrorDetails = 'WORKFLOW ERROR DETAILS',
  WorkflowNames = 'WORKFLOW NAMES',
}

interface PiiMatch {
  start: number;
  end: number;
  type: PiiRedactionType;
  priority: number;
}

const piiPattern = /<pii(?: type="([^"]+)")?(?: encoded="(true)")?>(.*?)<\/pii>/gs;

const encodePiiValue = (value: string): string =>
  value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

const decodePiiValue = (value: string): string =>
  value.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');

export function formatPii(value: string, type: PiiRedactionType): string {
  return `<pii type="${type}" encoded="true">${encodePiiValue(value)}</pii>`;
}

function collectMatches(
  value: string,
  pattern: RegExp,
  type: PiiRedactionType,
  priority: number,
  captureGroup = 0,
): PiiMatch[] {
  const matches: PiiMatch[] = [];
  for (const match of value.matchAll(pattern)) {
    const capturedValue = match[captureGroup];
    if (!capturedValue || match.index === undefined) {
      continue;
    }
    const captureOffset = match[0].indexOf(capturedValue);
    matches.push({
      start: match.index + captureOffset,
      end: match.index + captureOffset + capturedValue.length,
      type,
      priority,
    });
  }
  return matches;
}

export function sanitizeErrorDetails(errorMessage: string, agentNames: readonly string[] = []): string {
  const matches: PiiMatch[] = [
    ...collectMatches(
      errorMessage,
      /[A-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Z0-9-]+(?:\.[A-Z0-9-]+)+/gi,
      PiiRedactionType.EmailAddress,
      3,
    ),
    ...collectMatches(
      errorMessage,
      /[A-Za-z]:[\\/](?:[^\\/\r\n:*?"<>|]+[\\/])*[^\\/\r\n:*?"<>|]+\.mcs\.ya?ml/gi,
      PiiRedactionType.McsYamlFileName,
      4,
    ),
    ...collectMatches(
      errorMessage,
      /\\\\[^\\/\r\n:*?"<>|]+[\\/](?:[^\\/\r\n:*?"<>|]+[\\/])*[^\\/\r\n:*?"<>|]+\.mcs\.ya?ml/gi,
      PiiRedactionType.McsYamlFileName,
      4,
    ),
    ...collectMatches(
      errorMessage,
      /(?:\/[^\/\r\n"'<>|?*]+)+\.mcs\.ya?ml/gi,
      PiiRedactionType.McsYamlFileName,
      4,
    ),
    ...collectMatches(
      errorMessage,
      /(?:[^\s\\/:"'<>|?*]+[\\/])+[^\s\\/:"'<>|?*]+\.mcs\.ya?ml|[^\s\\/:"'<>|?*]+\.mcs\.ya?ml/gi,
      PiiRedactionType.McsYamlFileName,
      3,
    ),
    ...collectMatches(
      errorMessage,
      /"([^"\r\n]*?\.mcs\.ya?ml)"/gi,
      PiiRedactionType.McsYamlFileName,
      4,
      1,
    ),
    ...collectMatches(
      errorMessage,
      /'([^'\r\n]*?\.mcs\.ya?ml)'/gi,
      PiiRedactionType.McsYamlFileName,
      4,
      1,
    ),
  ];

  for (const agentName of agentNames.filter(name => name.length > 0)) {
    const escapedAgentName = agentName.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const agentNamePattern = new RegExp(
      `(?<![\\p{L}\\p{N}])${escapedAgentName}(?![\\p{L}\\p{N}])`,
      'giu',
    );
    matches.push(...collectMatches(
      errorMessage,
      agentNamePattern,
      PiiRedactionType.AgentName,
      2,
    ));
  }

  const quotedAgentPattern = /\bAgent\s+(?:"([^"\r\n]+)"|'([^'\r\n]+)')/gi;
  for (const match of errorMessage.matchAll(quotedAgentPattern)) {
    const agentName = match[1] ?? match[2] ?? match[3];
    if (!agentName || match.index === undefined) {
      continue;
    }
    const nameOffset = match[0].indexOf(agentName);
    matches.push({
      start: match.index + nameOffset,
      end: match.index + nameOffset + agentName.length,
      type: PiiRedactionType.AgentName,
      priority: 1,
    });
  }

  const unquotedAgentPattern = /\bAgent\s+([A-Za-z0-9][A-Za-z0-9 _.-]*?)(?=\s+(?:at|was|is|has|had|failed|could|cannot|can't|did|does|will|would|returned|with|from|for|unavailable|rejected)\b)/gi;
  matches.push(...collectMatches(
    errorMessage,
    unquotedAgentPattern,
    PiiRedactionType.AgentName,
    1,
    1,
  ));

  matches.sort((left, right) =>
    left.start - right.start
    || right.priority - left.priority
    || right.end - right.start - (left.end - left.start));

  let sanitized = '';
  let cursor = 0;
  for (const match of matches) {
    if (match.start < cursor) {
      continue;
    }
    sanitized += errorMessage.slice(cursor, match.start);
    sanitized += formatPii(errorMessage.slice(match.start, match.end), match.type);
    cursor = match.end;
  }

  return sanitized + errorMessage.slice(cursor);
}

const stripPiiTags = (value: string): string =>
  value.replace(
    piiPattern,
    (_match, _type: string | undefined, encoded: string | undefined, piiValue: string) =>
      encoded ? decodePiiValue(piiValue) : piiValue,
  );

const redactPii = (value: string): string =>
  value.replace(piiPattern, (_match, type: string | undefined) =>
    type ? `[REDACTED ${type}]` : '[REDACTED]');

export function prepareLogData(message: string | undefined, properties: TelemetryEventProperties): {
  displayMessage: string | undefined;
  telemetryProperties: Record<string, string>;
} {
  const displayMessage = message === undefined ? undefined : stripPiiTags(message);
  const rawMessage = properties.message as string || message;
  const telemetryProperties: Record<string, string> = {};

  for (const [key, value] of Object.entries(properties)) {
    if (typeof value === 'string') {
      telemetryProperties[key] = redactPii(value);
    } else if (value !== undefined) {
      telemetryProperties[key] = String(value);
    }
  }

  if (rawMessage) {
    telemetryProperties.message = redactPii(rawMessage);
  }

  return { displayMessage, telemetryProperties };
}

const NOOP_REPORTER = {
  sendTelemetryEvent: () => { },
  sendTelemetryErrorEvent: () => { },
  dispose: () => Promise.resolve(),
} as unknown as TelemetryReporter;

/**
 * Feature-based categories for output channel log grouping.
 * Only meaningful features that help users identify the source of a log.
 */
export enum LogCategory {
  LSP = "LSP",
  Clone = "Clone",
  Sync = "Sync",
  Auth = "Auth",
  Knowledge = "Knowledge",
  Reattach = "Reattach",
  AgentTree = "AgentTree",
  Workflow = "Workflow",
}

/**
 * Maps telemetry event names to output channel categories.
 * Events not in this map display without a category prefix.
 */
const eventCategoryMap: Partial<Record<TelemetryEventType, LogCategory>> = {
  // LSP
  [TelemetryEventsKeys.LanguageServerInfo]: LogCategory.LSP,
  [TelemetryEventsKeys.LanguageServerError]: LogCategory.LSP,

  // Clone flow
  [TelemetryEventsKeys.CloneAgentClick]: LogCategory.Clone,
  [TelemetryEventsKeys.CloneAgentSuccess]: LogCategory.Clone,
  [TelemetryEventsKeys.CloneAgentCancel]: LogCategory.Clone,
  [TelemetryEventsKeys.CloneAgentError]: LogCategory.Clone,

  // Sync flow
  [TelemetryEventsKeys.SyncWorkspaceClick]: LogCategory.Sync,
  [TelemetryEventsKeys.SyncWorkspaceSuccess]: LogCategory.Sync,
  [TelemetryEventsKeys.SyncWorkspaceCancel]: LogCategory.Sync,
  [TelemetryEventsKeys.SyncWorkspaceError]: LogCategory.Sync,
  [TelemetryEventsKeys.GetRemoteFileError]: LogCategory.Sync,
  [TelemetryEventsKeys.GetLocalFileError]: LogCategory.Sync,

  // Auth
  [TelemetryEventsKeys.SignInError]: LogCategory.Auth,
  [TelemetryEventsKeys.ResetAccountError]: LogCategory.Auth,
  [TelemetryEventsKeys.SwitchAccountClick]: LogCategory.Auth,
  [TelemetryEventsKeys.SwitchAccountSuccess]: LogCategory.Auth,
  [TelemetryEventsKeys.SwitchAccountCancel]: LogCategory.Auth,
  [TelemetryEventsKeys.SwitchAccountError]: LogCategory.Auth,

  // Knowledge files
  [TelemetryEventsKeys.RefreshKnowledgeFilesClick]: LogCategory.Knowledge,
  [TelemetryEventsKeys.RefreshKnowledgeFilesSuccess]: LogCategory.Knowledge,
  [TelemetryEventsKeys.RefreshKnowledgeFilesError]: LogCategory.Knowledge,
  [TelemetryEventsKeys.DownloadKnowledgeFileClick]: LogCategory.Knowledge,
  [TelemetryEventsKeys.DownloadKnowledgeFileSuccess]: LogCategory.Knowledge,
  [TelemetryEventsKeys.UploadKnowledgeFileSuccess]: LogCategory.Knowledge,
  [TelemetryEventsKeys.DownloadKnowledgeFileError]: LogCategory.Knowledge,
  [TelemetryEventsKeys.OpenKnowledgeFileError]: LogCategory.Knowledge,
  [TelemetryEventsKeys.VirtualKnowledgeFileError]: LogCategory.Knowledge,

  // Reattach
  [TelemetryEventsKeys.ReattachAgentClick]: LogCategory.Reattach,
  [TelemetryEventsKeys.ReattachAgentError]: LogCategory.Reattach,
  [TelemetryEventsKeys.ReattachAgentInfo]: LogCategory.Reattach,
  [TelemetryEventsKeys.ReattachAgentSuccess]: LogCategory.Reattach,

  // Environment / Tree
  [TelemetryEventsKeys.RefreshAgentsClick]: LogCategory.AgentTree,
  [TelemetryEventsKeys.RefreshAgentsSuccess]: LogCategory.AgentTree,
  [TelemetryEventsKeys.RefreshAgentsError]: LogCategory.AgentTree,
  [TelemetryEventsKeys.LoadEnvironmentError]: LogCategory.AgentTree,
  [TelemetryEventsKeys.LoadEnvironmentSuccess]: LogCategory.AgentTree,
  [TelemetryEventsKeys.LoadAgentsSuccess]: LogCategory.AgentTree,
  [TelemetryEventsKeys.LoadAgentsError]: LogCategory.AgentTree,

  // Workflow
  [TelemetryEventsKeys.WorkflowVisualizeClick]: LogCategory.Workflow,
  [TelemetryEventsKeys.WorkflowVisualizeSuccess]: LogCategory.Workflow,
  [TelemetryEventsKeys.WorkflowFocusNodeError]: LogCategory.Workflow,
  [TelemetryEventsKeys.WorkflowEditEmbeddedJsonError]: LogCategory.Workflow,
  [TelemetryEventsKeys.WorkflowVisualizeError]: LogCategory.Workflow,
};

/**
 * Singleton logger that sends telemetry to Application Insights and writes to the VS Code output channel.
 * Events sent using this service will appear in the "customEvents" table in Application Insights.
 * It also displays messages in the VS Code UI based on the log level (Info, Warning, Error).
 *
 * - `logTrace`/`logDebug`: output channel only (no telemetry).
 * - `logInfo`/`logWarning`/`logError`: telemetry + output channel + optional UI popup.
 *
 * @remarks
 * - Automatically attaches `sessionId` property to all events, `isError` property to error events, and `isWarning` property to warning events.
 * - Supports sending events with just a name, or with additional message and data.
 * - PII: use `formatPii` to retain sensitive content in the UI while emitting a descriptive redaction in telemetry.
 *   Legacy `<pii>...</pii>` tags remain supported and emit `[REDACTED]`.
*/
class Logger {
  private static instance: Logger;
  private reporter: TelemetryReporter = NOOP_REPORTER;
  private sessionId!: string;
  private outputChannel: vscode.LogOutputChannel | undefined;

  private constructor() { }

  public static getInstance(): Logger {
    if (!Logger.instance) {
      Logger.instance = new Logger();
    }
    return Logger.instance;
  }

  public initialize(context: vscode.ExtensionContext, sessionId: string) {
    this.reporter = new TelemetryReporter(TELEMETRY_CONNECTION_STRING);
    this.sessionId = sessionId;
    context.subscriptions.push(this.reporter);
  }

  public async dispose() {
    await this.reporter.dispose();
  }

  /**
   * Sets the output channel for writing diagnostic logs.
   * Must be called after the output channel is created.
   */
  public setOutputChannel(channel: vscode.LogOutputChannel) {
    this.outputChannel = channel;
  }

  /** Writes a trace-level message to the output channel only (no telemetry). */
  public logTrace(category: LogCategory | string, message: string): void {
    this.outputChannel?.trace(`[${category}] ${message}`);
  }

  /** Writes a debug-level message to the output channel only (no telemetry). */
  public logDebug(category: LogCategory | string, message: string): void {
    this.outputChannel?.debug(`[${category}] ${message}`);
  }

  /**
   * Sends a telemetry event with the given name, message, and data.
   * Writes to the output channel for diagnostic purposes.
   * If message is provided, shows a message in the VS Code UI based on the log level.
   * PII in the message should be wrapped with `formatPii` to ensure it is redacted in telemetry.
   *
   * @param logLevel - The level of the log (Info, Warning, Error).
   * @param eventName - Telemetry event name.
   * @param message - Optional message string to display to the user.
   * @param data - Optional telemetry data object (any custom metadata to send with the event).
   */
  private log(
    logLevel: LogLevel,
    eventName: TelemetryEventType,
    message?: string,
    data?: TelemetryEventData
  ) {
    const { properties, measurements } = this.parseData(logLevel, data);
    const { displayMessage, telemetryProperties } = prepareLogData(message, properties);

    const canSendTelemetry = isTelemetryEnabled();
    this.writeToOutputChannel(logLevel, eventName, displayMessage, properties);

    switch (logLevel) {
      case LogLevel.Info:
        if (canSendTelemetry) {
          this.reporter.sendTelemetryEvent(eventName, telemetryProperties, measurements);
        }
        if (displayMessage) {
          vscode.window.showInformationMessage(displayMessage);
        }
        break;
      case LogLevel.Warning:
        if (canSendTelemetry) {
          this.reporter.sendTelemetryErrorEvent(eventName, telemetryProperties, measurements);
        }
        if (displayMessage) {
          vscode.window.showWarningMessage(displayMessage);
        }
        break;
      case LogLevel.Error:
        if (canSendTelemetry) {
          this.reporter.sendTelemetryErrorEvent(eventName, telemetryProperties, measurements);
        }
        if (displayMessage) {
          vscode.window.showErrorMessage(displayMessage);
        }
        break;
    }
  }

  /**
   * Sends a standard telemetry event.
   * If message is provided, shows an information message to users.
   * PII in the message should be wrapped with `formatPii` to ensure it is redacted in telemetry.
   *
   * @param eventName - The name of the telemetry event.
   * @param message - Optional message string to display to the user.
   * @param data - Optional telemetry data object (any custom metadata to send with the event).
   */
  public logInfo(
    eventName: TelemetryEventType,
    message?: string,
    data?: TelemetryEventData
  ) {
    this.log(LogLevel.Info, eventName, message, data);
  }

  /**
   * Sends a warning telemetry event.
   * If message is provided, shows a warning message to users.
   * PII in the message should be wrapped with `formatPii` to ensure it is redacted in telemetry.
   *
   * @param eventName - The name of the telemetry event.
   * @param message - Optional message string to display to the user.
   * @param data - Optional telemetry data object (any custom metadata to send with the event).
   */
  public logWarning(
    eventName: TelemetryEventType,
    message?: string,
    data?: TelemetryEventData
  ) {
    this.log(LogLevel.Warning, eventName, message, data);
  }

  /**
* Sends error-specific telemetry event.
   * If message is provided, shows an error message to users.
   * PII in the message should be wrapped with `formatPii` to ensure it is redacted in telemetry.
   *
   * @param eventName - The name of the telemetry event.
   * @param message - Optional message string to display to the user.
   * @param data - Optional telemetry data object (any custom metadata to send with the event).
*/
  public logError(
    eventName: TelemetryEventType,
    message?: string,
    data?: TelemetryEventData
  ) {
    this.log(LogLevel.Error, eventName, message, data);
  }

  /** Writes the event message to the output channel with optional [Category] prefix. */
  private writeToOutputChannel(
    logLevel: LogLevel,
    eventName: TelemetryEventType,
    displayMessage: string | undefined,
    properties: TelemetryEventProperties,
  ): void {
    if (!this.outputChannel) {
      return;
    }

    const category = eventCategoryMap[eventName];

    const propertyMessage = properties?.['message'] as string | undefined;
    const mainMessage = displayMessage ?? (propertyMessage ? stripPiiTags(propertyMessage) : undefined);
    if (!mainMessage) {
      return;
    }

    const prefix = category ? `[${category}] ` : '';
    const outputLine = `${prefix}${mainMessage}`;

    switch (logLevel) {
      case LogLevel.Info:
        this.outputChannel.info(outputLine);
        break;
      case LogLevel.Warning:
        this.outputChannel.warn(outputLine);
        break;
      case LogLevel.Error:
        this.outputChannel.error(outputLine);
        break;
    }
  }

  private parseData(
    logLevel: LogLevel,
    data?: TelemetryEventData,
  ): TelemetryEventProps {
    const properties: Record<string, string> = {
      sessionId: this.sessionId,
      ...(logLevel === LogLevel.Warning && { isWarning: "true" }),
      ...(logLevel === LogLevel.Error && { isError: "true" }),
    };
    const measurements: Record<string, number> = {};

    if (data) {
      for (const [key, value] of Object.entries(data)) {
        if (typeof value === 'string') {
          properties[key] = value;
        } else if (typeof value === 'number') {
          measurements[key] = value;
        }
      }
    }

    return { properties, measurements };
  }
}

export default Logger.getInstance();
