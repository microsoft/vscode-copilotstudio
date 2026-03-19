import * as assert from 'assert';
import { isCopilotStudioTreeItem, TreeItemKind } from '../../clone/tree';

suite('isCopilotStudioTreeItem Type Guard', () => {
	test('returns false for null and undefined', () => {
		assert.strictEqual(isCopilotStudioTreeItem(null), false);
		assert.strictEqual(isCopilotStudioTreeItem(undefined), false);
	});

	test('returns false for non-object types', () => {
		assert.strictEqual(isCopilotStudioTreeItem('string'), false);
		assert.strictEqual(isCopilotStudioTreeItem(123), false);
		assert.strictEqual(isCopilotStudioTreeItem(true), false);
	});

	test('returns false for objects without kind property', () => {
		assert.strictEqual(isCopilotStudioTreeItem({}), false);
		assert.strictEqual(isCopilotStudioTreeItem({ agent: {}, environment: {} }), false);
	});

	test('returns false for objects with invalid kind', () => {
		assert.strictEqual(isCopilotStudioTreeItem({ kind: 'Agent' }), false);
		assert.strictEqual(isCopilotStudioTreeItem({ kind: 0 }), false);
		assert.strictEqual(isCopilotStudioTreeItem({ kind: 99 }), false);
	});

	test('returns true for all valid TreeItemKind values', () => {
		assert.strictEqual(isCopilotStudioTreeItem({ kind: TreeItemKind.SignIn }), true);
		assert.strictEqual(isCopilotStudioTreeItem({ kind: TreeItemKind.Environment }), true);
		assert.strictEqual(isCopilotStudioTreeItem({ kind: TreeItemKind.Agent }), true);
		assert.strictEqual(isCopilotStudioTreeItem({ kind: TreeItemKind.Error }), true);
		assert.strictEqual(isCopilotStudioTreeItem({ kind: TreeItemKind.SkuSection }), true);
	});
});

suite('Discriminated Union Narrowing', () => {
	test('TypeScript narrows AgentTreeItem when kind === Agent', () => {
		const item: unknown = {
			kind: TreeItemKind.Agent,
			agent: { agentId: 'x', displayName: 'Test' },
			environment: { environmentId: 'y', displayName: 'Env' }
		};
		
		if (isCopilotStudioTreeItem(item) && item.kind === TreeItemKind.Agent) {
			// TypeScript knows item is AgentTreeItem here - this compiles only if narrowing works
			assert.strictEqual(item.agent.agentId, 'x');
			assert.strictEqual(item.environment.environmentId, 'y');
		} else {
			assert.fail('Should have narrowed to AgentTreeItem');
		}
	});
});
