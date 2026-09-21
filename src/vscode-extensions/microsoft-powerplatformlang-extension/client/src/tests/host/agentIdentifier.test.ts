import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import { tryGetAgentIdentifier } from '../../clone/agentIdentifier';
import { CoreServicesClusterCategory, DefaultCoreServicesClusterCategory } from '../../constants';

const ENV = 'fa5c1b48-9fac-e55e-a7f2-f6d063859d08';
const AGENT = '42245493-2af3-4f5f-818a-bf77523e21d3';

describe('tryGetAgentIdentifier', () => {
	test('reads an agent id from the /agents/ portal url', () => {
		const result = tryGetAgentIdentifier(`https://copilotstudio.microsoft.com/environments/${ENV}/agents/${AGENT}`);

		assert.strictEqual(result?.environmentId, ENV);
		assert.strictEqual(result?.agentId, AGENT);
	});

	test('reads an agent id from the legacy /bots/ portal url', () => {
		const result = tryGetAgentIdentifier(`https://copilotstudio.microsoft.com/environments/${ENV}/bots/${AGENT}`);

		assert.strictEqual(result?.environmentId, ENV);
		assert.strictEqual(result?.agentId, AGENT);
	});

	test('keeps the realm cluster when the url is a preview portal /agents/ link', () => {
		const result = tryGetAgentIdentifier(`https://copilotstudio.preview.microsoft.com/environments/${ENV}/agents/${AGENT}`);

		assert.strictEqual(result?.environmentId, ENV);
		assert.strictEqual(result?.agentId, AGENT);
		assert.strictEqual(result?.clusterCategory, CoreServicesClusterCategory.FirstRelease);
	});

	test('ignores trailing path segments and query strings', () => {
		assert.strictEqual(tryGetAgentIdentifier(`https://copilotstudio.microsoft.com/environments/${ENV}/agents/${AGENT}/overview`)?.agentId, AGENT);
		assert.strictEqual(tryGetAgentIdentifier(`https://copilotstudio.microsoft.com/environments/${ENV}/agents/${AGENT}?tab=canvas`)?.agentId, AGENT);
		assert.strictEqual(tryGetAgentIdentifier(`https://copilotstudio.microsoft.com/environments/${ENV}/bots/${AGENT}/overview`)?.agentId, AGENT);
	});

	test('tolerates surrounding whitespace from a clipboard copy', () => {
		assert.strictEqual(tryGetAgentIdentifier(`  https://copilotstudio.microsoft.com/environments/${ENV}/agents/${AGENT}\n`)?.agentId, AGENT);
	});

	test('returns the environment with no agent when the url points at the environment only', () => {
		const result = tryGetAgentIdentifier(`https://copilotstudio.microsoft.com/environments/${ENV}`);

		assert.strictEqual(result?.environmentId, ENV);
		assert.strictEqual(result?.agentId, undefined);
	});

	test('defaults to the production cluster when no realm is present', () => {
		assert.strictEqual(
			tryGetAgentIdentifier(`https://copilotstudio.microsoft.com/environments/${ENV}/agents/${AGENT}`)?.clusterCategory,
			DefaultCoreServicesClusterCategory);
	});

	test('returns null for urls that are not Copilot Studio portal links', () => {
		assert.strictEqual(tryGetAgentIdentifier('https://example.invalid/environments/abc/agents/def'), null);
		assert.strictEqual(tryGetAgentIdentifier('just some copied text'), null);
		assert.strictEqual(tryGetAgentIdentifier(''), null);
	});
});
