// TDD probe for issue #199: sync progress indicator + button gating.
// These tests pin down the pure helpers the implementation will rely on:
//   - getChangeGroupVisuals(groupType, syncState) returns an icon id, a contextValue,
//     and an isSyncing flag matching the running operation for that group.
//   - isAnySyncInProgress(states) returns true iff any synchronizer is non-idle,
//     used to drive the `mcs.syncInProgress` context key that hides title-bar buttons.
//
// Today these helpers are not exported. Every test in this suite is expected to fail
// until the implementation phase adds them.

import * as assert from 'assert';
import * as treeModule from '../../sync/agentChangesTreeProvider';
import { SyncState } from '../../sync/workspaceSynchronizer';

type Visuals = {
	iconId: string;
	contextValue: string;
	isSyncing: boolean;
};

function getHelpers(): {
	getChangeGroupVisuals?: (groupType: 'local' | 'remote', state: SyncState) => Visuals;
	isAnySyncInProgress?: (states: SyncState[]) => boolean;
} {
	return treeModule as unknown as {
		getChangeGroupVisuals?: (groupType: 'local' | 'remote', state: SyncState) => Visuals;
		isAnySyncInProgress?: (states: SyncState[]) => boolean;
	};
}

suite('Issue #199 probe: getChangeGroupVisuals export', () => {
	test('agentChangesTreeProvider exports getChangeGroupVisuals(groupType, syncState)', () => {
		const helpers = getHelpers();
		assert.strictEqual(
			typeof helpers.getChangeGroupVisuals,
			'function',
			'Expected getChangeGroupVisuals to be exported from sync/agentChangesTreeProvider'
		);
	});
});

suite('Issue #199 probe: getChangeGroupVisuals — idle states', () => {
	test('Idle / remote → cloud icon, base contextValue, isSyncing=false', () => {
		const fn = getHelpers().getChangeGroupVisuals;
		assert.ok(fn, 'getChangeGroupVisuals not exported yet');
		const v = fn('remote', SyncState.Idle);
		assert.strictEqual(v.iconId, 'cloud');
		assert.strictEqual(v.contextValue, 'changeGroup-remote');
		assert.strictEqual(v.isSyncing, false);
	});

	test('Idle / local → file-code icon, base contextValue, isSyncing=false', () => {
		const fn = getHelpers().getChangeGroupVisuals;
		assert.ok(fn, 'getChangeGroupVisuals not exported yet');
		const v = fn('local', SyncState.Idle);
		assert.strictEqual(v.iconId, 'file-code');
		assert.strictEqual(v.contextValue, 'changeGroup-local');
		assert.strictEqual(v.isSyncing, false);
	});
});

suite('Issue #199 probe: getChangeGroupVisuals — sync states map to spinner', () => {
	// contextValue stays stable across sync states so inline action buttons remain
	// visible; the per-command `enablement: !mcs.syncInProgress` greys them out.
	test('Fetching / remote → spinner, contextValue stable, isSyncing=true', () => {
		const fn = getHelpers().getChangeGroupVisuals;
		assert.ok(fn, 'getChangeGroupVisuals not exported yet');
		const v = fn('remote', SyncState.Fetching);
		assert.strictEqual(v.iconId, 'sync~spin');
		assert.strictEqual(v.contextValue, 'changeGroup-remote');
		assert.strictEqual(v.isSyncing, true);
	});

	test('Pulling / remote → spinner, contextValue stable, isSyncing=true', () => {
		const fn = getHelpers().getChangeGroupVisuals;
		assert.ok(fn, 'getChangeGroupVisuals not exported yet');
		const v = fn('remote', SyncState.Pulling);
		assert.strictEqual(v.iconId, 'sync~spin');
		assert.strictEqual(v.contextValue, 'changeGroup-remote');
		assert.strictEqual(v.isSyncing, true);
	});

	test('Pushing / local → spinner, contextValue stable, isSyncing=true', () => {
		const fn = getHelpers().getChangeGroupVisuals;
		assert.ok(fn, 'getChangeGroupVisuals not exported yet');
		const v = fn('local', SyncState.Pushing);
		assert.strictEqual(v.iconId, 'sync~spin');
		assert.strictEqual(v.contextValue, 'changeGroup-local');
		assert.strictEqual(v.isSyncing, true);
	});
});

suite('Issue #199 probe: getChangeGroupVisuals — operation/group isolation', () => {
	test('Pushing / remote → cloud icon (push only affects local group)', () => {
		const fn = getHelpers().getChangeGroupVisuals;
		assert.ok(fn, 'getChangeGroupVisuals not exported yet');
		const v = fn('remote', SyncState.Pushing);
		assert.strictEqual(v.iconId, 'cloud');
		assert.strictEqual(v.contextValue, 'changeGroup-remote');
		assert.strictEqual(v.isSyncing, false);
	});

	test('Fetching / local → file-code icon (fetch only affects remote group)', () => {
		const fn = getHelpers().getChangeGroupVisuals;
		assert.ok(fn, 'getChangeGroupVisuals not exported yet');
		const v = fn('local', SyncState.Fetching);
		assert.strictEqual(v.iconId, 'file-code');
		assert.strictEqual(v.contextValue, 'changeGroup-local');
		assert.strictEqual(v.isSyncing, false);
	});

	test('Pulling / local → file-code icon (pull only affects remote group)', () => {
		const fn = getHelpers().getChangeGroupVisuals;
		assert.ok(fn, 'getChangeGroupVisuals not exported yet');
		const v = fn('local', SyncState.Pulling);
		assert.strictEqual(v.iconId, 'file-code');
		assert.strictEqual(v.contextValue, 'changeGroup-local');
		assert.strictEqual(v.isSyncing, false);
	});
});

suite('Issue #199 probe: isAnySyncInProgress', () => {
	test('agentChangesTreeProvider exports isAnySyncInProgress(states)', () => {
		const helpers = getHelpers();
		assert.strictEqual(
			typeof helpers.isAnySyncInProgress,
			'function',
			'Expected isAnySyncInProgress to be exported for the global mcs.syncInProgress context key'
		);
	});

	test('Empty list → false', () => {
		const fn = getHelpers().isAnySyncInProgress;
		assert.ok(fn, 'isAnySyncInProgress not exported yet');
		assert.strictEqual(fn([]), false);
	});

	test('All idle → false', () => {
		const fn = getHelpers().isAnySyncInProgress;
		assert.ok(fn, 'isAnySyncInProgress not exported yet');
		assert.strictEqual(fn([SyncState.Idle, SyncState.Idle]), false);
	});

	test('Any non-idle → true', () => {
		const fn = getHelpers().isAnySyncInProgress;
		assert.ok(fn, 'isAnySyncInProgress not exported yet');
		assert.strictEqual(fn([SyncState.Idle, SyncState.Fetching]), true);
		assert.strictEqual(fn([SyncState.Pulling]), true);
		assert.strictEqual(fn([SyncState.Pushing, SyncState.Idle]), true);
	});
});
