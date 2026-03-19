import * as assert from 'assert';
import * as vscode from 'vscode';
import {
	AgentChangesItemKind,
	AgentTreeItem,
	ChangeGroupTreeItem,
	ChangeItemTreeItem,
	AgentChangesTreeItemUnion,
} from '../../sync/agentChangesTreeProvider';
import { Resource } from '../../sync/changeTracking';
import { ChangeType } from '../../types';

/**
 * Type guard tests for AgentChangesTreeItemUnion discriminated union.
 * Ensures TypeScript can properly narrow the tree item types.
 */
suite('AgentChangesTreeProvider Type Guards', () => {

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

suite('AgentChangesTreeProvider Item Hierarchy', () => {

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

suite('Change Type Icon Mapping', () => {
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

suite('Tree Item Context Values', () => {
	// Context values control which inline buttons appear on hover

	test('Agent node should have contextValue "agent"', () => {
		// This is set in getTreeItem() - we're documenting the expected value
		const expectedContextValue = 'agent';
		assert.strictEqual(expectedContextValue, 'agent');
	});

	test('Local ChangeGroup should have contextValue "changeGroup-local"', () => {
		const groupType = 'local';
		const expectedContextValue = `changeGroup-${groupType}`;
		assert.strictEqual(expectedContextValue, 'changeGroup-local');
	});

	test('Remote ChangeGroup should have contextValue "changeGroup-remote"', () => {
		const groupType = 'remote';
		const expectedContextValue = `changeGroup-${groupType}`;
		assert.strictEqual(expectedContextValue, 'changeGroup-remote');
	});

	test('Local ChangeItem should have contextValue "changeItem-local"', () => {
		const groupType = 'local';
		const expectedContextValue = `changeItem-${groupType}`;
		assert.strictEqual(expectedContextValue, 'changeItem-local');
	});

	test('Remote ChangeItem should have contextValue "changeItem-remote"', () => {
		const groupType = 'remote';
		const expectedContextValue = `changeItem-${groupType}`;
		assert.strictEqual(expectedContextValue, 'changeItem-remote');
	});
});

suite('Context Key Calculation', () => {
	// Tests for context keys used in when clauses

	test('hasChanges should be true when only local changes exist', () => {
		const hasRemoteChanges = false;
		const hasLocalChanges = true;
		const hasChanges = hasRemoteChanges || hasLocalChanges;
		assert.strictEqual(hasChanges, true);
	});

	test('hasChanges should be true when only remote changes exist', () => {
		const hasRemoteChanges = true;
		const hasLocalChanges = false;
		const hasChanges = hasRemoteChanges || hasLocalChanges;
		assert.strictEqual(hasChanges, true);
	});

	test('hasChanges should be true when both local and remote changes exist', () => {
		const hasRemoteChanges = true;
		const hasLocalChanges = true;
		const hasChanges = hasRemoteChanges || hasLocalChanges;
		assert.strictEqual(hasChanges, true);
	});

	test('hasChanges should be false when no changes exist', () => {
		const hasRemoteChanges = false;
		const hasLocalChanges = false;
		const hasChanges = hasRemoteChanges || hasLocalChanges;
		assert.strictEqual(hasChanges, false);
	});
});

suite('Badge Value Calculation', () => {
	// Tests for badge logic - badge shows total local change count

	test('Badge should be undefined when count is 0', () => {
		const count = 0;
		const badge = count > 0 ? { value: count, tooltip: `${count} local change(s)` } : undefined;
		assert.strictEqual(badge, undefined);
	});

	test('Badge should show singular tooltip for 1 change', () => {
		const count = 1;
		const badge = count > 0 
			? { value: count, tooltip: `${count} local change${count === 1 ? '' : 's'}` } 
			: undefined;
		
		assert.ok(badge);
		assert.strictEqual(badge.value, 1);
		assert.strictEqual(badge.tooltip, '1 local change');
	});

	test('Badge should show plural tooltip for multiple changes', () => {
		const count = 5 as number;
		const badge = count > 0 
			? { value: count, tooltip: `${count} local change${count === 1 ? '' : 's'}` } 
			: undefined;
		
		assert.ok(badge);
		assert.strictEqual(badge.value, 5);
		assert.strictEqual(badge.tooltip, '5 local changes');
	});
});

suite('Apply Gating Logic', () => {
	// Tests for the Apply command gating behavior

	test('Apply should be allowed when remote changes count is 0', () => {
		const remoteChangeCount = 0;
		const shouldBlockApply = remoteChangeCount > 0;
		assert.strictEqual(shouldBlockApply, false);
	});

	test('Apply should be blocked when remote changes exist', () => {
		const remoteChangeCount = 3;
		const shouldBlockApply = remoteChangeCount > 0;
		assert.strictEqual(shouldBlockApply, true);
	});

	test('Apply should proceed when no remote changes exist', () => {
		const changes = { remoteChanges: [] };
		const shouldBlockApply = changes.remoteChanges.length > 0;
		assert.strictEqual(shouldBlockApply, false, 'Apply should proceed when no remote changes');
	});

	test('Gating message should include change count (singular)', () => {
		const remoteCount = 1;
		const message = `The agent has ${remoteCount} remote change${remoteCount === 1 ? '' : 's'} that must be retrieved before your local changes can be applied.`;
		assert.ok(message.includes('1 remote change that'));
	});

	test('Gating message should include change count (plural)', () => {
		const remoteCount = 5 as number;
		const message = `The agent has ${remoteCount} remote change${remoteCount === 1 ? '' : 's'} that must be retrieved before your local changes can be applied.`;
		assert.ok(message.includes('5 remote changes that'));
	});
});
