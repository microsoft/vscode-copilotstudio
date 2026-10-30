import * as assert from 'node:assert';
import { afterEach, describe, test } from 'node:test';
import * as vscode from 'vscode';
import {
	askRemoteChangeResolution,
	createSyncSuccessLog,
	getActiveSyncUri,
	getOrAddSynchronizer,
	getSyncStateFor,
	holdsUnpushedLocalChanges,
	logSyncConflicts,
	logWorkflowIssues,
	onAnySyncStateChanged,
	RemoteOverwriteRequestedError,
	removeSynchronizer,
	reportSyncError,
	sync,
	SyncState,
	withSyncCommandBusy,
} from '../../sync/workspaceSynchronizer';
import { registerSyncCommands, requestsRemoteOverwrite, resolveWorkspaceArg } from '../../commands/syncWorkspace';
import { AIPromptSchemaNamePrefix, CustomConnectorSchemaNamePrefix, LspMethods } from '../../constants';
import logger, { formatFileName, formatPii, PiiRedactionType, prepareLogData, sanitizeErrorDetails, stripPiiTags } from '../../services/logger';
import { LspRequestError, LSP_UNPUSHED_LOCAL_CHANGES } from '../../services/lspClient';
import type { SyncRequest, WorkflowResponse } from '../../types';
import { replace, restoreReplaced } from './helpers/replace';

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

const workflowSyncInfo = {
	accountInfo: { accountId: 'account-id', accountEmail: 'user@example.com', tenantId: 'tenant-id' },
	agentManagementEndpoint: 'https://api.example.com',
	dataverseEndpoint: 'https://org.example.com',
	environmentId: 'environment-id',
} as NonNullable<CopilotStudioWorkspace['syncInfo']>;

const createWorkflowWorkspace = (folderName: string): CopilotStudioWorkspace => ({
	...createMockWorkspace(folderName),
	workspaceUri: `file:///c%3A/tmp/${folderName}`,
	type: WorkspaceType.StandaloneWorkflow,
	syncInfo: workflowSyncInfo,
});

const runProgressInline = () => replace(vscode.window as any, 'withProgress', async (_options: unknown, task: () => Promise<unknown>) => await task());

const replaceLanguageServer = (sendRequest: (method: string, request: SyncRequest) => Promise<unknown>) => {
	replace(require('../../sync/localWorkspaces'), 'repairAccountInfo', async () => 'already-bound');
	replace(require('../../sync/localWorkspaces'), 'refreshSyncInfoFromConnection', () => workflowSyncInfo);
	replace(require('../../services/lspClient'), 'buildLspRequestPayload', async () => ({ accountInfo: workflowSyncInfo.accountInfo }));
	replace(require('../../services/lspClient'), 'lspClient', { sendRequest });
};

const arrangeSyncCommands = () => {
	const handlers = new Map<string, (arg?: unknown) => Promise<void>>();
	replace(vscode.commands as any, 'registerCommand', (id: string, handler: (arg?: unknown) => Promise<void>) => {
		handlers.set(id, handler);
		return { dispose: () => undefined };
	});
	runProgressInline();
	replace(vscode.workspace as any, 'findFiles', async () => []);
	replace(require('../../knowledgeFiles/virtualKnowledgeFile'), 'registerVirtualKnowledgeProvider', async () => undefined);
	registerSyncCommands({ subscriptions: [] } as unknown as vscode.ExtensionContext);
	return async (command: string, arg: unknown) => {
		const handler = handlers.get(command);
		assert.ok(handler, `${command} was not registered`);
		await handler(arg);
	};
};

describe('workspaceSynchronizer: unpushed local workflow changes', () => {
	test('recognises the refusal the server returns for unpushed local edits', () => {
		const refusal = new LspRequestError(LSP_UNPUSHED_LOCAL_CHANGES, "'My Flow-1' holds changes the environment has not seen.");

		assert.strictEqual(holdsUnpushedLocalChanges(refusal), true);
		assert.strictEqual(refusal.code, 409);
		assert.ok(refusal instanceof Error);
		assert.strictEqual(refusal.message, "'My Flow-1' holds changes the environment has not seen.");
	});

	test('does not treat another server failure as a local-change refusal', () => {
		assert.strictEqual(holdsUnpushedLocalChanges(new LspRequestError(400, 'Bad request')), false);
		assert.strictEqual(holdsUnpushedLocalChanges(new LspRequestError(500, 'Server error')), false);
	});

	test('does not treat a plain error or a non-error as a local-change refusal', () => {
		assert.strictEqual(holdsUnpushedLocalChanges(new Error('boom')), false);
		assert.strictEqual(holdsUnpushedLocalChanges('409'), false);
		assert.strictEqual(holdsUnpushedLocalChanges(undefined), false);
	});

	test('a server-tagged folder name reads plainly for the user but is redacted in telemetry', () => {
		const tagged = `${formatPii('MyFlow-a1b2', PiiRedactionType.WorkflowNames)} holds changes the environment has not seen.`;

		assert.strictEqual(stripPiiTags(tagged), 'MyFlow-a1b2 holds changes the environment has not seen.');
		assert.strictEqual(
			prepareLogData(undefined, { error: sanitizeErrorDetails(tagged, [], PiiRedactionType.WorkflowNames) }).telemetryProperties['error'],
			'[REDACTED WORKFLOW NAMES] holds changes the environment has not seen.');
	});

	test('the LSP middleware boundary redacts a tagged refusal even though it knows no workflow names', () => {
		const serverMessage = `${formatPii('PayrollApprovals-a1b2', PiiRedactionType.WorkflowNames)} holds changes the environment has not seen.`;
		const refusal = new LspRequestError(LSP_UNPUSHED_LOCAL_CHANGES, serverMessage);

		const logged = prepareLogData(undefined, { errorMessage: sanitizeErrorDetails(refusal.message) });

		assert.strictEqual(logged.telemetryProperties['errorMessage'], '[REDACTED WORKFLOW NAMES] holds changes the environment has not seen.');
		assert.strictEqual(stripPiiTags(refusal.message), 'PayrollApprovals-a1b2 holds changes the environment has not seen.');
	});

	test('an untagged folder the server derived from a punctuated name reaches telemetry', () => {
		const refusal = new LspRequestError(LSP_UNPUSHED_LOCAL_CHANGES, "'PayrollApprovals-a1b2' holds changes the environment has not seen.");

		assert.ok(prepareLogData(undefined, { errorMessage: sanitizeErrorDetails(refusal.message) })
			.telemetryProperties['errorMessage']
			.includes('PayrollApprovals-a1b2'));
	});
});

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

describe('workspaceSynchronizer: remote change conflicts', () => {
	const workflowWorkspace = createWorkflowWorkspace('conflict-flow');
	const agentWorkspace = { ...workflowWorkspace, type: WorkspaceType.Agent };
	const refusal = `'${formatPii('MyFlow-1', PiiRedactionType.WorkflowNames)}' has conflict local changes, and a pull would replace them.`;
	const workflowChange = { schemaName: 'Mcs.Workflow.4f1d7c2e-8b3a-4c5d-9e6f-0a1b2c3d4e5f' };
	const topicChange = { schemaName: 'cre98_agent.topic.Greeting' };
	const connectorChange = { schemaName: `${CustomConnectorSchemaNamePrefix}6f1c2b3a-4d5e-4f60-8a9b-0c1d2e3f4a5b` };
	const promptChange = { schemaName: `${AIPromptSchemaNamePrefix}0b6e1f7c-2a3d-4e5f-9a8b-7c6d5e4f3a2b` };

	let prompts: { message: string; options: vscode.MessageOptions; items: string[] }[];
	let requests: { method: string; request: SyncRequest }[];

	const arrange = (answer?: string) => {
		prompts = [];
		requests = [];
		replaceLanguageServer(async (method: string, request: SyncRequest) => {
			requests.push({ method, request });
			if (method === LspMethods.SYNC_PULL && !request.overwriteLocalChanges) {
				throw new LspRequestError(LSP_UNPUSHED_LOCAL_CHANGES, refusal);
			}
			return { localChanges: [] };
		});
		runProgressInline();
		replace(vscode.window as any, 'showWarningMessage', async (message: string, options: vscode.MessageOptions, ...items: string[]) => {
			prompts.push({ message, options, items });
			return answer;
		});
		replace(logger as any, 'logInfo', () => undefined);
		replace(logger as any, 'logWarning', () => undefined);
		replace(logger as any, 'logError', () => undefined);
		removeSynchronizer(workflowWorkspace.workspaceUri);
	};

	const pull = (offerRemoteOverwrite?: boolean) => getOrAddSynchronizer(workflowWorkspace).pull(undefined as any, { offerRemoteOverwrite });

	afterEach(() => {
		restoreReplaced();
		removeSynchronizer(workflowWorkspace.workspaceUri);
	});

	test('a push requires that nothing is left to pull only when asked', async () => {
		arrange();
		const synchronizer = getOrAddSynchronizer(workflowWorkspace);

		await synchronizer.push({ requireNoUnpulledChanges: true });
		await synchronizer.push();

		assert.deepStrictEqual(requests.map(({ method, request }) => [method, request.requireNoUnpulledChanges]), [
			[LspMethods.SYNC_PUSH, true],
			[LspMethods.SYNC_PUSH, false],
		]);
	});

	test('a refused Get that may overwrite the cloud offers it and hands the overwrite to the caller', async () => {
		arrange('Overwrite cloud changes');

		await assert.rejects(() => pull(true), RemoteOverwriteRequestedError);

		assert.strictEqual(requests.length, 1);
		assert.deepStrictEqual(prompts, [{
			message: "'MyFlow-1' has conflict local changes, and a pull would replace them.",
			options: {
				modal: true,
				detail: "Replace local changes discards your edits and gets the cloud copy. Overwrite cloud changes applies your edits and discards the cloud's.",
			},
			items: ['Replace local changes', 'Overwrite cloud changes'],
		}]);
	});

	test('a refused Get that may overwrite the cloud still replaces the local changes when the user picks that', async () => {
		arrange('Replace local changes');

		await pull(true);

		assert.deepStrictEqual(requests.map(({ method, request }) => [method, request.overwriteLocalChanges, request.requireNoUnpulledChanges]), [
			[LspMethods.SYNC_PULL, false, false],
			[LspMethods.SYNC_PULL, true, false],
		]);
		assert.strictEqual(prompts.length, 1);
	});

	test('a refused Get that may overwrite the cloud keeps the local changes when the dialog is dismissed', async () => {
		arrange();

		await assert.rejects(() => pull(true), (error: unknown) => holdsUnpushedLocalChanges(error));

		assert.strictEqual(requests.length, 1);
	});

	test('a refused Get that may not overwrite the cloud offers only replacing the local changes', async () => {
		arrange();

		await assert.rejects(() => pull(), (error: unknown) => holdsUnpushedLocalChanges(error));

		assert.deepStrictEqual(prompts.map(({ options, items }) => [options.detail, items]), [
			['Getting the cloud copy discards these local changes. Apply them first to keep them.', ['Replace local changes']],
		]);
	});

	const getOrOverwriteDetail = 'Get Remote Changes replaces your local copies with the cloud copies. Overwrite remote changes replaces the cloud copies with your local copies.';

	test('remote changes to a workflow can be got or overwritten and the dialog explains both', async () => {
		arrange('Overwrite remote changes');

		const resolution = await askRemoteChangeResolution(workflowWorkspace, [workflowChange], []);

		assert.strictEqual(resolution, 'overwriteRemoteChanges');
		assert.deepStrictEqual(prompts, [{
			message: 'The agent has 1 remote change that must be retrieved or overwritten before your local changes can be applied.',
			options: { modal: true, detail: getOrOverwriteDetail },
			items: ['Get Remote Changes', 'Overwrite remote changes'],
		}]);
	});

	test('remote changes to an agent that are all connectors or prompts the workspace also changed can be got or overwritten and the dialog explains both', async () => {
		arrange('Overwrite remote changes');

		const resolution = await askRemoteChangeResolution(agentWorkspace, [connectorChange, promptChange], [{ schemaName: `${connectorChange.schemaName}.openapidefinition` }, promptChange]);

		assert.strictEqual(resolution, 'overwriteRemoteChanges');
		assert.deepStrictEqual(prompts, [{
			message: 'The agent has 2 remote changes that must be retrieved or overwritten before your local changes can be applied.',
			options: { modal: true, detail: getOrOverwriteDetail },
			items: ['Get Remote Changes', 'Overwrite remote changes'],
		}]);
	});

	test('remote changes to an agent that include a connector the workspace did not change can only be got', async () => {
		arrange('Get Remote Changes');

		const resolution = await askRemoteChangeResolution(agentWorkspace, [connectorChange, promptChange], [promptChange]);

		assert.strictEqual(resolution, 'getRemoteChanges');
		assert.deepStrictEqual(prompts, [{
			message: 'The agent has 2 remote changes that must be retrieved before your local changes can be applied.',
			options: { modal: true },
			items: ['Get Remote Changes'],
		}]);
	});

	test('remote changes to an agent that include a component can only be got', async () => {
		arrange('Get Remote Changes');

		const resolution = await askRemoteChangeResolution(agentWorkspace, [topicChange, connectorChange], [topicChange, connectorChange]);

		assert.strictEqual(resolution, 'getRemoteChanges');
		assert.deepStrictEqual(prompts, [{
			message: 'The agent has 2 remote changes that must be retrieved before your local changes can be applied.',
			options: { modal: true },
			items: ['Get Remote Changes'],
		}]);
	});

	test('a dismissed remote change dialog resolves nothing', async () => {
		arrange();

		assert.strictEqual(await askRemoteChangeResolution(workflowWorkspace, [workflowChange], []), undefined);
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

	test('reads a request to overwrite remote changes only from an explicit true flag', () => {
		assert.strictEqual(requestsRemoteOverwrite({ ws: workspace, overwriteRemoteChanges: true }), true);
		assert.strictEqual(requestsRemoteOverwrite({ ws: workspace }), false);
		assert.strictEqual(requestsRemoteOverwrite({ ws: workspace, overwriteRemoteChanges: 'true' }), false);
		assert.strictEqual(requestsRemoteOverwrite(workspace), false);
		assert.strictEqual(requestsRemoteOverwrite(undefined), false);
		assert.strictEqual(requestsRemoteOverwrite(null), false);
	});
});

describe('syncWorkspace: overwriting remote workflow changes', () => {
	const workflowWorkspace = createWorkflowWorkspace('command-flow');
	const overwritingApply = ['microsoft-copilot-studio.applyChanges', { ws: workflowWorkspace, overwriteRemoteChanges: true }];
	const checkedPush = ['push', { suppressDisabledWorkflowWarnings: true, requireNoUnpulledChanges: true }];
	const overwritingPush = ['push', { suppressDisabledWorkflowWarnings: true, requireNoUnpulledChanges: false }];
	const offeringPull = ['pull', { offerRemoteOverwrite: true }];

	let run: (command: string, arg: unknown) => Promise<void>;
	let calls: unknown[][];
	let executed: unknown[][];
	let prompts: string[][];
	let errors: (string | undefined)[];

	const arrange = ({ remoteChanges = 0, answer, pullFailure }: { remoteChanges?: number; answer?: string; pullFailure?: Error } = {}) => {
		calls = [];
		executed = [];
		prompts = [];
		errors = [];
		replace(vscode.commands as any, 'executeCommand', async (...args: unknown[]) => { executed.push(args); });
		replace(vscode.window as any, 'showWarningMessage', async (_message: string, _options: vscode.MessageOptions, ...items: string[]) => {
			prompts.push(items);
			return answer;
		});
		replace(require('../../sync/workspaceScm'), 'getWorkspaceChanges', () => ({
			localChanges: [],
			remoteChanges: Array.from({ length: remoteChanges }, () => ({ changeKind: 'Workflow' })),
		}));
		replace(require('../../sync/workspaceSynchronizer'), 'getOrAddSynchronizer', () => ({
			workspace: workflowWorkspace,
			fetch: async () => { calls.push(['fetch']); },
			pull: async (_provider: unknown, options: unknown) => {
				calls.push(['pull', options]);
				if (pullFailure) {
					throw pullFailure;
				}
			},
			push: async (options: unknown) => { calls.push(['push', options]); },
		}));
		replace(logger as any, 'logInfo', () => undefined);
		replace(logger as any, 'logWarning', () => undefined);
		replace(logger as any, 'logError', (_event: string, message?: string) => { errors.push(message); });
		run = arrangeSyncCommands();
	};

	afterEach(restoreReplaced);

	test('an Apply asked to overwrite remote changes pushes without checking for them first', async () => {
		arrange({ remoteChanges: 1 });

		await run('microsoft-copilot-studio.applyChanges', { ws: workflowWorkspace, overwriteRemoteChanges: true });

		assert.deepStrictEqual(calls, [overwritingPush]);
		assert.deepStrictEqual(prompts, []);
	});

	test('an Apply with nothing to get pushes while requiring that nothing is left to pull', async () => {
		arrange();

		await run('microsoft-copilot-studio.applyChanges', { ws: workflowWorkspace });

		assert.deepStrictEqual(calls, [['fetch'], checkedPush]);
	});

	test('an Apply over remote workflow changes overwrites them when the user picks that', async () => {
		arrange({ remoteChanges: 1, answer: 'Overwrite remote changes' });

		await run('microsoft-copilot-studio.applyChanges', { ws: workflowWorkspace });

		assert.deepStrictEqual(prompts, [['Get Remote Changes', 'Overwrite remote changes']]);
		assert.deepStrictEqual(calls, [['fetch'], overwritingPush]);
	});

	test('an Apply over remote workflow changes gets them instead of pushing when the user picks that', async () => {
		arrange({ remoteChanges: 1, answer: 'Get Remote Changes' });

		await run('microsoft-copilot-studio.applyChanges', { ws: workflowWorkspace });

		assert.deepStrictEqual(calls, [['fetch'], offeringPull]);
	});

	test('an Apply over remote workflow changes does nothing when the dialog is dismissed', async () => {
		arrange({ remoteChanges: 1 });

		await run('microsoft-copilot-studio.applyChanges', { ws: workflowWorkspace });

		assert.deepStrictEqual(calls, [['fetch']]);
		assert.deepStrictEqual(executed, []);
	});

	test('a Get the user turns into a cloud overwrite reruns Apply as an overwrite', async () => {
		arrange({ pullFailure: new RemoteOverwriteRequestedError() });

		await run('microsoft-copilot-studio.getChanges', { ws: workflowWorkspace });

		assert.deepStrictEqual(calls, [offeringPull]);
		assert.deepStrictEqual(executed, [overwritingApply]);
		assert.deepStrictEqual(errors, []);
	});

	test('a Get from the Apply check that the user turns into a cloud overwrite reruns Apply as an overwrite', async () => {
		arrange({ remoteChanges: 1, answer: 'Get Remote Changes', pullFailure: new RemoteOverwriteRequestedError() });

		await run('microsoft-copilot-studio.applyChanges', { ws: workflowWorkspace });

		assert.deepStrictEqual(calls, [['fetch'], offeringPull]);
		assert.deepStrictEqual(executed, [overwritingApply]);
		assert.deepStrictEqual(errors, []);
	});

	test('a failed Get is reported rather than turned into an overwrite', async () => {
		arrange({ pullFailure: new Error('Getting changes failed.') });

		await run('microsoft-copilot-studio.getChanges', { ws: workflowWorkspace });

		assert.deepStrictEqual(executed, []);
		assert.deepStrictEqual(errors, ['Failed to execute Get operation']);
	});
});

describe('sync commands: showing a failure once', () => {
	const workflowWorkspace = createWorkflowWorkspace('once-flow');
	const draftRefusal = "'S5D1 WF 1' has a draft version that was saved in Copilot Studio but not published, and the environment refuses to apply changes while it exists. Publish the draft in Copilot Studio, then use Get changes to get it, or Apply changes again and choose Overwrite remote changes to replace it.";

	let run: (command: string, arg: unknown) => Promise<void>;
	let errors: (string | undefined)[][];
	let warnings: (string | undefined)[];

	const arrange = (failures: Record<string, Error[]>) => {
		errors = [];
		warnings = [];
		replaceLanguageServer(async (method: string) => {
			const failure = failures[method]?.shift();
			if (failure) {
				throw failure;
			}
			return { localChanges: [] };
		});
		replace(vscode.window as any, 'showWarningMessage', async () => undefined);
		replace(require('../../clients/account'), 'resetAccount', () => undefined);
		replace(logger as any, 'logInfo', () => undefined);
		replace(logger as any, 'logWarning', (_event: string, message?: string) => { warnings.push(message); });
		replace(logger as any, 'logError', (_event: string, message?: string, data?: { message?: string }) => { errors.push([message, data?.message]); });
		removeSynchronizer(workflowWorkspace.workspaceUri);
		run = arrangeSyncCommands();
	};

	afterEach(() => {
		restoreReplaced();
		removeSynchronizer(workflowWorkspace.workspaceUri);
	});

	test('a refused Apply shows the refusal once and still records the failed command', async () => {
		arrange({ [LspMethods.SYNC_PUSH]: [new LspRequestError(400, draftRefusal)] });

		await run('microsoft-copilot-studio.applyChanges', { ws: workflowWorkspace });

		assert.deepStrictEqual(errors, [
			['Error applying changes', 'Error applying changes'],
			[undefined, 'Failed to execute Apply operation'],
		]);
	});

	test('a Get the user cancels to keep local changes is not reported again as an error', async () => {
		arrange({ [LspMethods.SYNC_PULL]: [new LspRequestError(LSP_UNPUSHED_LOCAL_CHANGES, "'MyFlow-1' has conflict local changes, and a pull would replace them.")] });

		await run('microsoft-copilot-studio.getChanges', { ws: workflowWorkspace });

		assert.deepStrictEqual(warnings, ['getting changes was cancelled to keep local changes.']);
		assert.deepStrictEqual(errors, [[undefined, 'Failed to execute Get operation']]);
	});

	test('an Apply that fails again after signing in shows that failure once', async () => {
		arrange({ [LspMethods.SYNC_PUSH]: [new Error('UserNotMemberOfOrg'), new LspRequestError(400, draftRefusal)] });

		await run('microsoft-copilot-studio.applyChanges', { ws: workflowWorkspace });

		assert.deepStrictEqual(errors, [
			[`Your current account does not have permission. Please sign in with the account ${formatPii('(user@example.com)', PiiRedactionType.AccountIdentifier)} to perform this operation.`, undefined],
			['Error applying changes', 'Error applying changes'],
			[undefined, 'Re-authentication failed'],
			[undefined, 'Failed to execute Apply operation'],
		]);
	});

	test('a failure the sync code kept quiet is still shown by the caller that reports it', async () => {
		arrange({ [LspMethods.SYNC_PUSH]: [new LspRequestError(400, draftRefusal)] });
		const failure = await getOrAddSynchronizer(workflowWorkspace).push({ suppressErrorNotification: true }).catch((error: unknown) => error);

		reportSyncError('Failed to execute Apply operation', failure);

		assert.deepStrictEqual(errors, [
			[undefined, 'Error applying changes'],
			['Failed to execute Apply operation', 'Failed to execute Apply operation'],
		]);
	});

	test('a failure that is not an error object is shown every time it is reported', () => {
		arrange({});

		reportSyncError('Error applying changes', 'refused');
		reportSyncError('Failed to execute Apply operation', 'refused');

		assert.deepStrictEqual(errors.map(([message]) => message), ['Error applying changes', 'Failed to execute Apply operation']);
	});
});
