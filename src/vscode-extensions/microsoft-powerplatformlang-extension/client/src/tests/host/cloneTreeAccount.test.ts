import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import { AgentTreeDataProvider, TreeItemKind } from '../../clone/tree';

describe('AgentTreeDataProvider sign-in state', () => {
	test('shows the sign-in item when no account is signed in', async () => {
		const provider = new AgentTreeDataProvider({ isSignedIn: async () => false, listAccounts: async () => [] });
		const children = await provider.getChildren();
		assert.strictEqual(children.length, 1);
		assert.strictEqual(children[0].kind, TreeItemKind.SignIn);
	});

	test('shows the sign-in item when signed in but no stored accounts', async () => {
		const provider = new AgentTreeDataProvider({ isSignedIn: async () => true, listAccounts: async () => [] });
		const children = await provider.getChildren();
		assert.strictEqual(children.length, 1);
		assert.strictEqual(children[0].kind, TreeItemKind.SignIn);
	});
});
