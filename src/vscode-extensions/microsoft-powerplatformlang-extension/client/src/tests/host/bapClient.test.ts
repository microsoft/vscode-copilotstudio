import * as assert from 'node:assert';
import { describe, test } from 'node:test';

import { mergeEnvironmentsBySku } from '../../clients/bapClient';
import { EnvironmentInfo } from '../../types';

function makeEnv(environmentId: string, sku: string, displayName = environmentId): EnvironmentInfo {
	return {
		environmentId,
		displayName,
		environmentSku: sku,
		dataverseUrl: `https://${environmentId}.crm.dynamics.com/`,
		agentManagementUrl: `https://${environmentId}.api.powerplatform.com/`,
	};
}

describe('mergeEnvironmentsBySku', () => {
	test('keeps every environment across all per-SKU batches (union, in order)', () => {
		const dev = [makeEnv('dev-1', 'Developer'), makeEnv('dev-2', 'Developer')];
		const sandbox = [makeEnv('sandbox-1', 'Sandbox')];
		const production = [makeEnv('prod-1', 'Production')];

		const merged = mergeEnvironmentsBySku([dev, [], sandbox, production]);

		assert.deepStrictEqual(
			merged.map(e => e.environmentId),
			['dev-1', 'dev-2', 'sandbox-1', 'prod-1']
		);
	});

	test('de-duplicates by environmentId with the first occurrence winning', () => {
		const first = makeEnv('shared-1', 'Developer', 'From Developer batch');
		const duplicate = makeEnv('shared-1', 'Sandbox', 'From Sandbox batch');

		const merged = mergeEnvironmentsBySku([[first], [duplicate]]);

		assert.strictEqual(merged.length, 1);
		assert.strictEqual(merged[0], first, 'first occurrence should be retained');
		assert.strictEqual(merged[0].displayName, 'From Developer batch');
	});

	test('returns an empty list when there are no environments', () => {
		assert.deepStrictEqual(mergeEnvironmentsBySku([]), []);
	});

	test('ignores empty per-SKU batches', () => {
		assert.deepStrictEqual(mergeEnvironmentsBySku([[], [], []]), []);
	});

	test('preserves within-batch ordering', () => {
		const dev = [makeEnv('dev-b', 'Developer'), makeEnv('dev-a', 'Developer')];

		const merged = mergeEnvironmentsBySku([dev]);

		assert.deepStrictEqual(merged.map(e => e.environmentId), ['dev-b', 'dev-a']);
	});
});
