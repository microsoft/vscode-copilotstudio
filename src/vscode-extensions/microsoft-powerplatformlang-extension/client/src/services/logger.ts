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
 * - PII: wrap sensitive content in `<pii>...</pii>` tags for automatic redaction in telemetry.
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
   * PII in the message should be wrapped in `<pii>...</pii>` tags to ensure it is redacted in telemetry.
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

    // A clean version of the message for the user, with PII tags stripped out
    const displayMessage = message?.replace(/<pii>(.*?)<\/pii>/g, '$1');

    // The message for telemetry with potential PII tags
    const rawMessage = properties?.message as string || message;

    // A redacted version for telemetry where PII content is replaced with [REDACTED]
    const redactedMessage = rawMessage?.replace(/<pii>.*?<\/pii>/g, '[REDACTED]');
    const updatedProperties = redactedMessage ? { ...properties, message: redactedMessage } : properties;

    const canSendTelemetry = isTelemetryEnabled();
    this.writeToOutputChannel(logLevel, eventName, displayMessage, properties);

    switch (logLevel) {
      case LogLevel.Info:
        if (canSendTelemetry) {
          this.reporter.sendTelemetryEvent(eventName, updatedProperties, measurements);
        }
        if (displayMessage) {
          vscode.window.showInformationMessage(displayMessage);
        }
        break;
      case LogLevel.Warning:
        if (canSendTelemetry) {
          this.reporter.sendTelemetryErrorEvent(eventName, updatedProperties, measurements);
        }
        if (displayMessage) {
          vscode.window.showWarningMessage(displayMessage);
        }
        break;
      case LogLevel.Error:
        if (canSendTelemetry) {
          this.reporter.sendTelemetryErrorEvent(eventName, updatedProperties, measurements);
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
   * PII in the message should be wrapped in `<pii>...</pii>` tags to ensure it is redacted in telemetry.
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
   * PII in the message should be wrapped in `<pii>...</pii>` tags to ensure it is redacted in telemetry.
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
   * PII in the message should be wrapped in `<pii>...</pii>` tags to ensure it is redacted in telemetry.
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

    const mainMessage = displayMessage ?? (properties?.['message'] as string | undefined)?.replace(/<pii>(.*?)<\/pii>/g, '$1');
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
        } else {
          properties[key] = JSON.stringify(value);
        }
      }
    }

    return { properties, measurements };
  }
}

export default Logger.getInstance();
