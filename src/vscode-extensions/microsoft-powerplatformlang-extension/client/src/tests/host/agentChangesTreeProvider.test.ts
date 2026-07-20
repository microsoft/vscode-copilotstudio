import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import * as vscode from 'vscode';
import {
	AgentChangesItemKind,
	ChangeGroupTreeItem,
	AgentChangesTreeItemUnion,
	isWorkspaceConnected,
	describeDisconnection,
} from '../../sync/agentChangesTreeProvider';
import { Resource } from '../../sync/changeTracking';
import { ChangeType } from '../../types';
import { SyncState } from '../../sync/workspaceSynchronizer';

/**
 * Type guard tests for AgentChangesTreeItemUnion discriminated union.
 * Ensures TypeScript can properly narrow the tree item types.
 */
describe('AgentChangesTreeProvider Type Guards', () => {

	test('AgentTreeItem has kind === Agent', () => {
		const item: AgentChangesTreeItemUnion = {
			kind: AgentChangesItemKind.Agent,
			workspace: {
				workspaceUri: 'file:///test',
				displayName: 'Test Agent',
			} as any, // Simplified for test
		};

		assert.strictEqual(item.kind, AgentChangesItemKind.Agent);
		if (item.kind === AgentChangesItemKind.Agent) {
			// TypeScript should narrow to AgentTreeItem
			assert.strictEqual(item.workspace.displayName, 'Test Agent');
		}
	});

	test('ChangeGroupTreeItem has kind === ChangeGroup', () => {
		const item: AgentChangesTreeItemUnion = {
			kind: AgentChangesItemKind.ChangeGroup,
			workspace: { workspaceUri: 'file:///test' } as any,
			groupType: 'local',
			label: 'Local Changes',
		};

		assert.strictEqual(item.kind, AgentChangesItemKind.ChangeGroup);
		if (item.kind === AgentChangesItemKind.ChangeGroup) {
			// TypeScript should narrow to ChangeGroupTreeItem
			assert.strictEqual(item.groupType, 'local');
			assert.strictEqual(item.label, 'Local Changes');
		}
	});

	test('ChangeItemTreeItem has kind === ChangeItem', () => {
		const mockResource = {
			resourceUri: vscode.Uri.parse('file:///test/topic.mcs.yaml'),
			type: ChangeType.Update,
		} as Resource;

		const item: AgentChangesTreeItemUnion = {
			kind: AgentChangesItemKind.ChangeItem,
			workspace: { workspaceUri: 'file:///test' } as any,
			resource: mockResource,
			groupType: 'local',
		};

		assert.strictEqual(item.kind, AgentChangesItemKind.ChangeItem);
		if (item.kind === AgentChangesItemKind.ChangeItem) {
			// TypeScript should narrow to ChangeItemTreeItem
			assert.strictEqual(item.resource.type, ChangeType.Update);
			assert.strictEqual(item.groupType, 'local');
		}
	});
});

describe('AgentChangesTreeProvider Item Hierarchy', () => {

	test('Agent node should be at root level (kind=1)', () => {
		assert.strictEqual(AgentChangesItemKind.Agent, 1);
	});

	test('ChangeGroup node should be at second level (kind=2)', () => {
		assert.strictEqual(AgentChangesItemKind.ChangeGroup, 2);
	});

	test('ChangeItem node should be at third level (kind=3)', () => {
		assert.strictEqual(AgentChangesItemKind.ChangeItem, 3);
	});

	test('groupType discriminates local vs remote changes', () => {
		const localGroup: ChangeGroupTreeItem = {
			kind: AgentChangesItemKind.ChangeGroup,
			workspace: { workspaceUri: 'file:///test' } as any,
			groupType: 'local',
			label: 'Local Changes',
		};

		const remoteGroup: ChangeGroupTreeItem = {
			kind: AgentChangesItemKind.ChangeGroup,
			workspace: { workspaceUri: 'file:///test' } as any,
			groupType: 'remote',
			label: 'Remote Changes',
		};

		assert.strictEqual(localGroup.groupType, 'local');
		assert.strictEqual(remoteGroup.groupType, 'remote');
		assert.notStrictEqual(localGroup.groupType, remoteGroup.groupType);
	});
});

describe('Change Type Icon Mapping', () => {
	// These tests verify the change type enum values used for icon selection

	test('ChangeType.Create should map to added icon', () => {
		assert.strictEqual(ChangeType.Create, 0);
	});

	test('ChangeType.Update should map to modified icon', () => {
		assert.strictEqual(ChangeType.Update, 1);
	});

	test('ChangeType.Delete should map to removed icon', () => {
		assert.strictEqual(ChangeType.Delete, 2);
	});
});

describe('Sync Spinner Group Mapping', () => {
	// Mirrors the per-state spinner mapping in
	// AgentChangesTreeDataProvider.getTreeItem for the ChangeGroup case:
	//   Fetching / Pulling -> spinner on Remote group
	//   Pushing            -> spinner on Local group
	//   Idle               -> no spinner anywhere
	//
	// Keep this helper in lock-step with the production code.
	type GroupType = 'local' | 'remote';
	const isBusy = (groupType: GroupType, syncState: SyncState): boolean =>
		(groupType === 'remote' && (syncState === SyncState.Fetching || syncState === SyncState.Pulling)) ||
		(groupType === 'local' && syncState === SyncState.Pushing);

	test('Idle: neither group spins', () => {
		assert.strictEqual(isBusy('local', SyncState.Idle), false);
		assert.strictEqual(isBusy('remote', SyncState.Idle), false);
	});

	test('Fetching: only remote group spins', () => {
		assert.strictEqual(isBusy('remote', SyncState.Fetching), true);
		assert.strictEqual(isBusy('local', SyncState.Fetching), false);
	});

	test('Pulling: only remote group spins', () => {
		assert.strictEqual(isBusy('remote', SyncState.Pulling), true);
		assert.strictEqual(isBusy('local', SyncState.Pulling), false);
	});

	test('Pushing: only local group spins', () => {
		assert.strictEqual(isBusy('local', SyncState.Pushing), true);
		assert.strictEqual(isBusy('remote', SyncState.Pushing), false);
	});

	test('Each non-Idle state activates exactly one group', () => {
		for (const state of [SyncState.Fetching, SyncState.Pulling, SyncState.Pushing]) {
			const spinning = (['local', 'remote'] as GroupType[]).filter(g => isBusy(g, state));
			assert.strictEqual(spinning.length, 1, `expected one spinning group for state ${SyncState[state]}, got ${spinning.length}`);
		}
	});
});

describe('Disconnected Agent Presentation', () => {
	const makeWorkspace = (over: Partial<{ workspaceUri: string; syncInfo: unknown }>): any => ({
		workspaceUri: 'file:///nonexistent-agent-for-tests',
		displayName: 'Test Agent',
		description: '',
		type: 0,
		...over,
	});

	test('isWorkspaceConnected is false without syncInfo', () => {
		assert.strictEqual(isWorkspaceConnected(makeWorkspace({ syncInfo: undefined })), false);
	});

	test('isWorkspaceConnected is false without an agentManagementEndpoint', () => {
		assert.strictEqual(isWorkspaceConnected(makeWorkspace({ syncInfo: { agentManagementEndpoint: undefined } })), false);
	});

	test('describeDisconnection asks to reattach when no connection file exists', () => {
		const status = describeDisconnection(makeWorkspace({ syncInfo: undefined }));
		assert.strictEqual(status.action, 'reattach');
		assert.ok(status.message.includes('Not linked to a cloud agent'));
	});
});
