import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import { buildEnvironmentPickItems, pickAccount, toEnvironmentPickItem } from '../../services/accountEnvPicker';
import { EnvironmentInfo } from '../../types';

const makeEnv = (environmentId: string, displayName: string, environmentSku: string): EnvironmentInfo => ({
	environmentId,
	displayName,
	dataverseUrl: `https://${environmentId}.crm.dynamics.com`,
	agentManagementUrl: `https://${environmentId}.api`,
	environmentSku,
});

describe('toEnvironmentPickItem', () => {
	test('maps environment fields and carries the source account', () => {
		const item = toEnvironmentPickItem(makeEnv('e1', 'Env One', 'Developer'), { accountId: 'a', accountEmail: 'a@b' });
		assert.strictEqual(item.label, 'Env One');
		assert.strictEqual(item.description, 'e1');
		assert.strictEqual(item.environment.environmentId, 'e1');
		assert.strictEqual(item.sourceAccount?.accountId, 'a');
	});
});

describe('buildEnvironmentPickItems', () => {
	test('inserts a separator per SKU and dedups by environmentId', () => {
		const items = buildEnvironmentPickItems([
			makeEnv('e1', 'E1', 'Developer'),
			makeEnv('e1', 'E1 duplicate', 'Developer'),
			makeEnv('e2', 'E2', 'Production'),
		]);

		assert.deepStrictEqual(items.map(item => item.label), ['Developer', 'E1', 'Production', 'E2']);
		assert.strictEqual(items.filter(item => 'environment' in item).length, 2);
	});

	test('returns an empty list when there are no environments', () => {
		assert.deepStrictEqual(buildEnvironmentPickItems([]), []);
	});
});

describe('pickAccount', () => {
	test('returns undefined when there are no accounts', async () => {
		const result = await pickAccount('x', { listAccounts: async () => [] });
		assert.strictEqual(result, undefined);
	});

	test('returns the only account without prompting', async () => {
		let prompted = false;
		const result = await pickAccount('x', {
			listAccounts: async () => [{ accountId: 'a', accountEmail: 'a@b' }],
			showQuickPick: async () => { prompted = true; return undefined; },
		});

		assert.strictEqual(prompted, false);
		assert.notStrictEqual(result, 'cancelled');
		assert.strictEqual((result as { accountId: string }).accountId, 'a');
	});

	test('prompts and returns the chosen account when several are available', async () => {
		const result = await pickAccount('x', {
			listAccounts: async () => [{ accountId: 'a', accountEmail: 'a@b' }, { accountId: 'c', accountEmail: 'c@d' }],
			showQuickPick: async (items) => items[1],
		});

		assert.strictEqual((result as { accountId: string }).accountId, 'c');
	});

	test('returns "cancelled" when the picker is dismissed', async () => {
		const result = await pickAccount('x', {
			listAccounts: async () => [{ accountId: 'a', accountEmail: 'a@b' }, { accountId: 'c', accountEmail: 'c@d' }],
			showQuickPick: async () => undefined,
		});

		assert.strictEqual(result, 'cancelled');
	});
});
