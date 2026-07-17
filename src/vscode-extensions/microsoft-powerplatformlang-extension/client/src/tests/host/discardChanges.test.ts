import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import * as vscode from 'vscode';

import {
	DiscardChangeInput,
	DiscardDependencies,
	discardLocalChanges,
	isTextRestorable,
} from '../../sync/discardChanges';
import { KnowledgeFileChangeKind } from '../../constants';
import { ChangeType } from '../../types';

const WS = vscode.Uri.parse('file:///tmp/ws/agent/');

function change(overrides: Partial<DiscardChangeInput> & { fileUri: vscode.Uri }): DiscardChangeInput {
	return {
		schemaName: 'test.schema',
		changeType: ChangeType.Update,
		changeKind: 'Topic',
		...overrides,
	};
}

interface Recorder {
	deps: DiscardDependencies;
	restored: { path: string; content: string }[];
	deleted: string[];
	cachedRequests: string[];
}

function makeDeps(options?: { cached?: string; getCachedContent?: (schemaName: string) => Promise<string>; deleteFile?: (uri: vscode.Uri) => Promise<void> }): Recorder {
	const restored: { path: string; content: string }[] = [];
	const deleted: string[] = [];
	const cachedRequests: string[] = [];
	const deps: DiscardDependencies = {
		getCachedContent: options?.getCachedContent ?? (async (schemaName: string) => {
			cachedRequests.push(schemaName);
			return options?.cached ?? 'cached-content';
		}),
		writeFile: async (uri: vscode.Uri, content: string) => {
			restored.push({ path: uri.path, content });
		},
		deleteFile: options?.deleteFile ?? (async (uri: vscode.Uri) => {
			deleted.push(uri.path);
		}),
	};
	return { deps, restored, deleted, cachedRequests };
}

describe('discardChanges: isTextRestorable', () => {
	test('text component extensions are restorable', () => {
		for (const ext of ['.mcs.yml', '.mcs.yaml', '.fx1']) {
			const c = change({ fileUri: vscode.Uri.joinPath(WS, `topics/a${ext}`) });
			assert.strictEqual(isTextRestorable(c), true, `${ext} should be restorable`);
		}
		const workflow = change({ fileUri: vscode.Uri.joinPath(WS, 'workflows/w/workflow.json') });
		assert.strictEqual(isTextRestorable(workflow), true, 'workflow.json should be restorable');
	});

	test('the agent icon is not restorable offline', () => {
		const c = change({ schemaName: 'icon', fileUri: vscode.Uri.joinPath(WS, 'icon.png') });
		assert.strictEqual(isTextRestorable(c), false);
	});

	test('knowledge file attachments are not restorable even with a text extension', () => {
		const c = change({
			changeKind: KnowledgeFileChangeKind,
			fileUri: vscode.Uri.joinPath(WS, 'knowledge/files/data.json'),
		});
		assert.strictEqual(isTextRestorable(c), false);
	});

	test('unknown binary extensions are not restorable', () => {
		const c = change({ fileUri: vscode.Uri.joinPath(WS, 'knowledge/files/manual.pdf') });
		assert.strictEqual(isTextRestorable(c), false);
	});
});

describe('discardChanges: discardLocalChanges', () => {
	test('Create deletes the file and does not read the cache', async () => {
		const { deps, restored, deleted, cachedRequests } = makeDeps();
		const input = change({ changeType: ChangeType.Create, fileUri: vscode.Uri.joinPath(WS, 'topics/new.topic.mcs.yml') });

		const result = await discardLocalChanges([input], deps);

		assert.strictEqual(result.deleted, 1);
		assert.strictEqual(result.restored, 0);
		assert.strictEqual(result.skipped.length, 0);
		assert.deepStrictEqual(deleted, ['/tmp/ws/agent/topics/new.topic.mcs.yml']);
		assert.strictEqual(restored.length, 0);
		assert.strictEqual(cachedRequests.length, 0);
	});

	test('Update rewrites the file from cached baseline content', async () => {
		const { deps, restored, cachedRequests } = makeDeps({ cached: 'baseline: true' });
		const input = change({ changeType: ChangeType.Update, schemaName: 'a.topic', fileUri: vscode.Uri.joinPath(WS, 'topics/a.topic.mcs.yml') });

		const result = await discardLocalChanges([input], deps);

		assert.strictEqual(result.restored, 1);
		assert.strictEqual(result.deleted, 0);
		assert.strictEqual(result.skipped.length, 0);
		assert.deepStrictEqual(cachedRequests, ['a.topic']);
		assert.deepStrictEqual(restored, [{ path: '/tmp/ws/agent/topics/a.topic.mcs.yml', content: 'baseline: true' }]);
	});

	test('Delete restores the file from cached baseline content', async () => {
		const { deps, restored } = makeDeps({ cached: 'restored: yes' });
		const input = change({ changeType: ChangeType.Delete, schemaName: 'gone', fileUri: vscode.Uri.joinPath(WS, 'topics/gone.topic.mcs.yml') });

		const result = await discardLocalChanges([input], deps);

		assert.strictEqual(result.restored, 1);
		assert.strictEqual(restored[0].content, 'restored: yes');
	});

	test('icon updates are skipped, not corrupted with text', async () => {
		const { deps, restored, cachedRequests } = makeDeps();
		const input = change({ changeType: ChangeType.Update, schemaName: 'icon', fileUri: vscode.Uri.joinPath(WS, 'icon.png') });

		const result = await discardLocalChanges([input], deps);

		assert.strictEqual(result.restored, 0);
		assert.strictEqual(result.skipped.length, 1);
		assert.strictEqual(result.skipped[0].path, 'icon.png');
		assert.strictEqual(restored.length, 0, 'must not write text into a binary file');
		assert.strictEqual(cachedRequests.length, 0, 'must not query the cache for non-restorable files');
	});

	test('a cached-content lookup failure is skipped, not fatal', async () => {
		const { deps } = makeDeps({
			getCachedContent: async () => { throw new Error('Cached file not found.'); },
		});
		const input = change({ changeType: ChangeType.Update, fileUri: vscode.Uri.joinPath(WS, 'topics/a.topic.mcs.yml') });

		const result = await discardLocalChanges([input], deps);

		assert.strictEqual(result.restored, 0);
		assert.strictEqual(result.skipped.length, 1);
		assert.match(result.skipped[0].reason, /not found/i);
	});

	test('one failure does not abort the rest of the batch', async () => {
		const { deps, restored, deleted } = makeDeps({
			cached: 'ok',
			getCachedContent: (() => {
				let n = 0;
				return async (schemaName: string) => {
					n++;
					if (n === 1) { throw new Error('boom'); }
					return `content-for-${schemaName}`;
				};
			})(),
		});
		const inputs: DiscardChangeInput[] = [
			change({ changeType: ChangeType.Update, schemaName: 'first', fileUri: vscode.Uri.joinPath(WS, 'topics/first.topic.mcs.yml') }),
			change({ changeType: ChangeType.Update, schemaName: 'second', fileUri: vscode.Uri.joinPath(WS, 'topics/second.topic.mcs.yml') }),
			change({ changeType: ChangeType.Create, fileUri: vscode.Uri.joinPath(WS, 'topics/third.topic.mcs.yml') }),
		];

		const result = await discardLocalChanges(inputs, deps);

		assert.strictEqual(result.skipped.length, 1, 'first update failed');
		assert.strictEqual(result.restored, 1, 'second update succeeded');
		assert.strictEqual(result.deleted, 1, 'third create was deleted');
		assert.strictEqual(restored.length, 1);
		assert.strictEqual(restored[0].content, 'content-for-second');
		assert.deepStrictEqual(deleted, ['/tmp/ws/agent/topics/third.topic.mcs.yml']);
	});

	test('an empty change set is a no-op', async () => {
		const { deps } = makeDeps();
		const result = await discardLocalChanges([], deps);
		assert.deepStrictEqual(result, { restored: 0, deleted: 0, skipped: [] });
	});
});
