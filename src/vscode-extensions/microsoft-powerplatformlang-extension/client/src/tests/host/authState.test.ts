import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import { Uri } from 'vscode';
import {
	AuthError,
	classifyAuthError,
	getAuthAccountState,
	clearAuthAccountState,
	clearSuppressedAuthState,
	clearRecoverableAuthState,
	hasRecoverableAuthState,
	getAccountHealth,
	isAccountSignedInSync,
	getAccessTokenByAccountId,
	getCopilotStudioAccessTokenByAccountId,
} from '../../clients/account';
import { computeAgentAccountBadge } from '../../sync/agentChangesTreeProvider';
import { CopilotStudioWorkspace } from '../../sync/localWorkspaces';
import { DefaultCoreServicesClusterCategory } from '../../constants';

const makeWorkspace = (overrides: Partial<CopilotStudioWorkspace>): CopilotStudioWorkspace => ({
	workspaceUri: 'file:///agents/test/',
	displayName: 'Test Agent',
	description: '',
	icon: undefined as any,
	type: 0 as any,
	...overrides,
});

describe('classifyAuthError', () => {
	test('classifies user cancellation as cancelled', () => {
		assert.strictEqual(classifyAuthError(new Error('User canceled sign in')), 'cancelled');
		assert.strictEqual(classifyAuthError(new Error('The user did not consent to access')), 'cancelled');
	});

	test('classifies tenant/account failures as terminal', () => {
		for (const code of ['AADSTS50020', 'AADSTS90072', 'AADSTS500011', 'AADSTS700016', 'AADSTS50057', 'AADSTS50128', 'AADSTS50076', 'AADSTS700082']) {
			assert.strictEqual(classifyAuthError(new Error(`Sign-in failed: ${code} details`)), 'terminal', code);
		}
		assert.strictEqual(classifyAuthError(new Error('invalid_grant: token expired')), 'terminal');
	});

	test('classifies broker, network, and unknown failures as transient', () => {
		assert.strictEqual(classifyAuthError(new Error('platform_broker_error: See https://aka.ms/msal-net-wam')), 'transient');
		assert.strictEqual(classifyAuthError(new Error('network request failed')), 'transient');
		assert.strictEqual(classifyAuthError(new Error('DialogService: refused to show dialog')), 'transient');
		assert.strictEqual(classifyAuthError('some non-error value'), 'transient');
	});

	test('honors an AuthError classification directly', () => {
		assert.strictEqual(classifyAuthError(new AuthError('terminal', 'boom')), 'terminal');
		assert.strictEqual(classifyAuthError(new AuthError('cancelled', 'boom')), 'cancelled');
	});
});

describe('AuthError', () => {
	test('carries classification and account identity', () => {
		const error = new AuthError('terminal', 'no longer usable', 'acc-1', 'dev@b.com');
		assert.ok(error instanceof Error);
		assert.strictEqual(error.name, 'AuthError');
		assert.strictEqual(error.classification, 'terminal');
		assert.strictEqual(error.accountId, 'acc-1');
		assert.strictEqual(error.accountEmail, 'dev@b.com');
	});
});

describe('Per-account auth state', () => {
	test('unknown account has no cached state', () => {
		clearRecoverableAuthState();
		assert.strictEqual(getAuthAccountState('never-seen', 'never@seen.invalid'), undefined);
		assert.strictEqual(hasRecoverableAuthState(), false);
	});

	test('clear helpers are safe no-ops on empty state', () => {
		clearRecoverableAuthState();
		clearAuthAccountState('missing-account');
		clearSuppressedAuthState('missing-account');
		assert.strictEqual(hasRecoverableAuthState(), false);
	});

	test('clearRecoverableAuthState is idempotent', () => {
		clearRecoverableAuthState();
		assert.strictEqual(hasRecoverableAuthState(), false);
		clearRecoverableAuthState();
		assert.strictEqual(hasRecoverableAuthState(), false);
	});
});

describe('Account health snapshot', () => {
	test('an unknown account reports as not signed in', () => {
		assert.strictEqual(isAccountSignedInSync('unknown-id', 'unknown@example.invalid'), false);
		assert.strictEqual(getAccountHealth('unknown-id', 'unknown@example.invalid'), 'signedOut');
	});
});

describe('Silent-by-default token acquisition', () => {
	test('getAccessTokenByAccountId defaults to silent and throws a classified AuthError for an unknown account', async () => {
		const resource = Uri.parse('https://example.crm.dynamics.com/');
		let caught: unknown;
		try {
			await getAccessTokenByAccountId(resource, 'nonexistent-account-silent');
		} catch (error) {
			caught = error;
		}
		assert.ok(caught instanceof AuthError, 'should throw an AuthError without prompting');
		assert.strictEqual((caught as AuthError).classification, 'transient');
		assert.strictEqual((caught as AuthError).accountId, 'nonexistent-account-silent');
	});

	test('getCopilotStudioAccessTokenByAccountId defaults to silent and throws AuthError for an unknown account', async () => {
		let caught: unknown;
		try {
			await getCopilotStudioAccessTokenByAccountId(DefaultCoreServicesClusterCategory, 'nonexistent-account-cs', 'nobody@example.invalid');
		} catch (error) {
			caught = error;
		}
		assert.ok(caught instanceof AuthError, 'should throw an AuthError without prompting');
		assert.strictEqual((caught as AuthError).accountId, 'nonexistent-account-cs');
	});
});

describe('computeAgentAccountBadge', () => {
	test('includes the bound account and a signed-out status for an unknown account', () => {
		const badge = computeAgentAccountBadge(makeWorkspace({
			description: 'Dev',
			syncInfo: { accountInfo: { accountId: 'acc-x', accountEmail: 'nobody@example.invalid' } } as any,
		}), false);
		assert.strictEqual(badge.health, 'signedOut');
		assert.ok(badge.description.includes('nobody@example.invalid'));
		assert.ok(badge.description.includes('signed out'));
	});

	test('uses the schema name for disambiguation when the display name is duplicated', () => {
		const badge = computeAgentAccountBadge(makeWorkspace({
			description: 'Dev',
			schemaName: 'cr1a2_agent',
			syncInfo: { accountInfo: { accountId: 'acc-x', accountEmail: 'nobody@example.invalid' } } as any,
		}), true);
		assert.ok(badge.description.startsWith('cr1a2_agent'));
	});

	test('reports ok health and an empty description when there is no bound account', () => {
		const badge = computeAgentAccountBadge(makeWorkspace({ description: 'Dev', syncInfo: undefined }), false);
		assert.strictEqual(badge.health, 'ok');
		assert.strictEqual(badge.description, '');
	});
});
