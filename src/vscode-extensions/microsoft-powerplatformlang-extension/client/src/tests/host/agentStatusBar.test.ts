import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import { buildAgentStatusBarText } from '../../services/agentStatusBar';
import { CopilotStudioWorkspace } from '../../sync/localWorkspaces';

const makeWorkspace = (overrides: Partial<CopilotStudioWorkspace>): CopilotStudioWorkspace => ({
	workspaceUri: 'file:///agents/test/',
	displayName: 'Test Agent',
	description: '',
	icon: undefined as any,
	type: 0 as any,
	...overrides,
});

describe('buildAgentStatusBarText', () => {
	test('shows only the display name when schema and environment are absent', () => {
		assert.strictEqual(buildAgentStatusBarText(makeWorkspace({})), '$(hexagon) Test Agent');
	});

	test('appends the schema name when present', () => {
		assert.strictEqual(
			buildAgentStatusBarText(makeWorkspace({ schemaName: 'cr1_agent' })),
			'$(hexagon) Test Agent · cr1_agent');
	});

	test('appends the environment id when present', () => {
		assert.strictEqual(
			buildAgentStatusBarText(makeWorkspace({ schemaName: 'cr1_agent', syncInfo: { environmentId: 'env-1' } as any })),
			'$(hexagon) Test Agent · cr1_agent · env-1');
	});

	test('includes the environment id even when the schema name is absent', () => {
		assert.strictEqual(
			buildAgentStatusBarText(makeWorkspace({ syncInfo: { environmentId: 'env-1' } as any })),
			'$(hexagon) Test Agent · env-1');
	});
});
