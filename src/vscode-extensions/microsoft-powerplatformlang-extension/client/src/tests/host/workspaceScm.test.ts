import * as assert from 'node:assert';
import { describe, test } from 'node:test';

import { refreshInitialLocalChanges } from '../../sync/workspaceScm';

describe('workspaceScm: initial local changes', () => {
	test('keeps setup alive when the initial local diff fails', async () => {
		let attempts = 0;

		await refreshInitialLocalChanges(async () => {
			attempts++;
			throw new Error('invalid local file');
		});

		assert.strictEqual(attempts, 1);
	});
});
