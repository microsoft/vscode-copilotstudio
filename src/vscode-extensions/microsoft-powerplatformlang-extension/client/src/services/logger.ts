import * as vscode from 'vscode';
import { TelemetryEventMeasurements, TelemetryEventProperties, TelemetryReporter } from "@vscode/extension-telemetry";
import { LogLevel, TELEMETRY_CONNECTION_STRING, type TelemetryEventType } from '../constants';
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
 * Service for sending telemetry events to Application Insights via the VS Code extension telemetry API.
 * 
 * Events sent using this service will appear in the "customEvents" table in Application Insights.
 *
 * It also displays messages in the VS Code UI based on the log level (Info, Warning, Error).
 *
 * @remarks
 * - Automatically attaches `sessionId` property to all events, `isError` property to error events, and `isWarning` property to warning events.
 * - Supports sending events with just a name, or with additional message and data.
 * - If the message contains Personally Identifiable Information (PII), wrap the PII content in `<pii>...</pii>` tags to ensure it is redacted in telemetry.
 */
class Logger {
  private static instance: Logger;
  private reporter!: TelemetryReporter;
  private sessionId!: string;

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
   * Sends a telemetry event with the given name, message, and data.
   * If message is provided, shows a message in the VS Code UI based on the log level.
   * PII in the message should be wrapped in `<pii>...</pii>` tags to ensure it is redacted in telemetry.
   *
   * @param logLevel - The level of the log (Info, Warning, Error).
   * @param eventName - The name of the telemetry event.
   * @param message - Optional message string to display to the user.
   * @param data - Optional telemetry data object (any custom metadata to send with the event).
   */
  public log(
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

    // Create a new properties object with the redacted message
    const updatedProperties = redactedMessage ? { ...properties, message: redactedMessage } : properties;

    const canSendTelemetry = isTelemetryEnabled();

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
