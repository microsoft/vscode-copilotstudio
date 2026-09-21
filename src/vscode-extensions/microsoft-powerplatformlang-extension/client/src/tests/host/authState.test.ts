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
	extractTenantId,
	buildAccountTenantIndex,
	findAccountsByTenant,
	selectTenantAccount,
	getSoleStoredAccount,
	getStoredAccountSummaries,
	getAccountCandidates,
	selectAccountCandidates,
	isIdentityUnbound,
	isAccountSelectable,
	resolveAccountIdentity,
	resolveTenantId,
	StoredAccountSummary,
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

	test('a blank account id and email with an unknown tenant reports as not signed in', () => {
		assert.strictEqual(isAccountSignedInSync('', null as any, 'tenant-none'), false);
		assert.strictEqual(getAccountHealth('', undefined, 'tenant-none'), 'signedOut');
	});

	test('an explicitly bound account is not rescued by its tenant', () => {
		assert.strictEqual(isAccountSignedInSync('unknown-id', 'unknown@example.invalid', 'tenant-none'), false);
	});

	test('an ambiguous tenant is not silently resolved to one of its accounts', () => {
		assert.strictEqual(isAccountSignedInSync('', undefined, 'tenant-with-many'), false);
		assert.deepStrictEqual(selectTenantAccount(TWO_ACCOUNTS), {});
	});
});

const ZERO_TENANT = '00000000-0000-0000-0000-000000000000';
const REAL_TENANT = 'a30263b9-1caf-4db5-ab53-ed3850c0bd1f';
const OTHER_TENANT = 'ffffffff-1111-2222-3333-444444444444';
const NO_ACCOUNTS: StoredAccountSummary[] = [];
const ONE_ACCOUNT: StoredAccountSummary[] = [{ accountId: `oid1.${REAL_TENANT}`, accountEmail: 'one@contoso.invalid' }];
const TWO_ACCOUNTS: StoredAccountSummary[] = [
	{ accountId: `oid1.${REAL_TENANT}`, accountEmail: 'one@contoso.invalid' },
	{ accountId: `oid2.${REAL_TENANT}`, accountEmail: 'two@contoso.invalid' },
];

describe('selectAccountCandidates', () => {
	test('offers every signed-in account when the tenant is absent or all zero', () => {
		assert.deepStrictEqual(selectAccountCandidates([], undefined, () => TWO_ACCOUNTS), TWO_ACCOUNTS);
		assert.deepStrictEqual(selectAccountCandidates([], ZERO_TENANT, () => TWO_ACCOUNTS), TWO_ACCOUNTS);
		assert.deepStrictEqual(selectAccountCandidates([], ZERO_TENANT, () => ONE_ACCOUNT), ONE_ACCOUNT);
		assert.deepStrictEqual(selectAccountCandidates([], ZERO_TENANT, () => NO_ACCOUNTS), NO_ACCOUNTS);
	});

	test('offers only the tenant matches when the workspace records a real tenant', () => {
		assert.deepStrictEqual(selectAccountCandidates(TWO_ACCOUNTS, REAL_TENANT, () => ONE_ACCOUNT), TWO_ACCOUNTS);
	});

	test('offers nothing when a real tenant matches no signed-in account', () => {
		let widened = false;
		const candidates = selectAccountCandidates([], OTHER_TENANT, () => {
			widened = true;
			return TWO_ACCOUNTS;
		});

		assert.deepStrictEqual(candidates, []);
		assert.strictEqual(widened, false);
	});
});

describe('isIdentityUnbound', () => {
	test('treats blank and whitespace identities as unbound', () => {
		assert.strictEqual(isIdentityUnbound('', undefined), true);
		assert.strictEqual(isIdentityUnbound('   ', '  '), true);
		assert.strictEqual(isIdentityUnbound(undefined, undefined), true);
	});

	test('treats any recorded identity as bound', () => {
		assert.strictEqual(isIdentityUnbound('bound-id', undefined), false);
		assert.strictEqual(isIdentityUnbound('', 'bound@contoso.invalid'), false);
	});
});

describe('Unresolved account state', () => {
	test('an ambiguous tenant offers a selection instead of reporting signed out', () => {
		assert.strictEqual(selectAccountCandidates(TWO_ACCOUNTS, REAL_TENANT, () => NO_ACCOUNTS).length, 2);
		assert.strictEqual(isIdentityUnbound('', undefined), true);
	});

	test('a real tenant that matches nothing is never selectable, so it stays signed out', () => {
		assert.strictEqual(isAccountSelectable('', undefined, OTHER_TENANT), false);
		assert.strictEqual(getAccountHealth('', undefined, OTHER_TENANT), 'signedOut');
	});

	test('an explicitly bound account is never selectable', () => {
		assert.strictEqual(isAccountSelectable('bound-id', undefined, ZERO_TENANT), false);
		assert.strictEqual(isAccountSelectable('', 'bound@contoso.com', ZERO_TENANT), false);
	});

	test('selectable is exactly an unbound identity with at least one candidate', () => {
		assert.strictEqual(
			isAccountSelectable('', undefined, ZERO_TENANT),
			getAccountCandidates(ZERO_TENANT).length > 0);
		assert.strictEqual(
			isAccountSelectable('', undefined, undefined),
			getAccountCandidates(undefined).length > 0);
	});

	test('health never reports unresolved unless a selection would present candidates', () => {
		const health = getAccountHealth('', undefined, ZERO_TENANT);
		if (health === 'unresolved') {
			assert.ok(getAccountCandidates(ZERO_TENANT).length > 0);
		} else {
			assert.ok(health === 'ok' || health === 'signedOut', health);
		}
	});

	test('the badge invites account selection whenever health is unresolved', () => {
		const badge = computeAgentAccountBadge(
			makeWorkspace({ syncInfo: { accountInfo: { accountId: '', accountEmail: undefined, tenantId: ZERO_TENANT } } as any }),
			false);

		if (badge.health === 'unresolved') {
			assert.ok(badge.description.includes('select account'), badge.description);
		} else {
			assert.ok(badge.health === 'ok' || badge.health === 'signedOut', badge.health);
		}
	});
});

describe('extractTenantId', () => {
	test('reads the tenant suffix from a VS Code account id', () => {
		assert.strictEqual(
			extractTenantId('674b4cab-fb0c-466a-a81d-5a94243993b7.a30263b9-1caf-4db5-ab53-ed3850c0bd1f'),
			'a30263b9-1caf-4db5-ab53-ed3850c0bd1f');
	});

	test('lower-cases the extracted tenant', () => {
		assert.strictEqual(extractTenantId('OID.TENANT-ABC'), 'tenant-abc');
	});

	test('returns undefined for ids without a tenant suffix', () => {
		assert.strictEqual(extractTenantId('no-dot-here'), undefined);
		assert.strictEqual(extractTenantId('.leading'), undefined);
		assert.strictEqual(extractTenantId('trailing.'), undefined);
	});

	test('returns undefined for blank input', () => {
		assert.strictEqual(extractTenantId(''), undefined);
		assert.strictEqual(extractTenantId('   '), undefined);
		assert.strictEqual(extractTenantId(undefined), undefined);
	});
});

describe('buildAccountTenantIndex', () => {
	test('groups accounts by their tenant suffix', () => {
		const index = buildAccountTenantIndex([
			{ accountId: 'oid1.tenant-a', accountEmail: 'one@a.com' },
			{ accountId: 'oid2.tenant-a', accountEmail: 'two@a.com' },
			{ accountId: 'oid3.tenant-b', accountEmail: 'three@b.com' },
		]);

		assert.strictEqual(index.get('tenant-a')?.length, 2);
		assert.strictEqual(index.get('tenant-b')?.length, 1);
		assert.strictEqual(index.get('tenant-b')?.[0].accountEmail, 'three@b.com');
	});

	test('matches tenant keys case-insensitively', () => {
		assert.strictEqual(buildAccountTenantIndex([{ accountId: 'oid.TENANT-A' }]).get('tenant-a')?.length, 1);
	});

	test('skips accounts whose id carries no tenant suffix', () => {
		assert.strictEqual(buildAccountTenantIndex([{ accountId: 'no-tenant-suffix' }]).size, 0);
	});

	test('returns an empty index for an empty account list', () => {
		assert.strictEqual(buildAccountTenantIndex([]).size, 0);
	});
});

describe('findAccountsByTenant', () => {
	test('returns an empty list for an unknown tenant', () => {
		assert.deepStrictEqual(findAccountsByTenant('tenant-that-does-not-exist'), []);
	});

	test('returns an empty list for blank input', () => {
		assert.deepStrictEqual(findAccountsByTenant(''), []);
		assert.deepStrictEqual(findAccountsByTenant(undefined), []);
	});
});

describe('selectTenantAccount', () => {
	test('resolves when the tenant holds exactly one account', () => {
		assert.deepStrictEqual(
			selectTenantAccount([{ accountId: 'oid.tenant-a', accountEmail: 'one@a.com' }]),
			{ accountId: 'oid.tenant-a', accountEmail: 'one@a.com' });
	});

	test('refuses to guess when the tenant holds several accounts', () => {
		assert.deepStrictEqual(
			selectTenantAccount([{ accountId: 'oid1.tenant-a' }, { accountId: 'oid2.tenant-a' }]),
			{});
	});

	test('returns nothing when the tenant holds no accounts', () => {
		assert.deepStrictEqual(selectTenantAccount([]), {});
	});
});

describe('resolveAccountIdentity', () => {
	test('prefers an explicitly bound account id', () => {
		assert.deepStrictEqual(
			resolveAccountIdentity({ accountId: 'bound-id', tenantId: 'tenant-a' }),
			{ accountId: 'bound-id', accountEmail: undefined, tenantId: 'tenant-a' });
	});

	test('prefers an explicitly bound account email', () => {
		assert.deepStrictEqual(
			resolveAccountIdentity({ accountEmail: 'bound@contoso.com', tenantId: 'tenant-a' }),
			{ accountId: undefined, accountEmail: 'bound@contoso.com', tenantId: 'tenant-a' });
	});

	test('normalizes a blank account id to absent while keeping the email', () => {
		assert.deepStrictEqual(
			resolveAccountIdentity({ accountId: '', accountEmail: 'bound@contoso.com' }),
			{ accountId: undefined, accountEmail: 'bound@contoso.com' });
	});

	test('does not attach a tenant when no identity could be resolved', () => {
		assert.deepStrictEqual(resolveAccountIdentity({ accountId: '', tenantId: 'tenant-that-does-not-exist' }), {});
	});

	test('returns nothing when identity and tenant are all absent', () => {
		assert.deepStrictEqual(resolveAccountIdentity({ accountId: '', accountEmail: undefined }), {});
		assert.deepStrictEqual(resolveAccountIdentity(undefined), {});
	});

	test('returns nothing when the tenant matches no signed-in account', () => {
		assert.deepStrictEqual(resolveAccountIdentity({ accountId: '', tenantId: 'tenant-that-does-not-exist' }), {});
	});

	test('does not fall back to the sole account when a real tenant does not match', () => {
		assert.deepStrictEqual(resolveAccountIdentity({ accountId: '', tenantId: 'a30263b9-1caf-4db5-ab53-ed3850c0bd1f' }), {});
	});

	test('treats an all-zero tenant as absent rather than as a real tenant', () => {
		const resolved = resolveAccountIdentity({ accountId: '', tenantId: ZERO_TENANT });
		const soleAccount = getSoleStoredAccount();
		assert.strictEqual(resolved.accountId, soleAccount?.accountId);
		assert.strictEqual(resolved.accountEmail, soleAccount?.accountEmail);
	});

	test('resolves the tenant from the account id when the stored tenant is unusable', () => {
		assert.strictEqual(
			resolveAccountIdentity({ accountId: '674b4cab-fb0c-466a-a81d-5a94243993b7.a30263b9-1caf-4db5-ab53-ed3850c0bd1f', tenantId: undefined }).tenantId,
			'a30263b9-1caf-4db5-ab53-ed3850c0bd1f');

		assert.strictEqual(
			resolveAccountIdentity({ accountId: '674b4cab-fb0c-466a-a81d-5a94243993b7.a30263b9-1caf-4db5-ab53-ed3850c0bd1f', tenantId: ZERO_TENANT }).tenantId,
			'a30263b9-1caf-4db5-ab53-ed3850c0bd1f');
	});

	test('keeps a usable stored tenant in preference to the account id suffix', () => {
		assert.strictEqual(
			resolveAccountIdentity({ accountId: 'oid.ffffffff-1111-2222-3333-444444444444', tenantId: 'a30263b9-1caf-4db5-ab53-ed3850c0bd1f' }).tenantId,
			'a30263b9-1caf-4db5-ab53-ed3850c0bd1f');
	});

	test('omits the tenant when neither the store nor the account id supplies one', () => {
		assert.strictEqual(resolveAccountIdentity({ accountId: 'no-suffix', tenantId: undefined }).tenantId, undefined);
	});
});

describe('resolveTenantId', () => {
	test('prefers the stored tenant when it is usable', () => {
		assert.strictEqual(resolveTenantId('a30263b9-1caf-4db5-ab53-ed3850c0bd1f', 'ffffffff-1111-2222-3333-444444444444'), 'a30263b9-1caf-4db5-ab53-ed3850c0bd1f');
	});

	test('falls back to the token tenant when the stored tenant is blank or all zero', () => {
		assert.strictEqual(resolveTenantId(undefined, 'ffffffff-1111-2222-3333-444444444444'), 'ffffffff-1111-2222-3333-444444444444');
		assert.strictEqual(resolveTenantId('', 'ffffffff-1111-2222-3333-444444444444'), 'ffffffff-1111-2222-3333-444444444444');
		assert.strictEqual(resolveTenantId(ZERO_TENANT, 'ffffffff-1111-2222-3333-444444444444'), 'ffffffff-1111-2222-3333-444444444444');
	});

	test('returns an empty string when neither tenant is usable', () => {
		assert.strictEqual(resolveTenantId(undefined, undefined), '');
		assert.strictEqual(resolveTenantId(ZERO_TENANT, ZERO_TENANT), '');
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

	test('an email-only binding stays scoped to that account instead of falling back', async () => {
		const resource = Uri.parse('https://example.crm.dynamics.com/');
		let caught: unknown;
		try {
			await getAccessTokenByAccountId(resource, undefined, 'email-only@example.invalid');
		} catch (error) {
			caught = error;
		}
		assert.ok(caught instanceof AuthError, 'an email-only binding must not fall back to an arbitrary account');
		assert.strictEqual((caught as AuthError).accountEmail, 'email-only@example.invalid');
	});

	test('a blank account id with an email binding stays scoped to that account', async () => {
		const resource = Uri.parse('https://example.crm.dynamics.com/');
		let caught: unknown;
		try {
			await getAccessTokenByAccountId(resource, '', 'blank-id@example.invalid');
		} catch (error) {
			caught = error;
		}
		assert.ok(caught instanceof AuthError, 'a blank account id must not discard the email binding');
		assert.strictEqual((caught as AuthError).accountEmail, 'blank-id@example.invalid');
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

	test('still reports signed out when a blank identity has no matching signed-in tenant', () => {
		const badge = computeAgentAccountBadge(makeWorkspace({
			description: 'Dev',
			syncInfo: { accountInfo: { accountId: '', accountEmail: null, tenantId: 'tenant-that-does-not-exist' } } as any,
		}), false);
		assert.strictEqual(badge.health, 'signedOut');
	});

	test('omits an account label when the workspace records no account email', () => {
		const badge = computeAgentAccountBadge(makeWorkspace({
			description: 'Dev',
			syncInfo: { accountInfo: { accountId: '', accountEmail: null, tenantId: 'tenant-that-does-not-exist' } } as any,
		}), false);
		assert.strictEqual(badge.description, 'signed out');
	});
});
