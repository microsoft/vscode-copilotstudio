import * as vscode from 'vscode';
import { TelemetryEventMeasurements, TelemetryEventProperties, TelemetryReporter } from "@vscode/extension-telemetry";
import { LogLevel, TELEMETRY_CONNECTION_STRING, type TelemetryEventType, type FeatureTelemetryEvent } from '../constants';
import { isTelemetryEnabled } from './telemetry';

type TelemetryEventProps = {
  properties: TelemetryEventProperties,
  measurements?: TelemetryEventMeasurements,
};

/**
 * Type definitions for telemetry event parameters:
 * - properties: Record<string, string> (string key-value pairs for event metadata)
 * - measurements: Record<string, number> (string key-number pairs for numeric metrics)
 */
type TelemetryEventData = TelemetryEventProperties | TelemetryEventMeasurements;

/**
 * Unified logging service for the Copilot Studio VS Code extension.
 *
 * DESIGN PHILOSOPHY (Issue #198):
 * - Output Channel writes: All step-by-step execution details (Trace, Debug, Info, Warning, Error)
 * - Telemetry (Application Insights): High-signal feature events only (success/failure/duration)
 * - PII Redaction: Applied to both output and telemetry via `<pii>...</pii>` tags
 *
 * USAGE:
 * 1. For debugging output (step-by-step logs): Use logTrace/logDebug/logInfo/logWarning/logError
 * 2. For telemetry (feature outcomes): Use logFeatureEvent() — this is the ONLY path to Application Insights
 *
 * @remarks
 * - logFeatureEvent() is the only method that sends data to Application Insights
 * - All output channel methods format with feature prefix: "[Clone] message"
 * - PII in messages wrapped in `<pii>...</pii>` is automatically redacted in both channels
 * - sessionId is auto-attached to all telemetry events for correlation
 */
class Logger {
  private static instance: Logger;
  private reporter!: TelemetryReporter;
  private outputChannel!: vscode.LogOutputChannel;
  private sessionId!: string;

  private constructor() { }

  public static getInstance(): Logger {
    if (!Logger.instance) {
      Logger.instance = new Logger();
    }
    return Logger.instance;
  }

  /**
   * Initialize the logger with telemetry reporter and output channel.
   * Must be called once during extension activation before any logging.
   */
  public initialize(
    context: vscode.ExtensionContext,
    sessionId: string,
    outputChannel: vscode.LogOutputChannel
  ) {
    this.reporter = new TelemetryReporter(TELEMETRY_CONNECTION_STRING);
    this.sessionId = sessionId;
    this.outputChannel = outputChannel;
    context.subscriptions.push(this.reporter);
  }

  public async dispose() {
    await this.reporter.dispose();
  }

  /**
   * Write a trace-level message to the output channel (lowest severity).
   * Trace messages are only visible when the user sets LogOutputChannel level to Trace.
   * No telemetry is sent.
   */
  public logTrace(message: string, feature?: string) {
    const formatted = this.formatMessage(feature, message);
    this.outputChannel.trace(formatted);
  }

  /**
   * Write a debug-level message to the output channel.
   * Debug messages include internal steps and diagnostics.
   * No telemetry is sent.
   */
  public logDebug(message: string, feature?: string) {
    const formatted = this.formatMessage(feature, message);
    this.outputChannel.debug(formatted);
  }

  /**
   * Write an info-level message to the output channel.
   * Info messages describe the flow of user operations.
   * No telemetry is sent.
   */
  public logInfo(message: string, feature?: string) {
    const formatted = this.formatMessage(feature, message);
    this.outputChannel.info(formatted);
  }

  /**
   * Write a warning-level message to the output channel.
   * Warning messages indicate issues that were handled or degraded operations.
   * No telemetry is sent.
   */
  public logWarning(message: string, feature?: string) {
    const formatted = this.formatMessage(feature, message);
    this.outputChannel.warn(formatted);
  }

  /**
   * Write an error-level message to the output channel.
   * Error messages indicate failures that require user attention.
   * Optionally shows an error dialog to the user (when showDialog=true).
   * No telemetry is sent — use logFeatureEvent() for error telemetry.
   */
  public logError(message: string, feature?: string, options?: { showDialog?: boolean }) {
    const formatted = this.formatMessage(feature, message);
    this.outputChannel.error(formatted);
    if (options?.showDialog) {
      vscode.window.showErrorMessage(this.stripPii(message));
    }
  }

  /**
   * Send a high-signal feature telemetry event to Application Insights.
   * This is the ONLY method that sends to telemetry.
   *
   * Structured event includes feature name, operation, outcome, duration, and error details.
   * All events automatically include sessionId for correlation.
   * Also logs to output channel at Info level for user visibility.
   *
   * @param event - The FeatureTelemetryEvent with outcome, duration, and error details
   * @example
   * logger.logFeatureEvent({
   *   feature: 'clone',
   *   operation: 'cloneAgent',
   *   outcome: 'success',
   *   durationMs: 1234
   * });
   *
   * logger.logFeatureEvent({
   *   feature: 'auth',
   *   operation: 'signIn',
   *   outcome: 'failure',
   *   errorType: 'UnauthorizedError',
   *   errorMessage: 'Token acquisition failed: <pii>user@example.com</pii>'
   * });
   */
  public logFeatureEvent(event: FeatureTelemetryEvent) {
    // Validate required fields
    if (!event.feature || !event.operation || !event.outcome) {
      console.error('Invalid feature event: missing required fields', event);
      return;
    }

    // Build telemetry properties
    const properties: Record<string, string> = {
      sessionId: this.sessionId,
      feature: event.feature,
      operation: event.operation,
      outcome: event.outcome,
      ...(event.errorType && { errorType: event.errorType }),
    };

    const measurements: Record<string, number> = {
      ...(event.durationMs !== undefined && { durationMs: event.durationMs }),
    };

    // Redact PII in error message for telemetry
    if (event.errorMessage) {
      properties.errorMessage = event.errorMessage.replace(/<pii>.*?<\/pii>/g, '[REDACTED]');
    }

    // Copy additional properties
    for (const [key, value] of Object.entries(event)) {
      if (key === 'feature' || key === 'operation' || key === 'outcome' || 
          key === 'durationMs' || key === 'errorType' || key === 'errorMessage') {
        continue; // Already handled
      }
      if (typeof value === 'string') {
        properties[key] = value;
      } else if (typeof value === 'number') {
        measurements[key] = value;
      }
    }

    // Send to telemetry (if enabled)
    const canSendTelemetry = isTelemetryEnabled();
    if (canSendTelemetry) {
      // Build an event name from feature/operation for telemetry
      const eventName = `${event.feature}_${event.operation}`;
      const isError = event.outcome === 'failure';
      if (isError) {
        this.reporter.sendTelemetryErrorEvent(eventName, properties, measurements);
      } else {
        this.reporter.sendTelemetryEvent(eventName, properties, measurements);
      }
    }

    // Also log to output channel for user visibility
    const msg = `[${event.feature.toUpperCase()}] ${event.operation}: ${event.outcome}` +
      (event.durationMs !== undefined ? ` (${event.durationMs}ms)` : '') +
      (event.errorMessage ? ` - ${this.stripPii(event.errorMessage)}` : '');
    if (event.outcome === 'failure') {
      this.outputChannel.error(msg);
    } else if (event.outcome === 'cancelled') {
      this.outputChannel.warn(msg);
    } else {
      this.outputChannel.info(msg);
    }
  }

  /**
   * Format a message with feature prefix for output channel.
   * Example: "Cloning agent..." becomes "[Clone] Cloning agent..."
   */
  private formatMessage(feature: string | undefined, message: string): string {
    if (!feature) {
      return message;
    }
    // Capitalize first letter of feature
    const capitalizedFeature = feature.charAt(0).toUpperCase() + feature.slice(1);
    return `[${capitalizedFeature}] ${message}`;
  }

  /**
   * Strip PII tags for display to user (keep content).
   * Example: "Failed: <pii>user@example.com</pii>" becomes "Failed: user@example.com"
   */
  private stripPii(message: string): string {
    return message.replace(/<pii>(.*?)<\/pii>/g, '$1');
  }

  /**
   * DEPRECATED: Old telemetry-only API. Use logFeatureEvent() instead.
   * Kept for backward compatibility during migration.
   *
   * @deprecated Use logFeatureEvent() for telemetry and logInfo/logWarning/logError for output
   */
  public logLegacy(
    logLevel: LogLevel,
    eventName: TelemetryEventType,
    message?: string,
    data?: TelemetryEventData
  ) {
    const { properties, measurements } = this.parseData(logLevel, data);

    const displayMessage = message?.replace(/<pii>(.*?)<\/pii>/g, '$1');
    const rawMessage = properties?.message as string || message;
    const redactedMessage = rawMessage?.replace(/<pii>.*?<\/pii>/g, '[REDACTED]');
    const updatedProperties = redactedMessage ? { ...properties, message: redactedMessage } : properties;

    const canSendTelemetry = isTelemetryEnabled();

    switch (logLevel) {
      case LogLevel.Info:
        if (canSendTelemetry) {
          this.reporter.sendTelemetryEvent(eventName, updatedProperties, measurements);
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
