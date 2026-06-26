import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import { buildWorkspaceQuickPickDetail } from '../../sync/workspacePicker';
import { CopilotStudioWorkspace } from '../../sync/localWorkspaces';

const makeWorkspace = (overrides: Partial<CopilotStudioWorkspace>): CopilotStudioWorkspace => ({
	workspaceUri: 'file:///agents/test/',
	displayName: 'Test Agent',
	description: 'desc',
	icon: undefined as any,
	type: 0 as any,
	...overrides,
});

describe('buildWorkspaceQuickPickDetail', () => {
	test('uses the workspace description when not a duplicate', () => {
		const result = buildWorkspaceQuickPickDetail(makeWorkspace({ schemaName: 'cr1_agent' }), false);
		assert.strictEqual(result.description, 'desc');
	});

	test('uses the schema name as description for duplicates', () => {
		const result = buildWorkspaceQuickPickDetail(makeWorkspace({ schemaName: 'cr1_agent' }), true);
		assert.strictEqual(result.description, 'cr1_agent');
	});

	test('falls back to the description for duplicates without a schema name', () => {
		const result = buildWorkspaceQuickPickDetail(makeWorkspace({ schemaName: undefined }), true);
		assert.strictEqual(result.description, 'desc');
	});

	test('composes account and environment into the detail line', () => {
		const result = buildWorkspaceQuickPickDetail(makeWorkspace({
			syncInfo: { environmentId: 'env-1', accountInfo: { accountEmail: 'a@b.com' } } as any,
		}), false);
		assert.strictEqual(result.detail, 'account: a@b.com · env: env-1');
	});

	test('includes only the environment when no account is available', () => {
		const result = buildWorkspaceQuickPickDetail(makeWorkspace({
			syncInfo: { environmentId: 'env-1' } as any,
		}), false);
		assert.strictEqual(result.detail, 'env: env-1');
	});

	test('omits the detail when no account or environment is available', () => {
		const result = buildWorkspaceQuickPickDetail(makeWorkspace({}), false);
		assert.strictEqual(result.detail, undefined);
	});
});
