import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import {
	createSyncSuccessLog,
	getActiveSyncUri,
	getSyncStateFor,
	logWorkflowIssues,
	onAnySyncStateChanged,
	SyncState,
	withSyncCommandBusy,
} from '../../sync/workspaceSynchronizer';
import logger, { formatFileName, prepareLogData, sanitizeErrorDetails } from '../../services/logger';
import type { WorkflowResponse } from '../../types';

import { ThemeIcon } from 'vscode';
import { WorkspaceType, type CopilotStudioWorkspace } from '../../sync/localWorkspaces';

function createMockWorkspace(agentName: string): CopilotStudioWorkspace {
	return {
		workspaceUri: 'file:///c%3A/tmp',
		displayName: agentName,
		description: '',
		icon: new ThemeIcon('hubot'),
		type: WorkspaceType.Agent,
	};
}

describe('workspaceSynchronizer: sync success telemetry', () => {

	test('keeps the agent name visible while redacting it from telemetry', () => {
		const successLog = createSyncSuccessLog(createMockWorkspace('Contoso Support'), 'applying changes', 42);
		const prepared = prepareLogData(successLog.message, {
			sessionId: 'test-session',
			agentId: successLog.data.agentId,
			syncOperation: successLog.data.syncOperation,
		});

		assert.strictEqual(prepared.displayMessage, 'Completed applying changes for Contoso Support in 42ms');
		assert.strictEqual(prepared.telemetryProperties.message, 'Completed applying changes for [REDACTED AGENT NAME] in 42ms');
		assert.strictEqual(prepared.telemetryProperties.agent, '[REDACTED AGENT NAME]');
		assert.strictEqual(prepared.telemetryProperties.operation, 'applying changes');
	});

	test('does not allow PII values to close their redaction marker', () => {
		const agentName = 'Contoso </pii> alice@contoso.com';
		const successLog = createSyncSuccessLog(createMockWorkspace(agentName), 'applying changes', 42);
		const prepared = prepareLogData(successLog.message, {
			sessionId: 'test-session',
			agentId: successLog.data.agentId,
		});

		assert.strictEqual(prepared.displayMessage, `Completed applying changes for ${agentName} in 42ms`);
		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Completed applying changes for [REDACTED AGENT NAME] in 42ms',
		);
		assert.strictEqual(prepared.telemetryProperties.agent, '[REDACTED AGENT NAME]');
	});

	test('identifies an MCS YAML file name while preserving a safe file error', () => {
		const message = `Error opening file ${formatFileName('C:\\agents\\contoso\\agent.mcs.yml')}: ${sanitizeErrorDetails('Access denied')}`;
		const prepared = prepareLogData(message, {
			sessionId: 'test-session',
			message,
		});

		assert.strictEqual(
			prepared.displayMessage,
			'Error opening file C:\\agents\\contoso\\agent.mcs.yml: Access denied',
		);
		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Error opening file [REDACTED .MCS.YML FILE NAME]: Access denied',
		);
	});

	test('preserves filesystem error details for the user while redacting unsafe qualifiers', () => {
		const message = `Error opening file ${formatFileName('C:\\agents\\contoso\\agent.mcs.yml')}: ${sanitizeErrorDetails('Access denied by policy Contoso-Restricted')}`;
		const prepared = prepareLogData(message, { sessionId: 'test-session' });

		assert.strictEqual(
			prepared.displayMessage,
			'Error opening file C:\\agents\\contoso\\agent.mcs.yml: Access denied by policy Contoso-Restricted',
		);
		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Error opening file [REDACTED .MCS.YML FILE NAME]: Access denied by policy Contoso-Restricted',
		);
	});

	test('preserves a file error reason while redacting its agent and MCS YAML file', () => {
		const message = `Error opening file ${formatFileName('C:\\agents\\contoso\\agent.mcs.yml')}: ${sanitizeErrorDetails('Agent Contoso could not open C:\\agents\\contoso\\topic.mcs.yml', ['Contoso'])}`;
		const prepared = prepareLogData(message, { sessionId: 'test-session' });

		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Error opening file [REDACTED .MCS.YML FILE NAME]: Agent [REDACTED AGENT NAME] could not open [REDACTED .MCS.YML FILE NAME]',
		);
	});

	test('identifies an email address while preserving a safe rejection reason', () => {
		const message = `Re-authentication failed: ${sanitizeErrorDetails('alex@contoso.com was rejected')}`;
		const prepared = prepareLogData(message, { sessionId: 'test-session' });

		assert.strictEqual(prepared.displayMessage, 'Re-authentication failed: alex@contoso.com was rejected');
		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Re-authentication failed: [REDACTED EMAIL ADDRESS] was rejected',
		);
	});

	test('preserves a re-authentication reason while redacting its PII', () => {
		const message = `Re-authentication failed: ${sanitizeErrorDetails('Agent Contoso could not open C:\\agents\\contoso\\agent.mcs.yml for alex@contoso.com', ['Contoso'])}`;
		const prepared = prepareLogData(message, { sessionId: 'test-session' });

		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Re-authentication failed: Agent [REDACTED AGENT NAME] could not open [REDACTED .MCS.YML FILE NAME] for [REDACTED EMAIL ADDRESS]',
		);
	});

	test('sanitizes recognized PII while preserving the complete error reason', () => {
		const errorMessage = 'Agent Contoso Support could not open C:\\agents\\contoso\\topic.mcs.yaml for alex@contoso.com';
		const protectedMessage = sanitizeErrorDetails(errorMessage, ['Contoso Support']);
		const prepared = prepareLogData(protectedMessage, { sessionId: 'test-session' });

		assert.strictEqual(prepared.displayMessage, errorMessage);
		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Agent [REDACTED AGENT NAME] could not open [REDACTED .MCS.YAML FILE NAME] for [REDACTED EMAIL ADDRESS]',
		);
	});

	test('leaves non-PII error details unchanged', () => {
		const errorMessage = 'Request timed out after 30 seconds';
		const prepared = prepareLogData(sanitizeErrorDetails(errorMessage), { sessionId: 'test-session' });

		assert.strictEqual(prepared.displayMessage, errorMessage);
		assert.strictEqual(prepared.telemetryProperties.message, errorMessage);
	});

	test('redacts complete emails with apostrophes and Windows paths with spaces', () => {
		const errorMessage = "Could not open C:\\Users\\John Doe\\agent.mcs.yml for o'connor@example.com";
		const prepared = prepareLogData(sanitizeErrorDetails(errorMessage), { sessionId: 'test-session' });

		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Could not open [REDACTED .MCS.YML FILE NAME] for [REDACTED EMAIL ADDRESS]',
		);
	});

	test('redacts complete UNC MCS YAML paths with spaces', () => {
		const errorMessage = 'Could not open \\\\server\\John Doe\\agent.mcs.yml';
		const prepared = prepareLogData(sanitizeErrorDetails(errorMessage), { sessionId: 'test-session' });

		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Could not open [REDACTED .MCS.YML FILE NAME]',
		);
	});

	test('redacts full URLs as a URL rather than mislabeling them as file names', () => {
		const errorMessage = 'Cannot reach https://contoso.crm.dynamics.com/api endpoint';
		const prepared = prepareLogData(sanitizeErrorDetails(errorMessage), { sessionId: 'test-session' });

		assert.strictEqual(prepared.displayMessage, errorMessage);
		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Cannot reach [REDACTED URL] endpoint',
		);
	});

	test('redacts non-MCS file paths while disclosing the file extension', () => {
		const errorMessage = 'ENOENT: spawn C:\\Users\\alex\\.vscode\\extensions\\lspOut\\LanguageServerHost.exe';
		const prepared = prepareLogData(sanitizeErrorDetails(errorMessage), { sessionId: 'test-session' });

		assert.strictEqual(prepared.displayMessage, errorMessage);
		assert.strictEqual(
			prepared.telemetryProperties.message,
			'ENOENT: spawn [REDACTED .EXE FILE NAME]',
		);
	});

	test('redacts cached botdefinition paths disclosing the .json extension', () => {
		const errorMessage = 'Failed reading C:\\Users\\alex\\agents\\contoso\\.mcs\\botdefinition.json';
		const prepared = prepareLogData(sanitizeErrorDetails(errorMessage), { sessionId: 'test-session' });

		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Failed reading [REDACTED .JSON FILE NAME]',
		);
	});

	test('redacts bare file names disclosing the file extension', () => {
		const errorMessage = 'Could not parse settings.mcs.yml';
		const prepared = prepareLogData(sanitizeErrorDetails(errorMessage), { sessionId: 'test-session' });

		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Could not parse [REDACTED .MCS.YML FILE NAME]',
		);
	});

	test('uses original-string indices for Unicode agent-name matching', () => {
		const errorMessage = 'İssue for Contoso failed';
		const prepared = prepareLogData(sanitizeErrorDetails(errorMessage, ['Contoso']), {
			sessionId: 'test-session',
		});

		assert.strictEqual(
			prepared.telemetryProperties.message,
			'İssue for [REDACTED AGENT NAME] failed',
		);
	});

	test('does not redact short agent names inside words or status clauses as agent names', () => {
		const errorMessage = 'Validation failed. Agent failed to start.';
		const prepared = prepareLogData(sanitizeErrorDetails(errorMessage, ['AI']), {
			sessionId: 'test-session',
		});

		assert.strictEqual(prepared.telemetryProperties.message, errorMessage);
	});

	test('redacts multiline sensitive values without changing display text', () => {
		const message = 'Sync failed: <pii>Agent Contoso\nC:\\agents\\contoso</pii>';
		const prepared = prepareLogData(message, {
			sessionId: 'test-session',
			error: '<pii>Agent Contoso\nC:\\agents\\contoso</pii>',
		});

		assert.strictEqual(prepared.displayMessage, 'Sync failed: Agent Contoso\nC:\\agents\\contoso');
		assert.strictEqual(prepared.telemetryProperties.message, 'Sync failed: [REDACTED]');
		assert.strictEqual(prepared.telemetryProperties.error, '[REDACTED]');
	});
});

/**
 * Tests for the command-level busy tracking exposed by workspaceSynchronizer.
 *
 * Covers the "isSyncing" model that drives the `mcs.isSyncing` context key
 * and the per-workspace spinner / auto-expand behavior in the Agent Changes
 * tree view.
 */
describe('workspaceSynchronizer: withSyncCommandBusy', () => {

	test('getActiveSyncUri is undefined when no sync is running', () => {
		assert.strictEqual(getActiveSyncUri(), undefined);
	});

	test('getActiveSyncUri returns the workspace uri while body runs, undefined after', async () => {
		const uri = 'file:///test/agent-a';
		assert.strictEqual(getActiveSyncUri(), undefined);

		let observedDuringBody: string | undefined;
		await withSyncCommandBusy(uri, async () => {
			observedDuringBody = getActiveSyncUri();
		});

		assert.strictEqual(observedDuringBody, uri);
		assert.strictEqual(getActiveSyncUri(), undefined);
	});

	test('getActiveSyncUri is cleared when body throws', async () => {
		const uri = 'file:///test/agent-throws';
		await assert.rejects(
			withSyncCommandBusy(uri, async () => {
				assert.strictEqual(getActiveSyncUri(), uri);
				throw new Error('boom');
			}),
			/boom/,
		);
		assert.strictEqual(getActiveSyncUri(), undefined);
	});

	test('withSyncCommandBusy returns the body result', async () => {
		const result = await withSyncCommandBusy('file:///test/agent-result', async () => 42);
		assert.strictEqual(result, 42);
	});

	test('onAnySyncStateChanged fires at start and end of withSyncCommandBusy', async () => {
		const events: (string | undefined)[] = [];
		const sub = onAnySyncStateChanged(() => events.push(getActiveSyncUri()));
		try {
			await withSyncCommandBusy('file:///test/agent-event', async () => { /* no-op */ });
		} finally {
			sub.dispose();
		}
		// Expect at least one "start" event with the uri set, then a "clear" event.
		assert.ok(events.includes('file:///test/agent-event'), `expected start event, got ${JSON.stringify(events)}`);
		assert.strictEqual(events[events.length - 1], undefined, 'last event should observe cleared state');
	});

	test('throws when re-entered while another sync is in progress', async () => {
		const outerUri = 'file:///test/agent-outer';
		const innerUri = 'file:///test/agent-inner';
		await assert.rejects(
			withSyncCommandBusy(outerUri, async () => {
				await withSyncCommandBusy(innerUri, async () => { /* unreachable */ });
			}),
			/sync is already in progress/i,
		);
		// Outer's finally still runs, clearing the state.
		assert.strictEqual(getActiveSyncUri(), undefined);
	});

	test('same-uri re-entry also throws (no implicit reuse)', async () => {
		const uri = 'file:///test/agent-same';
		await assert.rejects(
			withSyncCommandBusy(uri, async () => {
				await withSyncCommandBusy(uri, async () => { /* unreachable */ });
			}),
			/sync is already in progress/i,
		);
		assert.strictEqual(getActiveSyncUri(), undefined);
	});
});

describe('workspaceSynchronizer: getSyncStateFor', () => {

	test('returns Idle for an unknown workspace uri', () => {
		assert.strictEqual(getSyncStateFor('file:///nonexistent/workspace'), SyncState.Idle);
	});

	test('returns Idle for a workspace not currently syncing', () => {
		// withSyncCommandBusy alone does not create a per-workspace synchronizer
		// entry; that only happens via getOrAddSynchronizer. So a workspace that
		// has only been wrapped by withSyncCommandBusy should still report Idle.
		assert.strictEqual(getSyncStateFor('file:///test/agent-never-synced'), SyncState.Idle);
	});
});

describe('workspaceSynchronizer: SyncState enum', () => {

	test('SyncState.Idle is the default / zero value', () => {
		assert.strictEqual(SyncState.Idle, 0);
	});

	test('SyncState has distinct values for each operation', () => {
		const values = new Set([SyncState.Idle, SyncState.Fetching, SyncState.Pulling, SyncState.Pushing]);
		assert.strictEqual(values.size, 4, 'all SyncState values must be distinct');
	});
});

describe('workspaceSynchronizer: logWorkflowIssues', () => {

	function captureLogs(run: () => void): { warnings: string[]; errors: string[] } {
		const warnings: string[] = [];
		const errors: string[] = [];
		const originalWarn = logger.logWarning;
		const originalError = logger.logError;
		logger.logWarning = ((_event: unknown, message?: string) => { if (message) { warnings.push(message); } }) as typeof logger.logWarning;
		logger.logError = ((_event: unknown, message?: string) => { if (message) { errors.push(message); } }) as typeof logger.logError;
		try {
			run();
		} finally {
			logger.logWarning = originalWarn;
			logger.logError = originalError;
		}
		return { warnings, errors };
	}

	test('reports a failed workflow as an error even when a disabled workflow is present', () => {
		const workflows: WorkflowResponse[] = [
			{ workflowName: 'Draft WF', isDisabled: true },
			{ workflowName: 'Bad WF', isDisabled: true, errorMessage: 'Failed to update workflow: boom' },
		];

		let returnedHasErrors = false;
		const { warnings, errors } = captureLogs(() => { returnedHasErrors = logWorkflowIssues(workflows); });

		assert.strictEqual(returnedHasErrors, true, 'logWorkflowIssues must return true when a workflow error is present');
		assert.strictEqual(errors.length, 1, `expected one error log, got ${JSON.stringify(errors)}`);
		assert.ok(errors[0].includes('Bad WF: Failed to update workflow: boom'), errors[0]);
		assert.strictEqual(warnings.length, 1, `expected one warning log, got ${JSON.stringify(warnings)}`);
		assert.ok(warnings[0].includes('Draft WF'), warnings[0]);
	});

	test('a workflow that is only disabled with no error is reported as a warning, not an error', () => {
		const workflows: WorkflowResponse[] = [
			{ workflowName: 'Draft Only', isDisabled: true },
		];

		let returnedHasErrors = true;
		const { warnings, errors } = captureLogs(() => { returnedHasErrors = logWorkflowIssues(workflows); });

		assert.strictEqual(returnedHasErrors, false, 'logWorkflowIssues must return false when there are no workflow errors');
		assert.strictEqual(errors.length, 0, `expected no error log, got ${JSON.stringify(errors)}`);
		assert.strictEqual(warnings.length, 1);
		assert.ok(warnings[0].includes('Draft Only'));
	});

	test('suppressDisabledWarnings hides the disabled warning but still logs errors', () => {
		const workflows: WorkflowResponse[] = [
			{ workflowName: 'Draft WF', isDisabled: true },
			{ workflowName: 'Bad WF', isDisabled: true, errorMessage: 'Failed to update workflow: boom' },
		];

		let returnedHasErrors = false;
		const { warnings, errors } = captureLogs(() => { returnedHasErrors = logWorkflowIssues(workflows, true); });

		assert.strictEqual(returnedHasErrors, true, 'errors must still be reported (returned) when warnings are suppressed');
		assert.strictEqual(warnings.length, 0, `expected no warning when suppressed, got ${JSON.stringify(warnings)}`);
		assert.strictEqual(errors.length, 1, `errors must still log when warnings are suppressed, got ${JSON.stringify(errors)}`);
		assert.ok(errors[0].includes('Bad WF: Failed to update workflow: boom'), errors[0]);
	});
});
