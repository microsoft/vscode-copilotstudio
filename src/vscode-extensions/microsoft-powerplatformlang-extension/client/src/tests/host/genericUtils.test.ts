import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import { isChildUri } from '../../utils/genericUtils';

describe('isChildUri', () => {
	test('returns true when the URIs are equal', () => {
		assert.strictEqual(isChildUri('file:///repo/Agent', 'file:///repo/Agent'), true);
	});

	test('returns true for a descendant separated by a path boundary', () => {
		assert.strictEqual(isChildUri('file:///repo/Agent/topics/Greeting.mcs.yml', 'file:///repo/Agent'), true);
	});

	test('returns true when the parent already ends with a separator', () => {
		assert.strictEqual(isChildUri('file:///repo/Agent/file.yml', 'file:///repo/Agent/'), true);
	});

	test('does not match a sibling folder that shares a name prefix', () => {
		assert.strictEqual(isChildUri('file:///repo/Agent2/file.yml', 'file:///repo/Agent'), false);
		assert.strictEqual(isChildUri('file:///repo/Agent2', 'file:///repo/Agent'), false);
	});

	test('matches case-insensitively', () => {
		assert.strictEqual(isChildUri('file:///repo/AGENT/file.yml', 'file:///repo/agent'), true);
	});
});
