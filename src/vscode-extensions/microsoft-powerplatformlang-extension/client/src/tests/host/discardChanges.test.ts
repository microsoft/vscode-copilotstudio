import * as assert from 'node:assert';
import { describe, test } from 'node:test';

import {
	formatDiscardResultMessage,
	getRemainingDiscardPaths,
	isDiscardComplete,
} from '../../commands/discardChanges';

describe('discardChanges: telemetry', () => {
	test('agent names are marked as PII while remaining visible to the user', () => {
		const message = formatDiscardResultMessage('Contoso Support', {
			restored: 1,
			deleted: 0,
			skipped: [],
		});

		assert.match(message, /<pii>Contoso Support<\/pii>/);
	});

	test('remaining local changes prevent a success outcome', () => {
		const result = {
			restored: 0,
			deleted: 1,
			skipped: [],
		};
		const remainingChanges = [{
			name: 'NewKb.txt',
			uri: 'capabilities/knowledge/files/NewKb.txt',
			changeType: 0,
			changeKind: 'FileAttachmentComponent',
			schemaName: 'agent.file.NewKb_txt',
		}];

		assert.strictEqual(isDiscardComplete(result, remainingChanges), false);
		assert.strictEqual(isDiscardComplete(result, []), true);
	});

	test('deduplicates skipped and returned changes by workspace-relative path', () => {
		const path = 'capabilities/knowledge/files/NewKb.txt';
		const result = {
			restored: 0,
			deleted: 0,
			skipped: [{ schemaName: 'agent.file.NewKb_txt', path, reason: 'use Get' }],
		};
		const remainingChanges = [{
			name: 'NewKb.txt',
			uri: path,
			changeType: 0,
			changeKind: 'FileAttachmentComponent',
			schemaName: 'agent.file.NewKb_txt',
		}];

		assert.deepStrictEqual(getRemainingDiscardPaths(result, remainingChanges), [path]);
	});
});
