import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { Uri } from 'vscode';
import { getDuplicateDisplayNames, buildAgentIdentityTooltip, tryRepairAccountInfo, tryRepairAgentManagementEndpoint, CopilotStudioWorkspace } from '../../sync/localWorkspaces';
import { AgentSyncInfo } from '../../types';
import { StoredAccountSummary } from '../../clients/account';

const PAC_CONNECTION_FILE = {
	DataverseEndpoint: 'https://orgdd8356c3.crm.dynamics.com',
	EnvironmentId: 'a7975ddd-87fe-e986-9280-bfe47a2e7b10',
	AccountInfo: {
		AccountId: '',
		TenantId: 'a30263b9-1caf-4db5-ab53-ed3850c0bd1f',
		AccountEmail: null,
		clusterCategory: null,
	},
	AgentId: '93b2ef34-5d52-4b2e-ad2e-214db365a58f',
	ComponentCollectionId: null,
	SolutionVersions: {
		SolutionVersions: { msdyn_RelevanceSearch: '1.0.3482.1' },
		CopilotStudioSolutionVersion: '2026.8.2.21776719',
	},
	AgentManagementEndpoint: 'https://powervamg.us-il106.gateway.prod.island.powerapps.com/',
	UnknownFutureKey: { nested: true },
};

const CLOUD_CACHE_FILES = ['botdefinition.json', 'changetoken.txt'];

let workspaceCounter = 0;

const createWorkspaceFolder = (connectionFile: unknown = PAC_CONNECTION_FILE): string => {
	const root = fs.mkdtempSync(path.join(os.tmpdir(), 'mcs-repair-'));
	const agentFolder = path.join(root, `agent-${workspaceCounter++}`);
	fs.mkdirSync(path.join(agentFolder, '.mcs'), { recursive: true });
	fs.writeFileSync(path.join(agentFolder, '.mcs', 'conn.json'), typeof connectionFile === 'string' ? connectionFile : JSON.stringify(connectionFile, null, 4), 'utf-8');
	fs.writeFileSync(path.join(agentFolder, '.mcs', 'botdefinition.json'), '{"$kind":"BotDefinition","entity":{"version":3635324}}', 'utf-8');
	fs.writeFileSync(path.join(agentFolder, '.mcs', 'changetoken.txt'), 'remote-change-token-value', 'utf-8');
	return agentFolder;
};

const readConnectionFile = (agentFolder: string): any => JSON.parse(fs.readFileSync(path.join(agentFolder, '.mcs', 'conn.json'), 'utf-8'));

const snapshotCloudCache = (agentFolder: string): Record<string, Buffer> => Object.fromEntries(
	CLOUD_CACHE_FILES.map(name => [name, fs.readFileSync(path.join(agentFolder, '.mcs', name))]));

const assertCloudCacheUntouched = (agentFolder: string, before: Record<string, Buffer>): void => {
	for (const name of CLOUD_CACHE_FILES) {
		assert.ok(before[name].equals(fs.readFileSync(path.join(agentFolder, '.mcs', name))), `${name} must not be rewritten`);
	}
};

const makeSyncInfo = (overrides: Partial<AgentSyncInfo['accountInfo']> = {}): AgentSyncInfo => ({
	accountInfo: {
		accountId: '',
		accountEmail: undefined,
		tenantId: 'a30263b9-1caf-4db5-ab53-ed3850c0bd1f',
		...overrides,
	},
} as AgentSyncInfo);

const makeWorkspace = (overrides: Partial<CopilotStudioWorkspace>): CopilotStudioWorkspace => ({
	workspaceUri: 'file:///agents/test/',
	displayName: 'Test Agent',
	description: '',
	icon: undefined as any,
	type: 0 as any,
	...overrides,
});

describe('tryRepairAccountInfo', () => {
	test('adopts the only signed-in account in the tenant and persists it', async () => {
		const agentFolder = createWorkspaceFolder();
		const cloudCacheBefore = snapshotCloudCache(agentFolder);
		const syncInfo = makeSyncInfo();

		const repaired = await tryRepairAccountInfo(syncInfo, Uri.file(agentFolder).toString(), {
			findAccounts: () => [{ accountId: 'oid.a30263b9-1caf-4db5-ab53-ed3850c0bd1f', accountEmail: 'nguhoa@asdkt4.onmicrosoft.com' }],
		});

		assert.strictEqual(repaired, true);
		assert.strictEqual(syncInfo.accountInfo.accountId, 'oid.a30263b9-1caf-4db5-ab53-ed3850c0bd1f');
		assert.strictEqual(syncInfo.accountInfo.accountEmail, 'nguhoa@asdkt4.onmicrosoft.com');

		const persisted = readConnectionFile(agentFolder);
		assert.strictEqual(persisted.AccountInfo.AccountId, 'oid.a30263b9-1caf-4db5-ab53-ed3850c0bd1f');
		assert.strictEqual(persisted.AccountInfo.AccountEmail, 'nguhoa@asdkt4.onmicrosoft.com');
		assertCloudCacheUntouched(agentFolder, cloudCacheBefore);
	});

	test('leaves every unrelated connection field untouched', async () => {
		const agentFolder = createWorkspaceFolder();

		await tryRepairAccountInfo(makeSyncInfo(), Uri.file(agentFolder).toString(), {
			findAccounts: () => [{ accountId: 'oid.a30263b9-1caf-4db5-ab53-ed3850c0bd1f', accountEmail: 'dev@contoso.com' }],
		});

		const persisted = readConnectionFile(agentFolder);
		assert.strictEqual(persisted.DataverseEndpoint, PAC_CONNECTION_FILE.DataverseEndpoint);
		assert.strictEqual(persisted.EnvironmentId, PAC_CONNECTION_FILE.EnvironmentId);
		assert.strictEqual(persisted.AgentId, PAC_CONNECTION_FILE.AgentId);
		assert.strictEqual(persisted.ComponentCollectionId, null);
		assert.strictEqual(persisted.AgentManagementEndpoint, PAC_CONNECTION_FILE.AgentManagementEndpoint);
		assert.strictEqual(persisted.AccountInfo.TenantId, PAC_CONNECTION_FILE.AccountInfo.TenantId);
		assert.strictEqual(persisted.AccountInfo.clusterCategory, null);
		assert.deepStrictEqual(persisted.SolutionVersions, PAC_CONNECTION_FILE.SolutionVersions);
		assert.deepStrictEqual(persisted.UnknownFutureKey, PAC_CONNECTION_FILE.UnknownFutureKey);
	});

	test('prompts when the tenant holds several accounts and persists the choice', async () => {
		const agentFolder = createWorkspaceFolder();
		const tenantAccounts = [
			{ accountId: 'oid1.a30263b9-1caf-4db5-ab53-ed3850c0bd1f', accountEmail: 'first@contoso.com' },
			{ accountId: 'oid2.a30263b9-1caf-4db5-ab53-ed3850c0bd1f', accountEmail: 'second@contoso.com' },
		];
		let promptedWith: unknown;

		const repaired = await tryRepairAccountInfo(makeSyncInfo(), Uri.file(agentFolder).toString(), {
			findAccounts: () => tenantAccounts,
			promptForAccount: async (matches) => {
				promptedWith = matches;
				return matches[1];
			},
		});

		assert.strictEqual(repaired, true);
		assert.deepStrictEqual(promptedWith, tenantAccounts);
		assert.strictEqual(readConnectionFile(agentFolder).AccountInfo.AccountEmail, 'second@contoso.com');
	});

	test('writes nothing when the prompt is cancelled', async () => {
		const agentFolder = createWorkspaceFolder();
		const cloudCacheBefore = snapshotCloudCache(agentFolder);

		const repaired = await tryRepairAccountInfo(makeSyncInfo(), Uri.file(agentFolder).toString(), {
			findAccounts: () => [{ accountId: 'oid1.tenant' }, { accountId: 'oid2.tenant' }],
			promptForAccount: async () => undefined,
		});

		assert.strictEqual(repaired, false);
		assert.strictEqual(readConnectionFile(agentFolder).AccountInfo.AccountId, '');
		assertCloudCacheUntouched(agentFolder, cloudCacheBefore);
	});

	test('writes nothing when no signed-in account matches the tenant', async () => {
		const agentFolder = createWorkspaceFolder();
		const cloudCacheBefore = snapshotCloudCache(agentFolder);

		const repaired = await tryRepairAccountInfo(makeSyncInfo(), Uri.file(agentFolder).toString(), { findAccounts: () => [] });

		assert.strictEqual(repaired, false);
		assert.strictEqual(readConnectionFile(agentFolder).AccountInfo.AccountId, '');
		assert.strictEqual(readConnectionFile(agentFolder).AccountInfo.AccountEmail, null);
		assertCloudCacheUntouched(agentFolder, cloudCacheBefore);
	});

	test('prompts with every signed-in account when the tenant is an all-zero guid', async () => {
		const agentFolder = createWorkspaceFolder();
		const cloudCacheBefore = snapshotCloudCache(agentFolder);
		const syncInfo = makeSyncInfo({ tenantId: '00000000-0000-0000-0000-000000000000' });
		const allAccounts = [
			{ accountId: 'oid1.tenant-a', accountEmail: 'first@contoso.com' },
			{ accountId: 'oid2.tenant-b', accountEmail: 'second@fabrikam.com' },
		];
		let promptedWith: StoredAccountSummary[] | undefined;

		const repaired = await tryRepairAccountInfo(syncInfo, Uri.file(agentFolder).toString(), {
			findAccounts: () => [],
			listAllAccounts: () => allAccounts,
			promptForAccount: async candidates => {
				promptedWith = candidates;
				return candidates[1];
			},
		});

		assert.strictEqual(repaired, true);
		assert.deepStrictEqual(promptedWith, allAccounts);
		assert.strictEqual(syncInfo.accountInfo.accountId, 'oid2.tenant-b');
		assert.strictEqual(readConnectionFile(agentFolder).AccountInfo.AccountEmail, 'second@fabrikam.com');
		assertCloudCacheUntouched(agentFolder, cloudCacheBefore);
	});

	test('prompts with every signed-in account when the tenant is missing entirely', async () => {
		const agentFolder = createWorkspaceFolder();
		const allAccounts = [{ accountId: 'oid1.tenant-a' }, { accountId: 'oid2.tenant-b' }];
		let promptedWith: StoredAccountSummary[] | undefined;

		const repaired = await tryRepairAccountInfo(makeSyncInfo({ tenantId: undefined }), Uri.file(agentFolder).toString(), {
			findAccounts: () => [],
			listAllAccounts: () => allAccounts,
			promptForAccount: async candidates => {
				promptedWith = candidates;
				return candidates[0];
			},
		});

		assert.strictEqual(repaired, true);
		assert.deepStrictEqual(promptedWith, allAccounts);
		assert.strictEqual(readConnectionFile(agentFolder).AccountInfo.AccountId, 'oid1.tenant-a');
	});

	test('adopts the sole signed-in account without prompting when the tenant is an all-zero guid', async () => {
		const agentFolder = createWorkspaceFolder();
		let prompted = false;

		const repaired = await tryRepairAccountInfo(
			makeSyncInfo({ tenantId: '00000000-0000-0000-0000-000000000000' }),
			Uri.file(agentFolder).toString(),
			{
				findAccounts: () => [],
				listAllAccounts: () => [{ accountId: 'oid1.tenant-a', accountEmail: 'only@contoso.com' }],
				promptForAccount: async () => {
					prompted = true;
					return undefined;
				},
			});

		assert.strictEqual(repaired, true);
		assert.strictEqual(prompted, false);
		assert.strictEqual(readConnectionFile(agentFolder).AccountInfo.AccountId, 'oid1.tenant-a');
	});

	test('never widens beyond the tenant when the workspace records a real tenant', async () => {
		const agentFolder = createWorkspaceFolder();
		const cloudCacheBefore = snapshotCloudCache(agentFolder);
		let listedAllAccounts = false;

		const repaired = await tryRepairAccountInfo(makeSyncInfo(), Uri.file(agentFolder).toString(), {
			findAccounts: () => [],
			listAllAccounts: () => {
				listedAllAccounts = true;
				return [{ accountId: 'oid.other-tenant', accountEmail: 'wrong@fabrikam.com' }];
			},
			promptForAccount: async () => ({ accountId: 'oid.other-tenant' }),
		});

		assert.strictEqual(repaired, false);
		assert.strictEqual(listedAllAccounts, false);
		assert.strictEqual(readConnectionFile(agentFolder).AccountInfo.AccountId, '');
		assertCloudCacheUntouched(agentFolder, cloudCacheBefore);
	});

	test('writes nothing when the tenant is an all-zero guid and no account is signed in', async () => {
		const agentFolder = createWorkspaceFolder();

		const repaired = await tryRepairAccountInfo(
			makeSyncInfo({ tenantId: '00000000-0000-0000-0000-000000000000' }),
			Uri.file(agentFolder).toString(),
			{ findAccounts: () => [], listAllAccounts: () => [] });

		assert.strictEqual(repaired, false);
		assert.strictEqual(readConnectionFile(agentFolder).AccountInfo.AccountId, '');
		assert.strictEqual(readConnectionFile(agentFolder).AccountInfo.AccountEmail, null);
	});

	test('derives and persists the tenant when an account is already bound but the tenant is missing', async () => {
		const agentFolder = createWorkspaceFolder();
		const cloudCacheBefore = snapshotCloudCache(agentFolder);
		const syncInfo = makeSyncInfo({
			accountId: '674b4cab-fb0c-466a-a81d-5a94243993b7.a30263b9-1caf-4db5-ab53-ed3850c0bd1f',
			accountEmail: 'nguhoa@asdkt4.onmicrosoft.com',
			tenantId: undefined,
		});

		const repaired = await tryRepairAccountInfo(syncInfo, Uri.file(agentFolder).toString(), {
			findAccounts: () => [],
			listAllAccounts: () => [],
		});

		assert.strictEqual(repaired, true);
		assert.strictEqual(syncInfo.accountInfo.tenantId, 'a30263b9-1caf-4db5-ab53-ed3850c0bd1f');
		assert.strictEqual(readConnectionFile(agentFolder).AccountInfo.TenantId, 'a30263b9-1caf-4db5-ab53-ed3850c0bd1f');
		assertCloudCacheUntouched(agentFolder, cloudCacheBefore);
	});

	test('derives the tenant when an account is bound and the tenant is an all-zero guid', async () => {
		const agentFolder = createWorkspaceFolder();
		const syncInfo = makeSyncInfo({
			accountId: '674b4cab-fb0c-466a-a81d-5a94243993b7.a30263b9-1caf-4db5-ab53-ed3850c0bd1f',
			tenantId: '00000000-0000-0000-0000-000000000000',
		});

		await tryRepairAccountInfo(syncInfo, Uri.file(agentFolder).toString(), { findAccounts: () => [] });

		assert.strictEqual(syncInfo.accountInfo.tenantId, 'a30263b9-1caf-4db5-ab53-ed3850c0bd1f');
		assert.strictEqual(readConnectionFile(agentFolder).AccountInfo.TenantId, 'a30263b9-1caf-4db5-ab53-ed3850c0bd1f');
	});

	test('never overwrites a tenant that is already usable', async () => {
		const agentFolder = createWorkspaceFolder();
		const syncInfo = makeSyncInfo({
			accountId: '674b4cab-fb0c-466a-a81d-5a94243993b7.ffffffff-1111-2222-3333-444444444444',
			tenantId: 'a30263b9-1caf-4db5-ab53-ed3850c0bd1f',
		});

		await tryRepairAccountInfo(syncInfo, Uri.file(agentFolder).toString(), { findAccounts: () => [] });

		assert.strictEqual(syncInfo.accountInfo.tenantId, 'a30263b9-1caf-4db5-ab53-ed3850c0bd1f');
	});

	test('leaves the tenant alone when the account id carries no tenant suffix', async () => {
		const agentFolder = createWorkspaceFolder();
		const syncInfo = makeSyncInfo({ accountId: 'no-tenant-suffix', tenantId: undefined });

		const repaired = await tryRepairAccountInfo(syncInfo, Uri.file(agentFolder).toString(), { findAccounts: () => [] });

		assert.strictEqual(repaired, true);
		assert.strictEqual(syncInfo.accountInfo.tenantId, undefined);
	});

	test('records the tenant of the account chosen from the picker', async () => {
		const agentFolder = createWorkspaceFolder();
		const syncInfo = makeSyncInfo({ tenantId: '00000000-0000-0000-0000-000000000000' });
		const allAccounts = [
			{ accountId: 'oid1.11111111-1111-1111-1111-111111111111', accountEmail: 'first@contoso.com' },
			{ accountId: 'oid2.22222222-2222-2222-2222-222222222222', accountEmail: 'second@fabrikam.com' },
		];

		const repaired = await tryRepairAccountInfo(syncInfo, Uri.file(agentFolder).toString(), {
			findAccounts: () => [],
			listAllAccounts: () => allAccounts,
			promptForAccount: async candidates => candidates[1],
		});

		assert.strictEqual(repaired, true);
		assert.strictEqual(syncInfo.accountInfo.tenantId, '22222222-2222-2222-2222-222222222222');

		const persisted = readConnectionFile(agentFolder);
		assert.strictEqual(persisted.AccountInfo.TenantId, '22222222-2222-2222-2222-222222222222');
		assert.strictEqual(persisted.AccountInfo.AccountId, 'oid2.22222222-2222-2222-2222-222222222222');
	});

	test('prompts with the tenant matches when a real tenant holds several accounts', async () => {
		const agentFolder = createWorkspaceFolder();
		const tenantAccounts = [
			{ accountId: 'oid1.a30263b9-1caf-4db5-ab53-ed3850c0bd1f', accountEmail: 'first@contoso.com' },
			{ accountId: 'oid2.a30263b9-1caf-4db5-ab53-ed3850c0bd1f', accountEmail: 'second@contoso.com' },
		];
		let promptedWith: StoredAccountSummary[] | undefined;
		let widened = false;

		const repaired = await tryRepairAccountInfo(makeSyncInfo(), Uri.file(agentFolder).toString(), {
			findAccounts: () => tenantAccounts,
			listAllAccounts: () => {
				widened = true;
				return [{ accountId: 'oid3.other-tenant' }];
			},
			promptForAccount: async candidates => {
				promptedWith = candidates;
				return candidates[0];
			},
		});

		assert.strictEqual(repaired, true);
		assert.strictEqual(widened, false, 'a real tenant with matches must never widen to all accounts');
		assert.deepStrictEqual(promptedWith, tenantAccounts);
	});

	test('reports success and skips discovery when an account is already bound', async () => {
		const agentFolder = createWorkspaceFolder();
		const cloudCacheBefore = snapshotCloudCache(agentFolder);
		let discoveryCalls = 0;

		const repaired = await tryRepairAccountInfo(makeSyncInfo({ accountId: 'already.bound' }), Uri.file(agentFolder).toString(), {
			findAccounts: () => {
				discoveryCalls++;
				return [];
			},
		});

		assert.strictEqual(repaired, true);
		assert.strictEqual(discoveryCalls, 0);
		assert.strictEqual(readConnectionFile(agentFolder).AccountInfo.AccountId, '');
		assertCloudCacheUntouched(agentFolder, cloudCacheBefore);
	});

	test('treats an email-only binding as already resolved', async () => {
		const repaired = await tryRepairAccountInfo(
			makeSyncInfo({ accountEmail: 'dev@contoso.com' }),
			Uri.file(createWorkspaceFolder()).toString(),
			{ findAccounts: () => [] });

		assert.strictEqual(repaired, true);
	});

	test('returns false when the workspace records no account metadata', async () => {
		assert.strictEqual(await tryRepairAccountInfo({} as AgentSyncInfo, Uri.file(createWorkspaceFolder()).toString()), false);
	});

	test('does not retry discovery for a workspace whose repair already failed', async () => {
		const workspaceUri = Uri.file(createWorkspaceFolder()).toString();
		let discoveryCalls = 0;
		const deps = {
			findAccounts: () => {
				discoveryCalls++;
				return [];
			},
		};

		await tryRepairAccountInfo(makeSyncInfo(), workspaceUri, deps);
		await tryRepairAccountInfo(makeSyncInfo(), workspaceUri, deps);

		assert.strictEqual(discoveryCalls, 1);
	});

	test('allows retrying after the account picker is cancelled', async () => {
		const workspaceUri = Uri.file(createWorkspaceFolder()).toString();
		const tenantAccounts = [
			{ accountId: 'oid1.a30263b9-1caf-4db5-ab53-ed3850c0bd1f', accountEmail: 'first@contoso.com' },
			{ accountId: 'oid2.a30263b9-1caf-4db5-ab53-ed3850c0bd1f', accountEmail: 'second@contoso.com' },
		];
		let promptCalls = 0;

		const cancelled = await tryRepairAccountInfo(makeSyncInfo(), workspaceUri, {
			findAccounts: () => tenantAccounts,
			promptForAccount: async () => {
				promptCalls++;
				return undefined;
			},
		});

		const syncInfo = makeSyncInfo();
		const retried = await tryRepairAccountInfo(syncInfo, workspaceUri, {
			findAccounts: () => tenantAccounts,
			promptForAccount: async (matches) => {
				promptCalls++;
				return matches[0];
			},
		});

		assert.strictEqual(cancelled, false);
		assert.strictEqual(retried, true);
		assert.strictEqual(promptCalls, 2);
		assert.strictEqual(syncInfo.accountInfo.accountEmail, 'first@contoso.com');
	});

	test('survives a malformed connection file without throwing', async () => {
		const agentFolder = createWorkspaceFolder('{ not valid json');
		const syncInfo = makeSyncInfo();

		const repaired = await tryRepairAccountInfo(syncInfo, Uri.file(agentFolder).toString(), {
			findAccounts: () => [{ accountId: 'oid.a30263b9-1caf-4db5-ab53-ed3850c0bd1f', accountEmail: 'dev@contoso.com' }],
		});

		assert.strictEqual(repaired, true);
		assert.strictEqual(syncInfo.accountInfo.accountEmail, 'dev@contoso.com');
	});

	test('resolves in memory when the connection file is missing from disk', async () => {
		const syncInfo = makeSyncInfo();

		const repaired = await tryRepairAccountInfo(syncInfo, Uri.file(path.join(os.tmpdir(), 'mcs-missing-workspace')).toString(), {
			findAccounts: () => [{ accountId: 'oid.a30263b9-1caf-4db5-ab53-ed3850c0bd1f', accountEmail: 'dev@contoso.com' }],
		});

		assert.strictEqual(repaired, true);
		assert.strictEqual(syncInfo.accountInfo.accountId, 'oid.a30263b9-1caf-4db5-ab53-ed3850c0bd1f');
	});
});

describe('tryRepairAgentManagementEndpoint', () => {
	const makeEndpointSyncInfo = (): AgentSyncInfo => ({
		environmentId: 'Default-a30263b9-1caf-4db5-ab53-ed3850c0bd1f',
		accountInfo: { accountId: 'oid.tenant', accountEmail: 'dev@contoso.com', tenantId: 'tenant' },
	} as AgentSyncInfo);

	const environmentWithEndpoint = async () => ({
		environmentId: 'Default-a30263b9-1caf-4db5-ab53-ed3850c0bd1f',
		displayName: 'Contoso (default)',
		dataverseUrl: 'https://org82dd85c2.crm.dynamics.com/',
		agentManagementUrl: 'https://powervamg.us-il106.gateway.prod.island.powerapps.com/',
	});

	test('uses the first lookup that returns an endpoint and persists it', async () => {
		const agentFolder = createWorkspaceFolder();
		const cloudCacheBefore = snapshotCloudCache(agentFolder);
		const syncInfo = makeEndpointSyncInfo();

		const repaired = await tryRepairAgentManagementEndpoint(syncInfo, Uri.file(agentFolder).toString(), [environmentWithEndpoint]);

		assert.strictEqual(repaired, true);
		assert.strictEqual(syncInfo.agentManagementEndpoint, 'https://powervamg.us-il106.gateway.prod.island.powerapps.com/');
		assert.strictEqual(readConnectionFile(agentFolder).AgentManagementEndpoint, 'https://powervamg.us-il106.gateway.prod.island.powerapps.com/');
		assertCloudCacheUntouched(agentFolder, cloudCacheBefore);
	});

	test('falls back to the next lookup when the first returns no endpoint', async () => {
		const syncInfo = makeEndpointSyncInfo();

		const repaired = await tryRepairAgentManagementEndpoint(syncInfo, Uri.file(createWorkspaceFolder()).toString(), [
			async () => ({ environmentId: 'e', displayName: 'e', dataverseUrl: 'https://e.crm.dynamics.com/', agentManagementUrl: undefined }),
			environmentWithEndpoint,
		]);

		assert.strictEqual(repaired, true);
		assert.strictEqual(syncInfo.agentManagementEndpoint, 'https://powervamg.us-il106.gateway.prod.island.powerapps.com/');
	});

	test('falls back to the next lookup when the first throws', async () => {
		const syncInfo = makeEndpointSyncInfo();

		const repaired = await tryRepairAgentManagementEndpoint(syncInfo, Uri.file(createWorkspaceFolder()).toString(), [
			async () => { throw new Error('Request failed with status 403'); },
			environmentWithEndpoint,
		]);

		assert.strictEqual(repaired, true);
		assert.strictEqual(syncInfo.agentManagementEndpoint, 'https://powervamg.us-il106.gateway.prod.island.powerapps.com/');
	});

	test('falls back when the first lookup returns null', async () => {
		const syncInfo = makeEndpointSyncInfo();

		const repaired = await tryRepairAgentManagementEndpoint(syncInfo, Uri.file(createWorkspaceFolder()).toString(), [async () => null, environmentWithEndpoint]);

		assert.strictEqual(repaired, true);
	});

	test('writes nothing when every lookup fails', async () => {
		const agentFolder = createWorkspaceFolder();
		const cloudCacheBefore = snapshotCloudCache(agentFolder);
		const syncInfo = makeEndpointSyncInfo();

		const repaired = await tryRepairAgentManagementEndpoint(syncInfo, Uri.file(agentFolder).toString(), [
			async () => { throw new Error('Request failed with status 403'); },
			async () => null,
		]);

		assert.strictEqual(repaired, false);
		assert.strictEqual(syncInfo.agentManagementEndpoint, undefined);
		assert.strictEqual(readConnectionFile(agentFolder).AgentManagementEndpoint, PAC_CONNECTION_FILE.AgentManagementEndpoint);
		assertCloudCacheUntouched(agentFolder, cloudCacheBefore);
	});

	test('repairs an endpoint recorded as null and writes the resolved url over it', async () => {
		const agentFolder = createWorkspaceFolder({ ...PAC_CONNECTION_FILE, AgentManagementEndpoint: null });
		const cloudCacheBefore = snapshotCloudCache(agentFolder);
		const syncInfo = { ...makeEndpointSyncInfo(), agentManagementEndpoint: null } as unknown as AgentSyncInfo;

		assert.strictEqual(readConnectionFile(agentFolder).AgentManagementEndpoint, null);

		const repaired = await tryRepairAgentManagementEndpoint(syncInfo, Uri.file(agentFolder).toString(), [environmentWithEndpoint]);

		assert.strictEqual(repaired, true);
		assert.strictEqual(syncInfo.agentManagementEndpoint, 'https://powervamg.us-il106.gateway.prod.island.powerapps.com/');
		assert.strictEqual(readConnectionFile(agentFolder).AgentManagementEndpoint, 'https://powervamg.us-il106.gateway.prod.island.powerapps.com/');
		assertCloudCacheUntouched(agentFolder, cloudCacheBefore);
	});

	test('repairs an endpoint recorded as an empty string', async () => {
		const agentFolder = createWorkspaceFolder({ ...PAC_CONNECTION_FILE, AgentManagementEndpoint: '' });
		const syncInfo = { ...makeEndpointSyncInfo(), agentManagementEndpoint: '' } as AgentSyncInfo;

		const repaired = await tryRepairAgentManagementEndpoint(syncInfo, Uri.file(agentFolder).toString(), [environmentWithEndpoint]);

		assert.strictEqual(repaired, true);
		assert.strictEqual(syncInfo.agentManagementEndpoint, 'https://powervamg.us-il106.gateway.prod.island.powerapps.com/');
		assert.strictEqual(readConnectionFile(agentFolder).AgentManagementEndpoint, 'https://powervamg.us-il106.gateway.prod.island.powerapps.com/');
	});

	test('repairs an endpoint whose key is missing from conn.json entirely', async () => {
		const { AgentManagementEndpoint, ...withoutEndpoint } = PAC_CONNECTION_FILE;
		const agentFolder = createWorkspaceFolder(withoutEndpoint);
		const syncInfo = makeEndpointSyncInfo();

		const repaired = await tryRepairAgentManagementEndpoint(syncInfo, Uri.file(agentFolder).toString(), [environmentWithEndpoint]);

		assert.strictEqual(repaired, true);
		assert.strictEqual(readConnectionFile(agentFolder).AgentManagementEndpoint, 'https://powervamg.us-il106.gateway.prod.island.powerapps.com/');
	});

	test('leaves a null endpoint as null when every lookup fails', async () => {
		const agentFolder = createWorkspaceFolder({ ...PAC_CONNECTION_FILE, AgentManagementEndpoint: null });
		const syncInfo = { ...makeEndpointSyncInfo(), agentManagementEndpoint: null } as unknown as AgentSyncInfo;

		const repaired = await tryRepairAgentManagementEndpoint(syncInfo, Uri.file(agentFolder).toString(), [async () => null]);

		assert.strictEqual(repaired, false);
		assert.strictEqual(readConnectionFile(agentFolder).AgentManagementEndpoint, null);
	});

	test('preserves every unrelated connection field while filling a null endpoint', async () => {
		const agentFolder = createWorkspaceFolder({ ...PAC_CONNECTION_FILE, AgentManagementEndpoint: null });

		await tryRepairAgentManagementEndpoint(
			{ ...makeEndpointSyncInfo(), agentManagementEndpoint: null } as unknown as AgentSyncInfo,
			Uri.file(agentFolder).toString(),
			[environmentWithEndpoint]);

		const persisted = readConnectionFile(agentFolder);
		assert.strictEqual(persisted.DataverseEndpoint, PAC_CONNECTION_FILE.DataverseEndpoint);
		assert.strictEqual(persisted.AgentId, PAC_CONNECTION_FILE.AgentId);
		assert.deepStrictEqual(persisted.AccountInfo, PAC_CONNECTION_FILE.AccountInfo);
		assert.deepStrictEqual(persisted.UnknownFutureKey, PAC_CONNECTION_FILE.UnknownFutureKey);
	});

	test('reports success and runs no lookup when the endpoint is already present', async () => {
		let lookupCalls = 0;
		const syncInfo = { ...makeEndpointSyncInfo(), agentManagementEndpoint: 'https://already.present/' } as AgentSyncInfo;

		const repaired = await tryRepairAgentManagementEndpoint(syncInfo, Uri.file(createWorkspaceFolder()).toString(), [
			async () => {
				lookupCalls++;
				return null;
			},
		]);

		assert.strictEqual(repaired, true);
		assert.strictEqual(lookupCalls, 0);
	});

	test('does not retry lookups for a workspace whose repair already failed', async () => {
		const workspaceUri = Uri.file(createWorkspaceFolder()).toString();
		let lookupCalls = 0;
		const failingLookup = async () => {
			lookupCalls++;
			return null;
		};

		await tryRepairAgentManagementEndpoint(makeEndpointSyncInfo(), workspaceUri, [failingLookup]);
		await tryRepairAgentManagementEndpoint(makeEndpointSyncInfo(), workspaceUri, [failingLookup]);

		assert.strictEqual(lookupCalls, 1);
	});
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
