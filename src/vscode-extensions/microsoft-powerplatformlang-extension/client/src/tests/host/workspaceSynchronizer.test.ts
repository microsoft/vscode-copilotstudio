import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import {
	getActiveSyncUri,
	getSyncStateFor,
	logWorkflowIssues,
	onAnySyncStateChanged,
	SyncState,
	withSyncCommandBusy,
} from '../../sync/workspaceSynchronizer';
import logger from '../../services/logger';
import type { WorkflowResponse } from '../../types';

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
