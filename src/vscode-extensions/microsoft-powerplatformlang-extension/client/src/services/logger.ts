import * as vscode from 'vscode';
import { TelemetryEventMeasurements, TelemetryEventProperties, TelemetryReporter } from "@vscode/extension-telemetry";
import { LogLevel, TELEMETRY_CONNECTION_STRING, TelemetryEventsKeys, type TelemetryEventType } from '../constants';
import { isTelemetryEnabled } from './telemetry';
import { SharedDimensions, EventDimensions } from './telemetryDimensions';

type TelemetryEventProps = {
  properties: TelemetryEventProperties,
  measurements: TelemetryEventMeasurements,
};

/**
 * Allowed keys for telemetry event data.
 * All keys must be declared in telemetryDimensions.ts before use.
 */
type AllowedDimensionKey =
  | typeof SharedDimensions[keyof typeof SharedDimensions]
  | typeof EventDimensions[keyof typeof EventDimensions]
  | 'error';

/**
 * Type definitions for telemetry event parameters:
 * - properties: Record<string, string> (string key-value pairs for event metadata)
 * - measurements: Record<string, number> (string key-number pairs for numeric metrics)
 * - error: special key — pass an Error object and the logger automatically extracts `errorMessage`
 *
 * Keys are constrained to declared dimensions in telemetryDimensions.ts.
 * Add new dimensions there before using them here.
 */
type TelemetryEventData = {
  [K in AllowedDimensionKey]?: K extends 'error' ? unknown : string | number;
};

export enum PiiRedactionType {
  AccountIdentifier = 'ACCOUNT IDENTIFIER',
  AgentName = 'AGENT NAME',
  EmailAddress = 'EMAIL ADDRESS',
  FileUri = 'FILE URI',
  Url = 'URL',
  AiBuilderPromptDetails = 'AI BUILDER PROMPT DETAILS',
  WorkflowErrorDetails = 'WORKFLOW ERROR DETAILS',
  WorkflowNames = 'WORKFLOW NAMES',
}

interface PiiMatch {
  start: number;
  end: number;
  type: PiiRedactionType | string;
  priority: number;
}

const piiPattern = /<pii(?: type="([^"]+)")?(?: encoded="(true)")?>(.*?)<\/pii>/gs;

const encodePiiValue = (value: string): string =>
  value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

const decodePiiValue = (value: string): string =>
  value.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');

export function formatPii(value: string, type: PiiRedactionType | string): string {
  return `<pii type="${type}" encoded="true">${encodePiiValue(value)}</pii>`;
}

/**
 * Derives a telemetry redaction label from a file path's extension chain so the
 * redacted marker discloses the file type without leaking the file name or path.
 * Examples: "agent.mcs.yml" -> ".MCS.YML FILE NAME", "botdefinition.json" -> ".JSON FILE NAME".
 * Falls back to "FILE NAME" when no extension is present.
 */
function fileExtensionLabel(filePath: string): string {
  const baseName = filePath.split(/[\\/]/).pop() ?? filePath;
  const firstDot = baseName.indexOf('.', 1);
  const extension = (firstDot >= 0 ? baseName.slice(firstDot) : '')
    .replace(/[^A-Za-z0-9.]/g, '')
    .toUpperCase();
  return extension ? `${extension} FILE NAME` : 'FILE NAME';
}

/**
 * Wraps a known file path or name so telemetry shows only its extension,
 * e.g. "[REDACTED .JSON FILE NAME]", while the user still sees the full path.
 */
export function formatFileName(filePath: string): string {
  return formatPii(filePath, fileExtensionLabel(filePath));
}

function collectMatches(
  value: string,
  pattern: RegExp,
  type: PiiRedactionType | string,
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

/**
 * Like {@link collectMatches}, but derives a per-match redaction label from the
 * matched file path's extension so telemetry discloses the file type only.
 */
function collectFileMatches(
  value: string,
  pattern: RegExp,
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
      type: fileExtensionLabel(capturedValue),
      priority,
    });
  }
  return matches;
}

export function sanitizeErrorDetails(errorMessage: string, agentNames: readonly string[] = []): string {
  const matches: PiiMatch[] = [
    // Full URL (scheme://host/path...). Matched first and whole so a tenant or
    // Dataverse endpoint is redacted as a URL rather than being partially
    // consumed and mislabeled as a file name by the path rules below.
    ...collectMatches(
      errorMessage,
      /[A-Za-z][A-Za-z0-9+.-]*:\/\/[^\s<>"'|]+/gi,
      PiiRedactionType.Url,
      6,
    ),
    ...collectMatches(
      errorMessage,
      /[A-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Z0-9-]+(?:\.[A-Z0-9-]+)+/gi,
      PiiRedactionType.EmailAddress,
      5,
    ),
    // Drive-letter path ending in any file name: C:\dir\file.ext
    // Allows spaces in directory names and filenames (e.g. C:\Users\Alex Doe\my agent.mcs.yml).
    // The final segment excludes @ to avoid consuming into email addresses.
    ...collectFileMatches(
      errorMessage,
      /[A-Za-z]:[\\/](?:[^\\/\r\n:*?"<>|]+[\\/])*[^\\/\r\n:*?"<>|@]*\.[A-Za-z0-9]{1,12}(?=[\s,;)\]}>:"'@]|$)/gi,
      4,
    ),
    // UNC path ending in any file name: \\server\share\file.ext
    ...collectFileMatches(
      errorMessage,
      /\\\\[^\\/\r\n:*?"<>|]+[\\/](?:[^\\/\r\n:*?"<>|]+[\\/])*[^\\/\r\n:*?"<>|@]*\.[A-Za-z0-9]{1,12}(?=[\s,;)\]}>:"'@]|$)/gi,
      4,
    ),
    // POSIX path ending in any file name: /dir/file.ext
    ...collectFileMatches(
      errorMessage,
      /(?:\/[^\/\r\n"'<>|?*]+)*\/[^\/\r\n"'<>|?*@]*\.[A-Za-z0-9]{1,12}(?=[\s,;)\]}>:"'@]|$)/gi,
      4,
    ),
    // Relative or mixed-separator path ending in any file name: dir\file.ext
    ...collectFileMatches(
      errorMessage,
      /(?:[^\s\\/:"'<>|?*]+[\\/])+[^\s\\/:"'<>|?*]*\.[A-Za-z0-9]{1,12}/gi,
      4,
    ),
    // Bare file name (no path). Each extension segment must be letter-initial and
    // at least two characters, so version numbers ("1.2.3") and abbreviations
    // ("e.g.", "U.S.A") are not mistaken for file names.
    ...collectFileMatches(
      errorMessage,
      /[A-Za-z0-9_-]+(?:\.[A-Za-z][A-Za-z0-9]{1,11})+/gi,
      3,
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

/**
 * Redacts PII from a telemetry value. Two-step process:
 * 1. Replaces explicit <pii type="TYPE">...</pii> tags with [REDACTED TYPE]
 * 2. Applies pattern-based detection for untagged PII (emails, paths, URLs) as defense-in-depth
 */
const redactPii = (value: string): string => {
  // Step 1: Replace explicit <pii> tags
  let result = value.replace(piiPattern, (_match, type: string | undefined) =>
    type ? `[REDACTED ${type}]` : '[REDACTED]');

  // Step 2: Pattern-based detection for untagged PII that callers missed
  result = result.replace(/[A-Za-z][A-Za-z0-9+.-]*:\/\/[^\s<>"'|]+/gi, '[REDACTED URL]');
  result = result.replace(/[A-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Z0-9-]+(?:\.[A-Z0-9-]+)+/gi, '[REDACTED EMAIL ADDRESS]');
  result = result.replace(/[A-Za-z]:[\\/](?:[^\\/\r\n:*?"<>|]+[\\/])*[^\\/\r\n:*?"<>|@]*\.[A-Za-z0-9]{1,12}(?=[\s,;)\]}>:"'@]|$)/gi, '[REDACTED FILE PATH]');
  result = result.replace(/\\\\[^\\/\r\n:*?"<>|]+[\\/](?:[^\\/\r\n:*?"<>|]+[\\/])*[^\\/\r\n:*?"<>|@]*\.[A-Za-z0-9]{1,12}(?=[\s,;)\]}>:"'@]|$)/gi, '[REDACTED FILE PATH]');
  result = result.replace(/(?:\/[^\/\r\n"'<>|?*]+)*\/[^\/\r\n"'<>|?*@]*\.[A-Za-z0-9]{1,12}(?=[\s,;)\]}>:"'@]|$)/gi, '[REDACTED FILE PATH]');

  return result;
};

export function prepareLogData(message: string | undefined, properties: TelemetryEventProperties): {
  displayMessage?: string;
  outputMessage?: string;
  telemetryProperties: Record<string, string>;
} {
  const rawErrorMessage = properties?.['errorMessage'] as string | undefined;
  const strippedError = rawErrorMessage ? stripPiiTags(rawErrorMessage) : undefined;

  // --- 1. VS Code popup: msg with <pii> tags stripped + error.message ---
  let displayMessage = message ? stripPiiTags(message) : undefined;
  if (strippedError && displayMessage && !displayMessage.includes(strippedError)) {
    displayMessage = `${displayMessage}: ${strippedError}`;
  }

  // --- 2. Output channel: (data.message || msg) with <pii> stripped + error.message ---
  const rawMessage = (properties?.['message'] as string | undefined) ?? message;
  let outputMessage = rawMessage ? stripPiiTags(rawMessage) : undefined;
  if (strippedError && outputMessage && !outputMessage.includes(strippedError)) {
    outputMessage = `${outputMessage}: ${strippedError}`;
  }

  // --- 3. Telemetry: sanitized dimensions only ---
  const telemetryProperties: Record<string, string> = {};
  for (const [key, value] of Object.entries(properties)) {
    if (typeof value === 'string') {
      telemetryProperties[key] = redactPii(value);
    } else if (value !== undefined) {
      telemetryProperties[key] = String(value);
    }
  }

  // Always populate message dimension for telemetry
  if (!telemetryProperties.message && message) {
    telemetryProperties.message = redactPii(message);
  }

  return { displayMessage, outputMessage, telemetryProperties };
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
  private version!: string;
  private isDevMode!: string;
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
    this.version = context.extension.packageJSON.version ?? '1.0.0';
    this.isDevMode = (process.env.VSCODE_DEBUG === 'true').toString();
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
    this.outputChannel?.trace(`[${category}] ${stripPiiTags(message)}`);
  }

  /** Writes a debug-level message to the output channel only (no telemetry). */
  public logDebug(category: LogCategory | string, message: string): void {
    this.outputChannel?.debug(`[${category}] ${stripPiiTags(message)}`);
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
    const { properties, measurements } = this.parseData(logLevel, message, data);
    const { displayMessage, outputMessage, telemetryProperties } = prepareLogData(message, properties);

    this.writeToOutputChannel(logLevel, eventName, outputMessage, measurements);

    const canSendTelemetry = isTelemetryEnabled();

    switch (logLevel) {
      case LogLevel.Info:
        if (canSendTelemetry) {
          this.reporter.sendTelemetryEvent(eventName, telemetryProperties, measurements);
        }
        if (displayMessage) {
          void vscode.window.showInformationMessage(displayMessage);
        }
        break;
      case LogLevel.Warning:
        if (canSendTelemetry) {
          this.reporter.sendTelemetryErrorEvent(eventName, telemetryProperties, measurements);
        }
        if (displayMessage) {
          void vscode.window.showWarningMessage(displayMessage);
        }
        break;
      case LogLevel.Error:
        if (canSendTelemetry) {
          this.reporter.sendTelemetryErrorEvent(eventName, telemetryProperties, measurements);
        }
        if (displayMessage) {
          void vscode.window.showErrorMessage(displayMessage);
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
  * **IMPORTANT:** Do NOT embed error details in the message string.
  * Pass the error object via `data: { error }` instead — the logger auto-extracts
  * `errorMessage` dimension and appends error details to the display message.
  *
  * @param eventName - The name of the telemetry event.
  * @param message - Optional descriptive message (what failed, NOT why). Error details are auto-appended.
  * @param data - Optional telemetry data. Pass `{ error }` for automatic error extraction.
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
  * PII in the message should be wrapped in `<pii>...</pii>` tags to ensure it is redacted in telemetry.
  *
  * **IMPORTANT:** Do NOT embed error details in the message string.
  * Pass the error object via `data: { error }` instead — the logger auto-extracts
  * `errorMessage` dimension and appends error details to the display message.
  *
  * @example
  * // ✅ Correct:
  * logger.logError(event, 'Failed to sync workspace', { error, durationMs });
  *
  * // ❌ Wrong — error message in the display string:
  * logger.logError(event, `Failed to sync workspace: ${(error as Error).message}`);
  *
  * @param eventName - The name of the telemetry event.
  * @param message - Optional descriptive message (what failed, NOT why). Error details are auto-appended.
  * @param data - Optional telemetry data. Pass `{ error }` for automatic error extraction.
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
    message?: string,
    measurements?: TelemetryEventMeasurements,
  ): void {
    if (!this.outputChannel || !message) {
      return;
    }

    const category = eventCategoryMap[eventName];

    // Replace :: with / in output channel for LSP messages only (telemetry uses :: to avoid PII flagging)
    let outputLine = category === LogCategory.LSP ? message.replace(/::/g, '/') : message;

    const durationMs = measurements?.['durationMs'];
    if (durationMs !== undefined) {
      outputLine = `${outputLine}, duration=${durationMs}ms`;
    }

    if (category) {
      outputLine = `[${category}] ${outputLine}`;
    }

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
    message?: string,
    data?: TelemetryEventData,
  ): TelemetryEventProps {
    const logLevelValue = logLevel === LogLevel.Error ? 'error'
      : logLevel === LogLevel.Warning ? 'warning' : 'info';

    const properties: Record<string, string> = {
      [SharedDimensions.sessionId]: this.sessionId,
      [SharedDimensions.version]: this.version,
      [SharedDimensions.isDevMode]: this.isDevMode,
      [SharedDimensions.logLevel]: logLevelValue,
    };
    const measurements: Record<string, number> = {};

    if (data) {
      for (const [key, value] of Object.entries(data)) {
        if (key === 'error') {
          // Error messages from external sources — detect and wrap PII patterns
          if (value instanceof Error) {
            properties[SharedDimensions.errorMessage] = sanitizeErrorDetails(value.message);
          } else if (value !== null && value !== undefined) {
            properties[SharedDimensions.errorMessage] = sanitizeErrorDetails(String(value));
          }
        } else if (typeof value === 'string') {
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
