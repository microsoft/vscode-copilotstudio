import * as assert from 'node:assert';
import { describe, test } from 'node:test';

import { refreshInitialLocalChanges, scheduleLocalChangeRefresh } from '../../sync/workspaceScm';

describe('workspaceScm: initial local changes', () => {
	test('keeps setup alive when the initial local diff fails', async () => {
		let attempts = 0;

		await refreshInitialLocalChanges(async () => {
			attempts++;
			throw new Error('invalid local file');
		});

		assert.strictEqual(attempts, 1);
	});

	test('coalesces a burst of workspace changes into the latest local refresh', async () => {
		const calls: string[] = [];

		scheduleLocalChangeRefresh('workspace', async () => {
			calls.push('first');
		}, 5);
		scheduleLocalChangeRefresh('workspace', async () => {
			calls.push('latest');
		}, 5);

		await new Promise(resolve => setTimeout(resolve, 25));

		assert.deepStrictEqual(calls, ['latest']);
	});

	test('never runs two local refreshes at once and finishes with the newest state', async () => {
		const events: string[] = [];
		let running = 0;

		const refresh = (name: string, durationMs: number) => async () => {
			running++;
			assert.strictEqual(running, 1, `${name} started while another refresh was in flight`);
			events.push(`start:${name}`);
			await new Promise(resolve => setTimeout(resolve, durationMs));
			events.push(`end:${name}`);
			running--;
		};

		scheduleLocalChangeRefresh('serialized', refresh('slow', 40), 1);
		await new Promise(resolve => setTimeout(resolve, 10));
		scheduleLocalChangeRefresh('serialized', refresh('fast', 1), 1);

		await new Promise(resolve => setTimeout(resolve, 120));

		assert.deepStrictEqual(events, ['start:slow', 'end:slow', 'start:fast', 'end:fast']);
	});

	test('collapses events that arrive during a refresh into a single trailing refresh', async () => {
		const events: string[] = [];
		let releaseFirst: () => void = () => { };
		const firstStarted = new Promise<void>(resolveStarted => {
			scheduleLocalChangeRefresh('trailing', async () => {
				events.push('start:running');
				resolveStarted();
				await new Promise<void>(resolve => { releaseFirst = resolve; });
				events.push('end:running');
			}, 1);
		});

		await firstStarted;

		scheduleLocalChangeRefresh('trailing', async () => {
			events.push('start:stale');
		}, 1);
		scheduleLocalChangeRefresh('trailing', async () => {
			events.push('start:newest');
		}, 1);

		await new Promise(resolve => setTimeout(resolve, 30));
		assert.deepStrictEqual(events, ['start:running']);

		releaseFirst();
		await new Promise(resolve => setTimeout(resolve, 30));

		assert.deepStrictEqual(events, ['start:running', 'end:running', 'start:newest']);
	});

	test('keeps serializing refreshes after one of them fails', async () => {
		const events: string[] = [];
		let failFirst: (reason: Error) => void = () => { };
		const firstStarted = new Promise<void>(resolveStarted => {
			scheduleLocalChangeRefresh('failing', async () => {
				events.push('start:first');
				resolveStarted();
				await new Promise<void>((_, reject) => { failFirst = reject; });
			}, 1);
		});

		await firstStarted;

		scheduleLocalChangeRefresh('failing', async () => {
			events.push('start:second');
		}, 1);

		await new Promise(resolve => setTimeout(resolve, 30));
		assert.deepStrictEqual(events, ['start:first']);

		failFirst(new Error('local diff failed'));
		await new Promise(resolve => setTimeout(resolve, 30));

		assert.deepStrictEqual(events, ['start:first', 'start:second']);
	});
});
