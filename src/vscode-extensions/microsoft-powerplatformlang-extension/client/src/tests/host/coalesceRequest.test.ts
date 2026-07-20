import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import { coalesceRequest } from '../../utils/coalesceRequest';

const delay = (ms: number) => new Promise<void>(resolve => setTimeout(resolve, ms));

describe('coalesceRequest', () => {
	test('coalesces concurrent callers for the same key into a single start()', async () => {
		const inFlight = new Map<string, Promise<string>>();
		let starts = 0;
		const start = async () => { starts++; await delay(10); return 'ok'; };

		const [a, b] = await Promise.all([
			coalesceRequest(inFlight, 'k', start),
			coalesceRequest(inFlight, 'k', start),
		]);

		assert.strictEqual(starts, 1);
		assert.strictEqual(a, 'ok');
		assert.strictEqual(b, 'ok');
	});

	test('a caller cancelling does not poison the shared request for other callers', async () => {
		const inFlight = new Map<string, Promise<string>>();
		let starts = 0;
		const start = async () => { starts++; await delay(30); return 'agents'; };

		const cloneCancel = new AbortController();
		const cloneWait = coalesceRequest(inFlight, 'env', start, cloneCancel.signal);
		const treeWait = coalesceRequest(inFlight, 'env', start, undefined);

		cloneCancel.abort();

		await assert.rejects(cloneWait, (error: Error) => error.name === 'AbortError');
		assert.strictEqual(await treeWait, 'agents');
		assert.strictEqual(starts, 1);
	});

	test('an already-aborted caller rejects while the shared request still completes for others', async () => {
		const inFlight = new Map<string, Promise<string>>();
		let starts = 0;
		const start = async () => { starts++; await delay(10); return 'value'; };

		const aborted = new AbortController();
		aborted.abort();

		await assert.rejects(coalesceRequest(inFlight, 'k', start, aborted.signal), (error: Error) => error.name === 'AbortError');
		assert.strictEqual(await coalesceRequest(inFlight, 'k', start), 'value');
		assert.strictEqual(starts, 1);
	});

	test('clears the in-flight entry after completion so a later call starts fresh', async () => {
		const inFlight = new Map<string, Promise<number>>();
		let starts = 0;
		const start = async () => { starts++; return starts; };

		assert.strictEqual(await coalesceRequest(inFlight, 'k', start), 1);
		assert.strictEqual(await coalesceRequest(inFlight, 'k', start), 2);
	});

	test('a failing shared request rejects callers and clears the entry for a fresh retry', async () => {
		const inFlight = new Map<string, Promise<string>>();
		let starts = 0;
		const start = async () => { starts++; await delay(5); throw new Error('boom'); };

		await assert.rejects(coalesceRequest(inFlight, 'k', start), /boom/);
		assert.strictEqual(inFlight.has('k'), false);
		await assert.rejects(coalesceRequest(inFlight, 'k', start), /boom/);
		assert.strictEqual(starts, 2);
	});
});
