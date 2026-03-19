import * as vscode from 'vscode';
import { restartLspClient } from './lspClient';

let isTelemetryEnabledCache = isTelemetryEnabled();

/**
 * Determines whether telemetry is enabled based on both the global VS Code telemetry setting
 * and the extension-specific telemetry setting. Telemetry is only considered enabled if both
 * settings are enabled. If the global VS Code telemetry setting is disabled, no telemetry data
 * will be sent, regardless of the extension's setting.
 *
 * @returns {boolean} True if telemetry is enabled in both VS Code and the extension; otherwise, false.
 */
export function isTelemetryEnabled(): boolean {
  const globalEnabled = vscode.env.isTelemetryEnabled === true;
  const extensionEnabled = vscode.workspace
    .getConfiguration('ms-CopilotStudio.telemetry')
    .get<boolean>('enabled', true);
  return globalEnabled && extensionEnabled;
}

/**
 * This function monitors both the global VS Code telemetry setting and the extension-specific telemetry setting.
 * When the telemetry setting changes, it restarts the LSP client to apply the new telemetry setting in the backend.
 * https://code.visualstudio.com/api/extension-guides/telemetry
 */
export function registerTelemetrySettingsListeners(context: vscode.ExtensionContext): void {
  const notifyIfChanged = async () => {
    const current = isTelemetryEnabled();
    if (current !== isTelemetryEnabledCache) {
      await restartLspClient();
      isTelemetryEnabledCache = current;
    }
  };

  // Global VS Code telemetry toggle
  const globalTelemetryToggle = vscode.env.onDidChangeTelemetryEnabled(() => {
    notifyIfChanged();
  });

  // Extension telemetry.enabled toggle
  const extensionTelemetryToggle = vscode.workspace.onDidChangeConfiguration(event => {
    if (event.affectsConfiguration('ms-CopilotStudio.telemetry.enabled')) {
      notifyIfChanged();
    }
  });

  context.subscriptions.push(globalTelemetryToggle, extensionTelemetryToggle);
}