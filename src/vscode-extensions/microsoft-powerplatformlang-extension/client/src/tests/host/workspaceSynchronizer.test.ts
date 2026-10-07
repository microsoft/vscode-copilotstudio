import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import {
	createSyncSuccessLog,
	getActiveSyncUri,
	getOrAddSynchronizer,
	getSyncStateFor,
	logSyncConflicts,
	logWorkflowIssues,
	onAnySyncStateChanged,
	removeSynchronizer,
	sync,
	SyncState,
	withSyncCommandBusy,
} from '../../sync/workspaceSynchronizer';
import { resolveWorkspaceArg } from '../../commands/syncWorkspace';
import logger, { formatFileName, formatPii, prepareLogData, sanitizeErrorDetails } from '../../services/logger';
import type { WorkflowResponse } from '../../types';

import { ThemeIcon } from 'vscode';
import { WorkspaceType, type CopilotStudioWorkspace } from '../../sync/localWorkspaces';

function createMockWorkspace(agentName: string): CopilotStudioWorkspace {
	return {
		workspaceUri: 'file:///c%3A/tmp',
		displayName: agentName,
		description: '',
		icon: new ThemeIcon('hubot'),
		type: WorkspaceType.Agent,
	};
}

describe('workspaceSynchronizer: sync success telemetry', () => {

	test('keeps the agent name visible while redacting it from telemetry', () => {
		const successLog = createSyncSuccessLog(createMockWorkspace('Contoso Support'), 'applying changes', 42);
		const prepared = prepareLogData(successLog.message, {
			sessionId: 'test-session',
			agentId: successLog.data.agentId,
			syncOperation: successLog.data.syncOperation,
		});

		assert.strictEqual(prepared.displayMessage, 'Completed applying changes for Contoso Support');
		assert.strictEqual(prepared.telemetryProperties.message, 'Completed applying changes for [REDACTED AGENT NAME]');
		assert.strictEqual(prepared.telemetryProperties.syncOperation, 'applying changes');
	});

	test('does not allow PII values to close their redaction marker', () => {
		const agentName = 'Contoso </pii> alice@contoso.com';
		const successLog = createSyncSuccessLog(createMockWorkspace(agentName), 'applying changes', 42);
		const prepared = prepareLogData(successLog.message, {
			sessionId: 'test-session',
			agentId: successLog.data.agentId,
		});

		assert.strictEqual(prepared.displayMessage, `Completed applying changes for ${agentName}`);
		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Completed applying changes for [REDACTED AGENT NAME]',
		);
	});

	test('identifies an MCS YAML file name while preserving a safe file error', () => {
		const message = `Error opening file ${formatFileName('C:\\agents\\contoso\\agent.mcs.yml')}: ${sanitizeErrorDetails('Access denied')}`;
		const prepared = prepareLogData(message, {
			sessionId: 'test-session',
			message,
		});

		assert.strictEqual(
			prepared.displayMessage,
			'Error opening file C:\\agents\\contoso\\agent.mcs.yml: Access denied',
		);
		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Error opening file [REDACTED .MCS.YML FILE NAME]: Access denied',
		);
	});

	test('preserves filesystem error details for the user while redacting unsafe qualifiers', () => {
		const message = `Error opening file ${formatFileName('C:\\agents\\contoso\\agent.mcs.yml')}: ${sanitizeErrorDetails('Access denied by policy Contoso-Restricted')}`;
		const prepared = prepareLogData(message, { sessionId: 'test-session' });

		assert.strictEqual(
			prepared.displayMessage,
			'Error opening file C:\\agents\\contoso\\agent.mcs.yml: Access denied by policy Contoso-Restricted',
		);
		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Error opening file [REDACTED .MCS.YML FILE NAME]: Access denied by policy Contoso-Restricted',
		);
	});

	test('preserves a file error reason while redacting its agent and MCS YAML file', () => {
		const message = `Error opening file ${formatFileName('C:\\agents\\contoso\\agent.mcs.yml')}: ${sanitizeErrorDetails('Agent Contoso could not open C:\\agents\\contoso\\topic.mcs.yml', ['Contoso'])}`;
		const prepared = prepareLogData(message, { sessionId: 'test-session' });

		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Error opening file [REDACTED .MCS.YML FILE NAME]: Agent [REDACTED AGENT NAME] could not open [REDACTED .MCS.YML FILE NAME]',
		);
	});

	test('identifies an email address while preserving a safe rejection reason', () => {
		const message = `Re-authentication failed: ${sanitizeErrorDetails('alex@contoso.com was rejected')}`;
		const prepared = prepareLogData(message, { sessionId: 'test-session' });

		assert.strictEqual(prepared.displayMessage, 'Re-authentication failed: alex@contoso.com was rejected');
		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Re-authentication failed: [REDACTED EMAIL ADDRESS] was rejected',
		);
	});

	test('preserves a re-authentication reason while redacting its PII', () => {
		const message = `Re-authentication failed: ${sanitizeErrorDetails('Agent Contoso could not open C:\\agents\\contoso\\agent.mcs.yml for alex@contoso.com', ['Contoso'])}`;
		const prepared = prepareLogData(message, { sessionId: 'test-session' });

		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Re-authentication failed: Agent [REDACTED AGENT NAME] could not open [REDACTED .MCS.YML FILE NAME] for [REDACTED EMAIL ADDRESS]',
		);
	});

	test('sanitizes recognized PII while preserving the complete error reason', () => {
		const errorMessage = 'Agent Contoso Support could not open C:\\agents\\contoso\\topic.mcs.yaml for alex@contoso.com';
		const protectedMessage = sanitizeErrorDetails(errorMessage, ['Contoso Support']);
		const prepared = prepareLogData(protectedMessage, { sessionId: 'test-session' });

		assert.strictEqual(prepared.displayMessage, errorMessage);
		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Agent [REDACTED AGENT NAME] could not open [REDACTED .MCS.YAML FILE NAME] for [REDACTED EMAIL ADDRESS]',
		);
	});

	test('leaves non-PII error details unchanged', () => {
		const errorMessage = 'Request timed out after 30 seconds';
		const prepared = prepareLogData(sanitizeErrorDetails(errorMessage), { sessionId: 'test-session' });

		assert.strictEqual(prepared.displayMessage, errorMessage);
		assert.strictEqual(prepared.telemetryProperties.message, errorMessage);
	});

	test('redacts complete emails with apostrophes and Windows paths with spaces', () => {
		const errorMessage = "Could not open C:\\Users\\John Doe\\agent.mcs.yml for o'connor@example.com";
		const prepared = prepareLogData(sanitizeErrorDetails(errorMessage), { sessionId: 'test-session' });

		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Could not open [REDACTED .MCS.YML FILE NAME] for [REDACTED EMAIL ADDRESS]',
		);
	});

	test('redacts complete UNC MCS YAML paths with spaces', () => {
		const errorMessage = 'Could not open \\\\server\\John Doe\\agent.mcs.yml';
		const prepared = prepareLogData(sanitizeErrorDetails(errorMessage), { sessionId: 'test-session' });

		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Could not open [REDACTED .MCS.YML FILE NAME]',
		);
	});

	test('redacts full URLs as a URL rather than mislabeling them as file names', () => {
		const errorMessage = 'Cannot reach https://contoso.crm.dynamics.com/api endpoint';
		const prepared = prepareLogData(sanitizeErrorDetails(errorMessage), { sessionId: 'test-session' });

		assert.strictEqual(prepared.displayMessage, errorMessage);
		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Cannot reach [REDACTED URL] endpoint',
		);
	});

	test('redacts non-MCS file paths while disclosing the file extension', () => {
		const errorMessage = 'ENOENT: spawn C:\\Users\\alex\\.vscode\\extensions\\lspOut\\LanguageServerHost.exe';
		const prepared = prepareLogData(sanitizeErrorDetails(errorMessage), { sessionId: 'test-session' });

		assert.strictEqual(prepared.displayMessage, errorMessage);
		assert.strictEqual(
			prepared.telemetryProperties.message,
			'ENOENT: spawn [REDACTED .EXE FILE NAME]',
		);
	});

	test('redacts cached botdefinition paths disclosing the .json extension', () => {
		const errorMessage = 'Failed reading C:\\Users\\alex\\agents\\contoso\\.mcs\\botdefinition.json';
		const prepared = prepareLogData(sanitizeErrorDetails(errorMessage), { sessionId: 'test-session' });

		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Failed reading [REDACTED .JSON FILE NAME]',
		);
	});

	test('redacts bare file names disclosing the file extension', () => {
		const errorMessage = 'Could not parse settings.mcs.yml';
		const prepared = prepareLogData(sanitizeErrorDetails(errorMessage), { sessionId: 'test-session' });

		assert.strictEqual(
			prepared.telemetryProperties.message,
			'Could not parse [REDACTED .MCS.YML FILE NAME]',
		);
	});

	test('uses original-string indices for Unicode agent-name matching', () => {
		const errorMessage = 'İssue for Contoso failed';
		const prepared = prepareLogData(sanitizeErrorDetails(errorMessage, ['Contoso']), {
			sessionId: 'test-session',
		});

		assert.strictEqual(
			prepared.telemetryProperties.message,
			'İssue for [REDACTED AGENT NAME] failed',
		);
	});

	test('does not redact short agent names inside words or status clauses as agent names', () => {
		const errorMessage = 'Validation failed. Agent failed to start.';
		const prepared = prepareLogData(sanitizeErrorDetails(errorMessage, ['AI']), {
			sessionId: 'test-session',
		});

		assert.strictEqual(prepared.telemetryProperties.message, errorMessage);
	});

	test('redacts multiline sensitive values without changing display text', () => {
		const message = 'Sync failed: <pii>Agent Contoso\nC:\\agents\\contoso</pii>';
		const prepared = prepareLogData(message, {
			sessionId: 'test-session',
			error: '<pii>Agent Contoso\nC:\\agents\\contoso</pii>',
		});

		assert.strictEqual(prepared.displayMessage, 'Sync failed: Agent Contoso\nC:\\agents\\contoso');
		assert.strictEqual(prepared.telemetryProperties.message, 'Sync failed: [REDACTED]');
		assert.strictEqual(prepared.telemetryProperties.error, '[REDACTED]');
	});

	test('does not re-wrap content a caller already tagged', () => {
		const tagged = `Could not parse ${formatFileName('topics/Goodbye.mcs.yml')}`;

		assert.strictEqual(sanitizeErrorDetails(tagged), tagged);
	});

	test('keeps a workspace validation error readable while redacting its file and detail', () => {
		const errorMessage = [
			'1 workspace file could not be read:',
			`  ${formatPii('topics/Jane Doe Onboarding.mcs.yml', 'WORKSPACE FILE DETAILS')}(12,5): ${formatPii("Duplicate key 'customer-private-key'.", 'WORKSPACE FILE DETAILS')}`,
		].join('\n');
		const prepared = prepareLogData('Failed to execute Apply operation', {
			sessionId: 'test-session',
			errorMessage: sanitizeErrorDetails(errorMessage),
		});

		assert.ok(prepared.displayMessage?.includes("topics/Jane Doe Onboarding.mcs.yml(12,5): Duplicate key 'customer-private-key'."));
		assert.strictEqual(
			prepared.telemetryProperties.errorMessage,
			'1 workspace file could not be read:\n  [REDACTED WORKSPACE FILE DETAILS](12,5): [REDACTED WORKSPACE FILE DETAILS]',
		);
	});
});

/**
 * Tests for the command-level busy tracking exposed by workspaceSynchronizer.
 *
 * Covers the "isSyncing" model that drives the `mcs.isSyncing` context key
 * and the per-workspace spinner / auto-expand behavior in the Agent Changes
 * tree view.
 */
describe('workspaceSynchronizer: withSyncCommandBusy', () => {

	test('getActiveSyncUri is undefined when no sync is running', () => {
		assert.strictEqual(getActiveSyncUri(), undefined);
	});

	test('getActiveSyncUri returns the workspace uri while body runs, undefined after', async () => {
		const uri = 'file:///test/agent-a';
		assert.strictEqual(getActiveSyncUri(), undefined);

		let observedDuringBody: string | undefined;
		await withSyncCommandBusy(uri, async () => {
			observedDuringBody = getActiveSyncUri();
		});

		assert.strictEqual(observedDuringBody, uri);
		assert.strictEqual(getActiveSyncUri(), undefined);
	});

	test('getActiveSyncUri is cleared when body throws', async () => {
		const uri = 'file:///test/agent-throws';
		await assert.rejects(
			withSyncCommandBusy(uri, async () => {
				assert.strictEqual(getActiveSyncUri(), uri);
				throw new Error('boom');
			}),
			/boom/,
		);
		assert.strictEqual(getActiveSyncUri(), undefined);
	});

	test('withSyncCommandBusy returns the body result', async () => {
		const result = await withSyncCommandBusy('file:///test/agent-result', async () => 42);
		assert.strictEqual(result, 42);
	});

	test('onAnySyncStateChanged fires at start and end of withSyncCommandBusy', async () => {
		const events: (string | undefined)[] = [];
		const sub = onAnySyncStateChanged(() => events.push(getActiveSyncUri()));
		try {
			await withSyncCommandBusy('file:///test/agent-event', async () => { /* no-op */ });
		} finally {
			sub.dispose();
		}
		// Expect at least one "start" event with the uri set, then a "clear" event.
		assert.ok(events.includes('file:///test/agent-event'), `expected start event, got ${JSON.stringify(events)}`);
		assert.strictEqual(events[events.length - 1], undefined, 'last event should observe cleared state');
	});

	test('throws when re-entered while another sync is in progress', async () => {
		const outerUri = 'file:///test/agent-outer';
		const innerUri = 'file:///test/agent-inner';
		await assert.rejects(
			withSyncCommandBusy(outerUri, async () => {
				await withSyncCommandBusy(innerUri, async () => { /* unreachable */ });
			}),
			/sync is already in progress/i,
		);
		// Outer's finally still runs, clearing the state.
		assert.strictEqual(getActiveSyncUri(), undefined);
	});

	test('same-uri re-entry also throws (no implicit reuse)', async () => {
		const uri = 'file:///test/agent-same';
		await assert.rejects(
			withSyncCommandBusy(uri, async () => {
				await withSyncCommandBusy(uri, async () => { /* unreachable */ });
			}),
			/sync is already in progress/i,
		);
		assert.strictEqual(getActiveSyncUri(), undefined);
	});
});

describe('workspaceSynchronizer: getSyncStateFor', () => {

	test('returns Idle for an unknown workspace uri', () => {
		assert.strictEqual(getSyncStateFor('file:///nonexistent/workspace'), SyncState.Idle);
	});

	test('returns Idle for a workspace not currently syncing', () => {
		// withSyncCommandBusy alone does not create a per-workspace synchronizer
		// entry; that only happens via getOrAddSynchronizer. So a workspace that
		// has only been wrapped by withSyncCommandBusy should still report Idle.
		assert.strictEqual(getSyncStateFor('file:///test/agent-never-synced'), SyncState.Idle);
	});
});

describe('workspaceSynchronizer: SyncState enum', () => {

	test('SyncState.Idle is the default / zero value', () => {
		assert.strictEqual(SyncState.Idle, 0);
	});

	test('SyncState has distinct values for each operation', () => {
		const values = new Set([SyncState.Idle, SyncState.Fetching, SyncState.Pulling, SyncState.Pushing]);
		assert.strictEqual(values.size, 4, 'all SyncState values must be distinct');
	});
});

describe('workspaceSynchronizer: logWorkflowIssues', () => {

	function captureLogs(run: () => void): { warnings: string[]; errors: string[] } {		const warnings: string[] = [];
		const errors: string[] = [];
		const originalWarn = logger.logWarning;
		const originalError = logger.logError;
		logger.logWarning = ((_event: unknown, message?: string) => { if (message) { warnings.push(message); } }) as typeof logger.logWarning;
		logger.logError = ((_event: unknown, message?: string) => { if (message) { errors.push(message); } }) as typeof logger.logError;
		try {
			run();
		} finally {
			logger.logWarning = originalWarn;
			logger.logError = originalError;
		}
		return { warnings, errors };
	}

	test('reports a failed workflow as an error even when a disabled workflow is present', () => {
		const workflows: WorkflowResponse[] = [
			{ workflowId: '11111111-1111-1111-1111-111111111111', workflowName: 'Draft WF', isDisabled: true },
			{ workflowId: '22222222-2222-2222-2222-222222222222', workflowName: 'Bad WF', isDisabled: true, errorMessage: 'Failed to update workflow: boom' },
		];

		let returnedHasErrors = false;
		const { warnings, errors } = captureLogs(() => { returnedHasErrors = logWorkflowIssues(workflows); });

		assert.strictEqual(returnedHasErrors, true, 'logWorkflowIssues must return true when a workflow error is present');
		assert.strictEqual(errors.length, 1, `expected one error log, got ${JSON.stringify(errors)}`);
		assert.ok(errors[0].includes('Bad WF: Failed to update workflow: boom'), errors[0]);
		assert.strictEqual(warnings.length, 1, `expected one warning log, got ${JSON.stringify(warnings)}`);
		assert.ok(warnings[0].includes('Draft WF'), warnings[0]);
	});

	test('a workflow that is only disabled with no error is reported as a warning, not an error', () => {
		const workflows: WorkflowResponse[] = [
			{ workflowId: '11111111-1111-1111-1111-111111111111', workflowName: 'Draft Only', isDisabled: true },
		];

		let returnedHasErrors = true;
		const { warnings, errors } = captureLogs(() => { returnedHasErrors = logWorkflowIssues(workflows); });

		assert.strictEqual(returnedHasErrors, false, 'logWorkflowIssues must return false when there are no workflow errors');
		assert.strictEqual(errors.length, 0, `expected no error log, got ${JSON.stringify(errors)}`);
		assert.strictEqual(warnings.length, 1);
		assert.ok(warnings[0].includes('Draft Only'));
	});

	test('suppressDisabledWarnings hides the disabled warning but still logs errors', () => {
		const workflows: WorkflowResponse[] = [
			{ workflowId: '11111111-1111-1111-1111-111111111111', workflowName: 'Draft WF', isDisabled: true },
			{ workflowId: '22222222-2222-2222-2222-222222222222', workflowName: 'Bad WF', isDisabled: true, errorMessage: 'Failed to update workflow: boom' },
		];

		let returnedHasErrors = false;
		const { warnings, errors } = captureLogs(() => { returnedHasErrors = logWorkflowIssues(workflows, true); });

		assert.strictEqual(returnedHasErrors, true, 'errors must still be reported (returned) when warnings are suppressed');
		assert.strictEqual(warnings.length, 0, `expected no warning when suppressed, got ${JSON.stringify(warnings)}`);
		assert.strictEqual(errors.length, 1, `errors must still log when warnings are suppressed, got ${JSON.stringify(errors)}`);
		assert.ok(errors[0].includes('Bad WF: Failed to update workflow: boom'), errors[0]);
	});

	test('workflows sharing a display name are reported separately and stay distinguishable by id', () => {
		const workflows: WorkflowResponse[] = [
			{ workflowId: '11111111-1111-1111-1111-111111111111', workflowName: 'Shared', isDisabled: true },
			{ workflowId: '22222222-2222-2222-2222-222222222222', workflowName: 'Shared', isDisabled: true },
		];

		const { warnings } = captureLogs(() => { logWorkflowIssues(workflows); });

		assert.strictEqual(warnings.length, 1, `expected one warning log, got ${JSON.stringify(warnings)}`);
		assert.strictEqual((warnings[0].match(/Shared/g) ?? []).length, 2, warnings[0]);

		const byId = new Map(workflows.map(workflow => [workflow.workflowId, workflow]));
		assert.strictEqual(byId.size, 2);
	});

	test('reports a pull that left merge conflicts as a warning', () => {
		const message = 'Resolve them before pushing:\n  <pii type="WORKSPACE FILE DETAILS" encoded="true">topics/Jane Doe.mcs.yml</pii>(3,1)';

		let reported = false;
		const { warnings } = captureLogs(() => { reported = logSyncConflicts(message); });

		assert.strictEqual(reported, true);
		assert.strictEqual(warnings.length, 1, `expected one warning log, got ${JSON.stringify(warnings)}`);
		assert.strictEqual(warnings[0], message);
	});

	test('does not warn when the pull reported no conflicts', () => {
		let reported = true;
		const emptyWarnings = captureLogs(() => { reported = logSyncConflicts(''); }).warnings;

		assert.strictEqual(reported, false);
		assert.strictEqual(emptyWarnings.length, 0);
		assert.strictEqual(logSyncConflicts(undefined), false);
	});

	test('keeps conflicted file names out of telemetry while showing them to the user', () => {
		const message = 'Resolve them before pushing:\n  <pii type="WORKSPACE FILE DETAILS" encoded="true">topics/Jane Doe.mcs.yml</pii>(3,1)';

		const prepared = prepareLogData(message, { sessionId: 'test-session' });

		assert.ok(prepared.displayMessage?.includes('topics/Jane Doe.mcs.yml'), prepared.displayMessage);
		assert.ok(!prepared.telemetryProperties.message.includes('Jane Doe'), prepared.telemetryProperties.message);
		assert.ok(prepared.telemetryProperties.message.includes('[REDACTED WORKSPACE FILE DETAILS]'), prepared.telemetryProperties.message);
	});
});

describe('workspaceSynchronizer: workspace binding', () => {
	const bindingUri = 'file:///c%3A/tmp/binding-agent';

	const workspaceWithAccount = (accountId: string): CopilotStudioWorkspace => ({
		...createMockWorkspace('Binding Agent'),
		workspaceUri: bindingUri,
		syncInfo: { accountInfo: { accountId, accountEmail: undefined, tenantId: '' } } as any,
	});

	const captureOverrideAccountInfo = async (
		accountOverride: Parameters<typeof sync>[9]
	): Promise<Record<string, unknown>> => {
		const localWorkspaces = require('../../sync/localWorkspaces') as typeof import('../../sync/localWorkspaces');
		const lspClient = require('../../services/lspClient') as typeof import('../../services/lspClient');
		const originalRefreshSyncInfo = localWorkspaces.refreshSyncInfoFromConnection;
		const originalRepairAccountInfo = localWorkspaces.repairAccountInfo;
		const captureSnapshotDescriptor = Object.getOwnPropertyDescriptor(localWorkspaces, 'captureWorkspaceConnectionSnapshot');
		const buildPayloadDescriptor = Object.getOwnPropertyDescriptor(lspClient, 'buildLspRequestPayload');
		const persistedSyncInfo = {
			accountInfo: {
				accountId: 'persisted-account-id',
				accountEmail: 'persisted@example.com',
				tenantId: 'persisted-tenant-id',
				clusterCategory: 2,
			},
			agentManagementEndpoint: 'https://api.example.com',
			dataverseEndpoint: 'https://org.example.com',
			environmentId: 'environment-id',
		} as NonNullable<CopilotStudioWorkspace['syncInfo']>;
		const workspace = {
			...createMockWorkspace('Override Agent'),
			syncInfo: persistedSyncInfo,
		};
		let capturedAccountInfo: Record<string, unknown> | undefined;

		localWorkspaces.repairAccountInfo = async () => 'already-bound';
		localWorkspaces.refreshSyncInfoFromConnection = () => persistedSyncInfo;
		Object.defineProperty(localWorkspaces, 'captureWorkspaceConnectionSnapshot', {
			value: () => 'connection-snapshot',
			writable: true,
			configurable: true,
		});
		Object.defineProperty(lspClient, 'buildLspRequestPayload', {
			value: async (syncInfo: CopilotStudioWorkspace['syncInfo']) => {
				capturedAccountInfo = syncInfo?.accountInfo as unknown as Record<string, unknown>;
				throw new Error('captured override identity');
			},
			writable: true,
			configurable: true,
		});

		try {
			await assert.rejects(
				() => sync(workspace, 'Refresh', 'test/sync', true, false, false, false, false, true, accountOverride),
				/captured override identity/,
			);
			assert.ok(capturedAccountInfo);
			return capturedAccountInfo;
		} finally {
			localWorkspaces.repairAccountInfo = originalRepairAccountInfo;
			localWorkspaces.refreshSyncInfoFromConnection = originalRefreshSyncInfo;
			if (captureSnapshotDescriptor) {
				Object.defineProperty(localWorkspaces, 'captureWorkspaceConnectionSnapshot', captureSnapshotDescriptor);
			}
			if (buildPayloadDescriptor) {
				Object.defineProperty(lspClient, 'buildLspRequestPayload', buildPayloadDescriptor);
			}
		}
	};

	test('does not retain a persisted account id or tenant for an email-only override', async () => {
		const accountInfo = await captureOverrideAccountInfo({
			accountEmail: 'selected@example.com',
		});

		assert.deepStrictEqual(accountInfo, {
			accountId: undefined,
			accountEmail: 'selected@example.com',
			tenantId: '',
			clusterCategory: 2,
		});
	});

	test('does not retain a persisted email or tenant for an id-only override', async () => {
		const accountInfo = await captureOverrideAccountInfo({
			accountId: 'selected-account-id',
		});

		assert.deepStrictEqual(accountInfo, {
			accountId: 'selected-account-id',
			accountEmail: undefined,
			tenantId: '',
			clusterCategory: 2,
		});
	});

	test('retains persisted identity for an override containing only account metadata', async () => {
		const accountInfo = await captureOverrideAccountInfo({
			clusterCategory: 3,
		});

		assert.deepStrictEqual(accountInfo, {
			accountId: 'persisted-account-id',
			accountEmail: 'persisted@example.com',
			tenantId: 'persisted-tenant-id',
			clusterCategory: 3,
		});
	});

	test('adopts the latest workspace object instead of keeping the one it was created with', () => {
		removeSynchronizer(bindingUri);
		const stale = workspaceWithAccount('');
		const created = getOrAddSynchronizer(stale);

		const repaired = workspaceWithAccount('chosen.tenant');
		const reused = getOrAddSynchronizer(repaired);

		assert.strictEqual(reused, created, 'the cached synchronizer instance must be reused');
		assert.strictEqual(reused.workspace, repaired);
		assert.strictEqual(reused.workspace.syncInfo?.accountInfo.accountId, 'chosen.tenant');
		removeSynchronizer(bindingUri);
	});

	test('retries account repair once when a concurrent startup repair changes the connection', async () => {
		const localWorkspaces = require('../../sync/localWorkspaces') as typeof import('../../sync/localWorkspaces');
		const originalRepairAccountInfo = localWorkspaces.repairAccountInfo;
		let repairCalls = 0;
		localWorkspaces.repairAccountInfo = async () => {
			repairCalls++;
			return repairCalls === 1 ? 'stale' : 'inaccessible';
		};

		try {
			await assert.rejects(
				() => sync(workspaceWithAccount(''), 'Preview', 'test/sync', true),
				/Select an account with access, or add it if it is not listed/,
			);
			assert.strictEqual(repairCalls, 2);
		} finally {
			localWorkspaces.repairAccountInfo = originalRepairAccountInfo;
		}
	});

	test('starts out reporting no failed operation', () => {
		removeSynchronizer(bindingUri);
		const synchronizer = getOrAddSynchronizer(workspaceWithAccount('chosen.tenant'));

		assert.strictEqual(synchronizer.lastOperationSucceeded, true);
		assert.strictEqual(synchronizer.syncState, SyncState.Idle);
		removeSynchronizer(bindingUri);
	});

	test('reports a failed operation so callers can skip follow-up refreshes', async () => {
		removeSynchronizer(bindingUri);
		const synchronizer = getOrAddSynchronizer(workspaceWithAccount(''));
		const observed: { state: SyncState; succeeded: boolean }[] = [];
		synchronizer.subscribe(state => { observed.push({ state, succeeded: synchronizer.lastOperationSucceeded }); });

		await assert.rejects(() => synchronizer.fetch());

		assert.strictEqual(synchronizer.lastOperationSucceeded, false);
		const idleTransition = observed.find(entry => entry.state === SyncState.Idle);
		assert.ok(idleTransition, 'listeners must still see the return to Idle');
		assert.strictEqual(idleTransition.succeeded, false, 'the Idle listener must be able to see the failure');
		removeSynchronizer(bindingUri);
	});
});

describe('syncWorkspace: command argument resolution', () => {
	const workspace = createMockWorkspace('Arg Agent');

	test('resolves the workspace supplied by an explicit row command', () => {
		assert.strictEqual(resolveWorkspaceArg({ ws: workspace }), workspace);
	});

	test('resolves the tree element supplied by an inline or context menu', () => {
		assert.strictEqual(resolveWorkspaceArg({ kind: 1, workspace } as any), workspace);
	});

	test('resolves a bare workspace object', () => {
		assert.strictEqual(resolveWorkspaceArg(workspace), workspace);
	});

	test('returns undefined for palette invocation so the caller falls back to the picker', () => {
		assert.strictEqual(resolveWorkspaceArg(undefined), undefined);
		assert.strictEqual(resolveWorkspaceArg(null), undefined);
		assert.strictEqual(resolveWorkspaceArg({} as any), undefined);
	});
});
