import * as assert from 'node:assert';
import { describe, test } from 'node:test';

import { formatDiscardErrorMessage, formatDiscardResultMessage } from '../../commands/discardChanges';

describe('discardChanges: telemetry', () => {
	test('agent names are marked as PII while remaining visible to the user', () => {
		const message = formatDiscardResultMessage('Contoso Support', {
			restored: 1,
			deleted: 0,
			skipped: [],
		});

		assert.match(message, /<pii>Contoso Support<\/pii>/);
	});

	test('non-Error rejections retain their message and PII protection', () => {
		const message = formatDiscardErrorMessage('request rejected');

		assert.match(message, /<pii>request rejected<\/pii>/);
	});
});
