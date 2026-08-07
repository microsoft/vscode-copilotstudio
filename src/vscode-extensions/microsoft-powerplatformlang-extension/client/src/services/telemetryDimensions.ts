/**
 * Canonical telemetry dimension names used across the VS Code extension.
 * All names use camelCase to align with Application Insights conventions.
 * New dimensions MUST be added here before use in code.
 */

// ─────────────────────────────────────────────────────────────────────────────
// Shared dimensions (added automatically to every event by the logger)
// ─────────────────────────────────────────────────────────────────────────────

export const SharedDimensions = {
  /** Session correlation UUID shared between FE and BE. */
  sessionId: 'sessionId',

  /** VSIX version string. */
  version: 'version',

  /** "true" when running in dev/debug mode. Filter: where customDimensions.isDevMode != "true" */
  isDevMode: 'isDevMode',

  /** Event severity: "info" | "warning" | "error". */
  logLevel: 'logLevel',

  /** Human-readable display message. */
  message: 'message',

  /** Error message text (PII-wrapped for scrubbing). */
  errorMessage: 'errorMessage',

  /** Connected agent unique ID. */
  agentId: 'agentId',

  /** Dataverse environment unique ID. */
  environmentId: 'environmentId',

  /** Operation duration in milliseconds. */
  durationMs: 'durationMs',
} as const;

// ─────────────────────────────────────────────────────────────────────────────
// Event-specific dimensions (passed per call site via data parameter)
// ─────────────────────────────────────────────────────────────────────────────

export const EventDimensions = {
  /** User-facing sync command: Preview | Get | Apply. */
  syncOperation: 'syncOperation',

  /** Environment SKU (e.g., Developer, Trial, Sandbox). */
  sku: 'sku',

  /** Number of environments loaded. */
  environmentCount: 'environmentCount',

  /** Number of agents loaded. */
  agentCount: 'agentCount',

  /** LSP method name (e.g., powerplatformls::getWorkspaceDetails). Uses :: to avoid PII flagging. */
  lspMethod: 'lspMethod',
} as const;
