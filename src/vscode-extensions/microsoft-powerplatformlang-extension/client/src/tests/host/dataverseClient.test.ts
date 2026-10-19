import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import { Uri } from 'vscode';

// Import the module to test
import { clearWhoAmICache, projectSharedAgents, whoAmIAsync } from '../../clients/dataverseClient';

describe('clearWhoAmICache', () => {
	/**
	 * A1: Verify clearWhoAmICache is callable and idempotent
	 * 
	 * This is a lightweight sanity check that the function exists and doesn't throw.
	 * Full cache behavior testing would require mocking the network layer.
	 */
	test('clearWhoAmICache can be called multiple times without error', () => {
		// Should not throw when called on empty caches
		assert.doesNotThrow(() => clearWhoAmICache());
		
		// Should be idempotent - safe to call multiple times
		assert.doesNotThrow(() => clearWhoAmICache());
		assert.doesNotThrow(() => clearWhoAmICache());
	});

	test('does not coalesce an interactive request with pending non-interactive validation', async () => {
		const account = require('../../clients/account') as typeof import('../../clients/account');
		const originalFetchAccessToken = account.FetchAccessToken;
		let releaseNonInteractive: (() => void) | undefined;
		let markNonInteractiveStarted: (() => void) | undefined;
		const nonInteractiveStarted = new Promise<void>(resolve => { markNonInteractiveStarted = resolve; });
		const nonInteractiveGate = new Promise<void>(resolve => { releaseNonInteractive = resolve; });
		const interactionModes: boolean[] = [];

		account.FetchAccessToken = async (...args: Parameters<typeof originalFetchAccessToken>) => {
			const interactive = args[6] ?? false;
			interactionModes.push(interactive);
			if (!interactive) {
				markNonInteractiveStarted?.();
				await nonInteractiveGate;
			}
			return {
				response: new Response(JSON.stringify({ UserId: interactive ? 'interactive-user' : 'background-user' }), { status: 200 }),
				tokenInfo: {
					accessToken: 'token',
					accountId: 'account',
					tenantId: 'tenant',
				},
			};
		};

		clearWhoAmICache();
		try {
			const endpoint = Uri.parse('https://contoso.crm.dynamics.com');
			const background = whoAmIAsync(endpoint, null, 'account', undefined, false);
			await nonInteractiveStarted;

			const interactive = await whoAmIAsync(endpoint, null, 'account', undefined, true);
			assert.strictEqual(interactive, 'interactive-user');
			assert.deepStrictEqual(interactionModes, [false, true]);

			releaseNonInteractive?.();
			assert.strictEqual(await background, 'background-user');
		} finally {
			releaseNonInteractive?.();
			account.FetchAccessToken = originalFetchAccessToken;
			clearWhoAmICache();
		}
	});
});

describe('projectSharedAgents', () => {
	// This fixture is grounded in a real captured trace, not merely hand-constructed.
	// Against a live Sandbox environment, an Environment Admin who had full read+write
	// access to three agents owned by ANOTHER user saw none of them in the extension:
	// the old write-access gate issued an invalid (unbound) RetrievePrincipalAccess call
	// that returned HTTP 404 for every agent (and no per-part Content-ID), so all three
	// were filtered out. The three entries below stand in for those captured non-owned
	// agents; the ids are anonymized placeholders (no real records or secrets are stored).
	const nonOwnedBots = [
		{ botid: '00000000-0000-0000-0000-000000000001', name: 'Shared Agent A', schemaname: 'cr00_sharedagenta', iconbase64: '', bot_botcomponentcollection: [] },
		{ botid: '00000000-0000-0000-0000-000000000002', name: 'Shared Agent B', schemaname: 'cr00_sharedagentb', iconbase64: '', bot_botcomponentcollection: [] },
		{ botid: '00000000-0000-0000-0000-000000000003', name: 'Shared Agent C', schemaname: 'cr00_sharedagentc', iconbase64: '', bot_botcomponentcollection: [] },
	];

	test('returns every non-owned agent (no access-based filtering)', () => {
		// Regression guard: a broken RetrievePrincipalAccess "write access" gate used to drop
		// every non-owned agent, so environment admins saw only the agents they personally
		// owned. Shared listing must surface ALL non-owned agents the query returns.
		const result = projectSharedAgents(nonOwnedBots);

		assert.strictEqual(result.length, nonOwnedBots.length);
		assert.deepStrictEqual(result.map(a => a.agentId), nonOwnedBots.map(b => b.botid));
		assert.deepStrictEqual(result.map(a => a.displayName), nonOwnedBots.map(b => b.name));
	});

	test('marks projected agents as shared', () => {
		const result = projectSharedAgents(nonOwnedBots);

		assert.ok(result.every(a => a.displayComplement === ' (shared)'));
	});

	test('returns an empty list when there are no non-owned agents', () => {
		assert.deepStrictEqual(projectSharedAgents([]), []);
	});
});
