import * as strictAssert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import * as path from 'node:path';
import { createContext, runInContext } from 'node:vm';
import { HttpRequestError } from '../../utils/httpRequestError';
import { CoreServicesClusterCategory, DefaultCoreServicesClusterCategory } from '../../constants';
import * as assert from 'node:assert';
import { describe, test } from 'node:test';

import { mergeEnvironmentsBySku, toEnvironmentInfo, toEnvironmentEndpointCandidate, resolveEnvironmentForCloneAsync, EnvironmentDetails } from '../../clients/bapClient';
import { EnvironmentInfo } from '../../types';

const makeDetails = (properties: Record<string, unknown>): EnvironmentDetails => ({
	name: 'Default-a30263b9-1caf-4db5-ab53-ed3850c0bd1f',
	properties: {
		displayName: 'Contoso (default)',
		linkedEnvironmentMetadata: { instanceUrl: 'https://org82dd85c2.crm.dynamics.com/' },
		...properties,
	} as any,
});

describe('toEnvironmentInfo', () => {
	test('reads the Copilot Studio endpoint from the runtime endpoints', () => {
		const info = toEnvironmentInfo(makeDetails({
			runtimeEndpoints: { 'microsoft.PowerVirtualAgents': 'https://powervamg.us-il106.gateway.prod.island.powerapps.com/' },
		}));

		assert.strictEqual(info?.agentManagementUrl, 'https://powervamg.us-il106.gateway.prod.island.powerapps.com/');
		assert.strictEqual(info?.environmentId, 'Default-a30263b9-1caf-4db5-ab53-ed3850c0bd1f');
	});

	test('rejects an environment with no runtime endpoints so it never reaches the LSP', () => {
		assert.strictEqual(toEnvironmentInfo(makeDetails({})), null);
	});

	test('rejects an environment where Copilot Studio is not enabled', () => {
		assert.strictEqual(
			toEnvironmentInfo(makeDetails({ runtimeEndpoints: { 'microsoft.PowerApps': 'https://api.powerapps.com/' } })),
			null);
	});

	test('returns null when the environment has no linked Dataverse instance', () => {
		assert.strictEqual(toEnvironmentInfo({ name: 'env', properties: { displayName: 'No Dataverse' } as any }), null);
	});

	test('every environment it returns carries an endpoint the LSP can build a Uri from', () => {
		const info = toEnvironmentInfo(makeDetails({
			runtimeEndpoints: { 'microsoft.PowerVirtualAgents': 'https://powervamg.us-il106.gateway.prod.island.powerapps.com/' },
		}));

		assert.ok(info);
		assert.doesNotThrow(() => new URL(info!.agentManagementUrl));
	});
});

describe('toEnvironmentEndpointCandidate', () => {
	test('keeps an endpointless environment so repair can still inspect it', () => {
		const candidate = toEnvironmentEndpointCandidate(makeDetails({}));

		assert.ok(candidate);
		assert.strictEqual(candidate?.agentManagementUrl, undefined);
		assert.strictEqual(candidate?.dataverseUrl, 'https://org82dd85c2.crm.dynamics.com/');
	});

	test('surfaces the endpoint when Copilot Studio is enabled', () => {
		const candidate = toEnvironmentEndpointCandidate(makeDetails({
			runtimeEndpoints: { 'microsoft.PowerVirtualAgents': 'https://powervamg.us-il106.gateway.prod.island.powerapps.com/' },
		}));

		assert.strictEqual(candidate?.agentManagementUrl, 'https://powervamg.us-il106.gateway.prod.island.powerapps.com/');
	});

	test('still returns null when the environment has no linked Dataverse instance', () => {
		assert.strictEqual(toEnvironmentEndpointCandidate({ name: 'env', properties: { displayName: 'No Dataverse' } as any }), null);
	});
});

describe('resolveEnvironmentForCloneAsync', () => {
	const ENDPOINT = 'https://powervamg.us-il106.gateway.prod.island.powerapps.com/';

	const candidate = (agentManagementUrl?: string) => ({
		environmentId: 'Default-a30263b9-1caf-4db5-ab53-ed3850c0bd1f',
		displayName: 'Contoso (default)',
		dataverseUrl: 'https://org82dd85c2.crm.dynamics.com/',
		agentManagementUrl,
	});

	test('falls back to the list lookup when the per-id response omits the endpoint', async () => {
		const calls: string[] = [];
		const result = await resolveEnvironmentForCloneAsync(0 as any, 'env', null, null, undefined, [
			async () => { calls.push('byId'); return candidate(undefined); },
			async () => { calls.push('list'); return candidate(ENDPOINT); },
		]);

		assert.deepStrictEqual(calls, ['byId', 'list']);
		assert.strictEqual(result.environment?.agentManagementUrl, ENDPOINT);
		assert.strictEqual(result.endpointMissing, false);
	});

	test('performs only one lookup when the per-id response already carries the endpoint', async () => {
		let listCalls = 0;
		const result = await resolveEnvironmentForCloneAsync(0 as any, 'env', null, null, undefined, [
			async () => candidate(ENDPOINT),
			async () => { listCalls++; return candidate(ENDPOINT); },
		]);

		assert.strictEqual(listCalls, 0, 'the list lookup must not run once the endpoint is known');
		assert.strictEqual(result.environment?.agentManagementUrl, ENDPOINT);
	});

	test('reports the endpoint missing only when every lookup agrees it is absent', async () => {
		const result = await resolveEnvironmentForCloneAsync(0 as any, 'env', null, null, undefined, [
			async () => candidate(undefined),
			async () => candidate(undefined),
		]);

		assert.strictEqual(result.environment, null);
		assert.strictEqual(result.endpointMissing, true);
	});

	test('does not claim the endpoint is missing when the environment was never found', async () => {
		const result = await resolveEnvironmentForCloneAsync(0 as any, 'env', null, null, undefined, [
			async () => null,
			async () => null,
		]);

		assert.strictEqual(result.environment, null);
		assert.strictEqual(result.endpointMissing, false);
	});

	test('keeps trying the remaining lookups when one throws', async () => {
		const result = await resolveEnvironmentForCloneAsync(0 as any, 'env', null, null, undefined, [
			async () => { throw new Error('Request failed with status 403'); },
			async () => candidate(ENDPOINT),
		]);

		assert.strictEqual(result.environment?.agentManagementUrl, ENDPOINT);
	});

	test('returns a full environment the LSP can use, not just the endpoint', async () => {
		const result = await resolveEnvironmentForCloneAsync(0 as any, 'env', null, null, undefined, [
			async () => candidate(ENDPOINT),
		]);

		assert.strictEqual(result.environment?.dataverseUrl, 'https://org82dd85c2.crm.dynamics.com/');
		assert.strictEqual(result.environment?.environmentId, 'Default-a30263b9-1caf-4db5-ab53-ed3850c0bd1f');
		assert.strictEqual(result.environment?.displayName, 'Contoso (default)');
	});
});

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

test('environment request errors expose the service message without JSON and retain diagnostic details', async () => {
  let responseBody = '';
  const sandbox = createContext({
    exports: {},
    require: (id: string) => {
      switch (id) {
        case 'vscode': return { Uri: { from: () => ({}) } };
        case './account': return {
          FetchAccessToken: async () => ({
            response: new Response(responseBody, { status: 404 }), tokenInfo: {}
          })
        };
        case '../constants': return { CoreServicesClusterCategory, DefaultCoreServicesClusterCategory };
        case '../services/logger': return {};
        case '../utils/httpRequestError': return { HttpRequestError };
        default: throw new Error('Unexpected dependency: ' + id);
      }
    }
  });
  const compiled = await readFile(path.resolve(__dirname, '..', '..', 'clients', 'bapClient.js'), 'utf8');
  runInContext(compiled, sandbox);
  const message = "The environment 'unknown-env' could not be found in the tenant 'test-tenant'.";
  for (const sample of [
    { body: JSON.stringify({ error: { code: 'EnvironmentNotFound', message, detailUrlType: 'NotSpecified' } }), expected: message },
    { body: JSON.stringify({ error: { message: { detail: 'Unexpected format' } } }), expected: 'Request failed with status 404' },
    { body: JSON.stringify({ error: { message: ' ' } }), expected: 'Request failed with status 404' },
    { body: 'Not JSON', expected: 'Request failed with status 404' }
  ]) {
    responseBody = sample.body;
    await strictAssert.rejects(
      runInContext('exports.getEnvironmentEndpointByIdAsync(null, "unknown-env", null)', sandbox) as Promise<unknown>,
      error => {
        strictAssert.ok(error instanceof HttpRequestError);
        strictAssert.equal(error.userMessage, sample.expected);
        strictAssert.match(error.message, /status 404/);
        return true;
      }
    );
  }
});
