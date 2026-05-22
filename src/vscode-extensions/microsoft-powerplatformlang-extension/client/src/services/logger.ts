import * as vscode from 'vscode';
import { TelemetryEventMeasurements, TelemetryEventProperties, TelemetryReporter } from "@vscode/extension-telemetry";
import { LogLevel, TELEMETRY_CONNECTION_STRING, type TelemetryEventType } from '../constants';
import { isTelemetryEnabled } from './telemetry';

type TelemetryEventProps = {
  properties: Record<string, string>,
  measurements?: Record<string, number>,
};

/**
 * Type definitions for telemetry event parameters:
 * - properties: Record<string, string> (string key-value pairs for event metadata)
 * - measurements: Record<string, number> (string key-number pairs for numeric metrics)
 */
type TelemetryEventData = TelemetryEventProperties | TelemetryEventMeasurements | Record<string, string | number>;

/**
 * Options for log methods controlling destinations and UI behavior.
 */
interface LogOptions {
  /** Additional telemetry data (properties and measurements). */
  data?: TelemetryEventData;
  /** If true, shows a VS Code notification to the user. */
  showUI?: boolean;
  /** User-facing message for notifications (separate from diagnostic message). Falls back to the log message if not provided. */
  userMessage?: string;
}

/**
 * Tracks a single operation's lifecycle for duration and outcome telemetry.
 */
interface OperationTracker {
  /** Call on successful completion. Emits telemetry + output log with duration. */
  success(data?: TelemetryEventData): void;
  /** Call on failure. Emits error telemetry + output log with duration and error details. */
  failure(error: unknown, options?: { showUI?: boolean; userMessage?: string }): void;
}

/**
 * Unified logging service for the Copilot Studio extension.
 *
 * Destinations:
 * - **Output Channel**: All levels (Trace through Error) for debugging via VS Code output window.
 * - **Telemetry**: Info, Warning, Error only — for high-signal monitoring in Application Insights.
 * - **UI Notifications**: Opt-in via `showUI: true` — shows vscode info/warning/error messages.
 *
 * PII: Wrap sensitive content in `<pii>...</pii>` tags. It will be redacted in telemetry
 * and displayed as-is only in the output channel (which is local to the user's machine).
 */
class Logger {
  private static instance: Logger;
  private reporter!: TelemetryReporter;
  private sessionId!: string;
  private outputChannel: vscode.LogOutputChannel | null = null;

  private constructor() { }

  public static getInstance(): Logger {
    if (!Logger.instance) {
      Logger.instance = new Logger();
    }
    return Logger.instance;
  }

  public initialize(context: vscode.ExtensionContext, sessionId: string, outputChannel?: vscode.LogOutputChannel) {
    this.reporter = new TelemetryReporter(TELEMETRY_CONNECTION_STRING);
    this.sessionId = sessionId;
    this.outputChannel = outputChannel ?? null;
    context.subscriptions.push(this.reporter);
  }

  public async dispose() {
    await this.reporter.dispose();
  }

  // ─── Core log method ─────────────────────────────────────────────────

  /**
   * Sends a log entry to the appropriate destinations based on level.
   *
   * @param logLevel - Severity level controlling routing.
   * @param eventName - Telemetry event name (from TelemetryEventsKeys).
   * @param message - Diagnostic message for output channel and telemetry.
   * @param options - Controls showUI, userMessage, and additional data.
   */
  public log(
    logLevel: LogLevel,
    eventName: TelemetryEventType,
    message?: string,
    options?: LogOptions | TelemetryEventData
  ) {
    // Support legacy signature: log(level, event, message, data)
    const opts = this.normalizeOptions(options);

    // Strip PII tags for display in output channel (local-only, safe)
    const displayMessage = message?.replace(/<pii>(.*?)<\/pii>/g, '$1');

    // Redact PII for telemetry
    const redactedMessage = message?.replace(/<pii>.*?<\/pii>/g, '[REDACTED]');

    // 1. Output Channel — all levels
    if (displayMessage) {
      this.writeToOutputChannel(logLevel, eventName, displayMessage);
    }

    // 2. Telemetry — Info, Warning, Error only
    if (logLevel !== LogLevel.Trace && logLevel !== LogLevel.Debug) {
      this.sendTelemetry(logLevel, eventName, redactedMessage, opts.data);
    }

    // 3. UI Notification — opt-in only
    if (opts.showUI) {
      const uiText = opts.userMessage?.replace(/<pii>(.*?)<\/pii>/g, '$1')
        || displayMessage;
      if (uiText) {
        this.showUIMessage(logLevel, uiText);
      }
    }
  }

  // ─── Convenience methods ────────────────────────────────────────────

  /** Trace-level log. Output channel only. */
  public trace(context: string, message: string) {
    this.writeToOutputChannel(LogLevel.Trace, context, message);
  }

  /** Debug-level log. Output channel only. */
  public debug(context: string, message: string) {
    this.writeToOutputChannel(LogLevel.Debug, context, message);
  }

  /** Info-level log with telemetry. */
  public logInfo(
    eventName: TelemetryEventType,
    message?: string,
    options?: LogOptions | TelemetryEventData
  ) {
    this.log(LogLevel.Info, eventName, message, options);
  }

  /** Warning-level log with telemetry. */
  public logWarning(
    eventName: TelemetryEventType,
    message?: string,
    options?: LogOptions | TelemetryEventData
  ) {
    this.log(LogLevel.Warning, eventName, message, options);
  }

  /** Error-level log with telemetry. */
  public logError(
    eventName: TelemetryEventType,
    message?: string,
    options?: LogOptions | TelemetryEventData
  ) {
    this.log(LogLevel.Error, eventName, message, options);
  }

  // ─── Operation tracking ─────────────────────────────────────────────

  /**
   * Starts tracking an operation for duration and outcome telemetry.
   *
   * @param operationName - Human-readable operation name for output logs.
   * @param successEvent - Telemetry event key emitted on success.
   * @param errorEvent - Telemetry event key emitted on failure.
   * @returns An OperationTracker with success() and failure() methods.
   *
   * @example
   * ```ts
   * const op = logger.startOperation("CloneAgent", TelemetryEventsKeys.CloneAgentSuccess, TelemetryEventsKeys.CloneAgentError);
   * try {
   *   await doWork();
   *   op.success({ agentId });
   * } catch (e) {
   *   op.failure(e, { showUI: true, userMessage: "Failed to clone agent." });
   * }
   * ```
   */
  public startOperation(
    operationName: string,
    successEvent: TelemetryEventType,
    errorEvent: TelemetryEventType
  ): OperationTracker {
    const startTime = performance.now();
    this.writeToOutputChannel(LogLevel.Info, operationName, 'Starting');

    return {
      success: (data?: TelemetryEventData) => {
        const durationMs = Math.round(performance.now() - startTime);
        const measurements = { durationMs, ...(this.extractMeasurements(data)) };
        const properties = this.extractProperties(data);

        this.writeToOutputChannel(LogLevel.Info, operationName, `Completed (${durationMs}ms)`);
        this.sendTelemetry(LogLevel.Info, successEvent, undefined, { ...properties, ...measurements });
      },
      failure: (error: unknown, options?: { showUI?: boolean; userMessage?: string }) => {
        const durationMs = Math.round(performance.now() - startTime);
        const err = error instanceof Error ? error : new Error(String(error));
        const errorMessage = err.message?.replace(/<pii>.*?<\/pii>/g, '[REDACTED]');

        this.writeToOutputChannel(LogLevel.Error, operationName, `Failed (${durationMs}ms): ${err.message}`);
        this.sendTelemetry(LogLevel.Error, errorEvent, errorMessage, {
          durationMs,
          errorType: err.name,
        });

        if (options?.showUI) {
          const uiText = options.userMessage || `${operationName} failed: ${err.message}`;
          this.showUIMessage(LogLevel.Error, uiText);
        }
      }
    };
  }

  // ─── Private helpers ────────────────────────────────────────────────

  private normalizeOptions(options?: LogOptions | TelemetryEventData): LogOptions {
    if (!options) {return {};}
    // Detect legacy TelemetryEventData (plain object with string/number values)
    if ('showUI' in options || 'userMessage' in options || 'data' in options) {
      return options as LogOptions;
    }
    // Legacy: treat as raw data
    return { data: options as TelemetryEventData };
  }

  private writeToOutputChannel(level: LogLevel, context: string, message: string) {
    if (!this.outputChannel) {return;}
    const formatted = `[${context}] ${message}`;
    switch (level) {
      case LogLevel.Trace: this.outputChannel.trace(formatted); break;
      case LogLevel.Debug: this.outputChannel.debug(formatted); break;
      case LogLevel.Info: this.outputChannel.info(formatted); break;
      case LogLevel.Warning: this.outputChannel.warn(formatted); break;
      case LogLevel.Error: this.outputChannel.error(formatted); break;
    }
  }

  private sendTelemetry(
    logLevel: LogLevel,
    eventName: TelemetryEventType,
    message?: string,
    data?: TelemetryEventData
  ) {
    if (!isTelemetryEnabled()) {return;}

    const { properties, measurements } = this.parseData(logLevel, data);
    if (message) {
      properties.message = message;
    }

    switch (logLevel) {
      case LogLevel.Info:
        this.reporter.sendTelemetryEvent(eventName, properties, measurements);
        break;
      case LogLevel.Warning:
      case LogLevel.Error:
        this.reporter.sendTelemetryErrorEvent(eventName, properties, measurements);
        break;
    }
  }

  private showUIMessage(level: LogLevel, message: string) {
    switch (level) {
      case LogLevel.Info: vscode.window.showInformationMessage(message); break;
      case LogLevel.Warning: vscode.window.showWarningMessage(message); break;
      case LogLevel.Error: vscode.window.showErrorMessage(message); break;
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

  private extractProperties(data?: TelemetryEventData): Record<string, string> {
    if (!data) {return {};}
    const result: Record<string, string> = {};
    for (const [key, value] of Object.entries(data)) {
      if (typeof value === 'string') {result[key] = value;}
    }
    return result;
  }

  private extractMeasurements(data?: TelemetryEventData): Record<string, number> {
    if (!data) {return {};}
    const result: Record<string, number> = {};
    for (const [key, value] of Object.entries(data)) {
      if (typeof value === 'number') {result[key] = value;}
    }
    return result;
  }
}

export default Logger.getInstance();
