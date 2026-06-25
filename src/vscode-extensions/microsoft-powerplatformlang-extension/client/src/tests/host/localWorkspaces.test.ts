import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import { getDuplicateDisplayNames, buildAgentIdentityTooltip, CopilotStudioWorkspace } from '../../sync/localWorkspaces';

const makeWorkspace = (overrides: Partial<CopilotStudioWorkspace>): CopilotStudioWorkspace => ({
	workspaceUri: 'file:///agents/test/',
	displayName: 'Test Agent',
	description: '',
	icon: undefined as any,
	type: 0 as any,
	...overrides,
});

describe('getDuplicateDisplayNames', () => {
	test('returns empty set when all display names are unique', () => {
		const result = getDuplicateDisplayNames([
			makeWorkspace({ displayName: 'Alpha' }),
			makeWorkspace({ displayName: 'Beta' }),
		]);
		assert.strictEqual(result.size, 0);
	});

	test('flags names that appear more than once (case-insensitive, lowercased)', () => {
		const result = getDuplicateDisplayNames([
			makeWorkspace({ displayName: 'Agent B4 CC' }),
			makeWorkspace({ displayName: 'agent b4 cc' }),
			makeWorkspace({ displayName: 'Unique One' }),
		]);
		assert.strictEqual(result.size, 1);
		assert.ok(result.has('agent b4 cc'));
		assert.ok(!result.has('unique one'));
	});

	test('returns empty set for an empty list', () => {
		assert.strictEqual(getDuplicateDisplayNames([]).size, 0);
	});
});

describe('buildAgentIdentityTooltip', () => {
	test('includes display name, schema, environment display name, tenant, and account', () => {
		const tooltip = buildAgentIdentityTooltip(makeWorkspace({
			displayName: 'Agent B4 CC',
			schemaName: 'cr1a2_agentb4cc',
			syncInfo: {
				environmentId: 'env-123',
				environmentDisplayName: 'Contoso Dev',
				accountInfo: { tenantId: 'tenant-9', accountEmail: 'dev@contoso.com' },
			} as any,
		}));

		const value = tooltip.value;
		assert.ok(value.includes('Agent B4 CC'));
		assert.ok(value.includes('cr1a2_agentb4cc'));
		assert.ok(value.includes('Contoso Dev'));
		assert.ok(value.includes('env-123'));
		assert.ok(value.includes('tenant-9'));
		assert.ok(value.includes('dev@contoso.com'));
	});

	test('falls back to em dash for missing schema/account and omits tenant when absent', () => {
		const tooltip = buildAgentIdentityTooltip(makeWorkspace({
			displayName: 'No Sync Agent',
			syncInfo: undefined,
		}));

		const value = tooltip.value;
		assert.ok(value.includes('No Sync Agent'));
		assert.ok(value.includes('SchemaName: `—`'));
		assert.ok(!value.includes('Tenant:'));
		assert.ok(value.includes('Status: Not connected'));
	});
});
