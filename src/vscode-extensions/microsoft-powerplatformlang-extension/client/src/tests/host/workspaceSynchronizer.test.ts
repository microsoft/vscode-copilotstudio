import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import {
	getActiveSyncUri,
	getSyncStateFor,
	onAnySyncStateChanged,
	SyncState,
	withSyncCommandBusy,
} from '../../sync/workspaceSynchronizer';

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
