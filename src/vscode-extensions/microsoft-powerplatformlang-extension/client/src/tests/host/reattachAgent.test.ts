import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import * as vscode from 'vscode';
import * as accountModule from '../../clients/account';
import * as bapClientModule from '../../clients/bapClient';
import * as connectionManagerModule from '../../connections/connectionManager';
import { executeReattachAgentCommand } from '../../commands/reattachAgent';
import { TelemetryEventsKeys } from '../../constants';
import * as accountEnvPickerModule from '../../services/accountEnvPicker';
import * as lspClientModule from '../../services/lspClient';
import logger, { formatPii, PiiRedactionType } from '../../services/logger';
import * as virtualKnowledgeModule from '../../knowledgeFiles/virtualKnowledgeFile';
import {
	CopilotStudioWorkspace,
	WorkspaceType,
} from '../../sync/localWorkspaces';
import * as localWorkspacesModule from '../../sync/localWorkspaces';
import * as workspaceScmModule from '../../sync/workspaceScm';
import { PullOptions } from '../../sync/workspaceSynchronizer';
import * as workspaceSynchronizerModule from '../../sync/workspaceSynchronizer';
import * as syncWorkspaceModule from '../../commands/syncWorkspace';
import {
	AccountInfo,
	EnvironmentInfo,
	ReattachAgentResponse,
} from '../../types';

type Restore = () => void;

type LogRecord = {
	level: 'info' | 'warning' | 'error';
	event: unknown;
	message: unknown;
	data: unknown;
};

class FakeQuickPick {
	title: string | undefined;
	placeholder: string | undefined;
	ignoreFocusOut = false;
	busy = false;
	buttons: readonly vscode.QuickInputButton[] = [];
	items: readonly vscode.QuickPickItem[] = [];
	selectedItems: readonly vscode.QuickPickItem[] = [];
	showCount = 0;
	disposed = false;
	onShow: ((quickPick: FakeQuickPick) => void) | undefined;

	private readonly hideListeners: Array<() => void> = [];
	private readonly buttonListeners: Array<(button: vscode.QuickInputButton) => void> = [];
	private readonly acceptListeners: Array<() => void> = [];

	onDidHide(listener: () => void): vscode.Disposable {
		return this.addListener(this.hideListeners, listener);
	}

	onDidTriggerButton(listener: (button: vscode.QuickInputButton) => void): vscode.Disposable {
		return this.addListener(this.buttonListeners, listener);
	}

	onDidAccept(listener: () => void): vscode.Disposable {
		return this.addListener(this.acceptListeners, listener);
	}

	show(): void {
		this.showCount++;
		this.onShow?.(this);
	}

	dispose(): void {
		this.disposed = true;
	}

	fireHide(): void {
		for (const listener of [...this.hideListeners]) {
			listener();
		}
	}

	fireButton(button: vscode.QuickInputButton): void {
		for (const listener of [...this.buttonListeners]) {
			listener(button);
		}
	}

	fireAccept(): void {
		for (const listener of [...this.acceptListeners]) {
			listener();
		}
	}

	private addListener<T>(listeners: T[], listener: T): vscode.Disposable {
		listeners.push(listener);
		return {
			dispose: () => {
				const index = listeners.indexOf(listener);
				if (index >= 0) {
					listeners.splice(index, 1);
				}
			},
		};
	}
}

const stubProperty = (
	target: object,
	key: PropertyKey,
	replacement: unknown
): Restore => {
	const descriptor = Object.getOwnPropertyDescriptor(target, key);
	Object.defineProperty(target, key, {
		value: replacement,
		writable: true,
		configurable: true,
	});
	return () => {
		if (descriptor) {
			Object.defineProperty(target, key, descriptor);
		} else {
			delete (target as Record<PropertyKey, unknown>)[key];
		}
	};
};

type FlowHarnessOptions = {
	attached?: boolean;
	activeSyncUri?: string;
	currentEnvironmentId?: string;
	targetEnvironmentId?: string;
	accounts?: AccountInfo[];
	workspaceAccount?: AccountInfo;
	configureQuickPick?: (quickPick: FakeQuickPick) => void;
	switchAccount?: () => Promise<void>;
	listEnvironments?: (account: AccountInfo) => Promise<EnvironmentInfo[]>;
	withSyncCommandBusy?: <T>(workspaceUri: string, action: () => Promise<T>) => Promise<T>;
	sendRequest?: (
		method: string,
		request: Record<string, unknown>
	) => Promise<unknown>;
	pushNewWorkspace?: () => Promise<void>;
	pull?: (options: PullOptions) => Promise<void>;
	autoBindAgentConnections?: () => Promise<{
		needsNewCount: number;
		boundCount: number;
		enabledWorkflowCount: number;
		disabledWorkflowNames: string[];
	}>;
	promptManageConnectionsForWorkspaces?: () => Promise<void>;
	showWarningMessage?: (
		message: string,
		optionsOrItem: vscode.MessageOptions | string,
		...items: string[]
	) => Thenable<string | undefined>;
	getDiagnosticsErrors?: () => Promise<{ count: number; files: number }>;
	executeCommand?: (command: string) => Promise<void>;
	onLog?: (level: LogRecord['level'], message: unknown) => void;
};

const installFlowHarness = (options: FlowHarnessOptions = {}) => {
	const restores: Restore[] = [];
	const logs: LogRecord[] = [];
	const requests: Array<{ method: string; request: Record<string, unknown> }> = [];
	const progressOptions: vscode.ProgressOptions[] = [];
	const events: string[] = [];
	const targetEnvironmentId = options.targetEnvironmentId ?? 'target-environment';
	const currentEnvironmentId = options.currentEnvironmentId ?? 'current-environment';
	const account: AccountInfo = {
		accountId: 'account-id',
		accountEmail: 'developer@example.com',
		tenantId: 'tenant-id',
	};
	const accounts = options.accounts ?? [account];
	const workspaceAccount = options.workspaceAccount ?? account;
	const environment = {
		environmentId: targetEnvironmentId,
		displayName: 'Target Environment',
	} as unknown as EnvironmentInfo;
	const workspace: CopilotStudioWorkspace = {
		workspaceUri: vscode.Uri.file('C:\\reattach-flow-test\\Test Agent').toString(),
		displayName: 'Test Agent',
		description: '',
		icon: undefined as unknown as vscode.ThemeIcon,
		type: WorkspaceType.Agent,
		syncInfo: options.attached
			? ({
					agentId: 'existing-agent-id',
					environmentId: currentEnvironmentId,
					accountInfo: workspaceAccount,
				} as CopilotStudioWorkspace['syncInfo'])
			: undefined,
	};
	const quickPick = new FakeQuickPick();
	options.configureQuickPick?.(quickPick);
	if (!quickPick.onShow) {
		quickPick.onShow = picker => {
			picker.selectedItems = [picker.items[0]];
			picker.fireAccept();
		};
	}

	const addStub = (target: object, key: PropertyKey, replacement: unknown) => {
		restores.push(stubProperty(target, key, replacement));
	};

	addStub(vscode.window, 'createQuickPick', () => quickPick as unknown as vscode.QuickPick<vscode.QuickPickItem>);
	addStub(
		vscode.window,
		'withProgress',
		async (progress: vscode.ProgressOptions, task: () => Promise<unknown>) => {
			progressOptions.push(progress);
			return await task();
		}
	);
	addStub(
		vscode.window,
		'showWarningMessage',
		async (
			message: string,
			optionsOrItem: vscode.MessageOptions | string,
			...items: string[]
		) => {
			if (options.showWarningMessage) {
				return await options.showWarningMessage(message, optionsOrItem, ...items);
			}
			return typeof optionsOrItem === 'string' ? optionsOrItem : items[0];
		}
	);
	addStub(vscode.commands, 'executeCommand', async (command: string) => {
		await options.executeCommand?.(command);
	});
	addStub(accountModule, 'listStoredAccounts', async () => accounts);
	addStub(accountModule, 'switchAccount', options.switchAccount ?? (async () => undefined));
	addStub(accountModule, 'getPreferredTreeAccount', () => account);
	addStub(accountModule, 'clearAuthAccountState', () => undefined);
	addStub(
		bapClientModule,
		'listEnvironmentsAsync',
		async (
			_clusterCategory: unknown,
			_filter: unknown,
			accountId: string | null,
			accountEmail: string
		) => await (
			options.listEnvironments?.({
				accountId: accountId ?? undefined,
				accountEmail,
				tenantId: '',
			}) ?? Promise.resolve([environment])
		)
	);
	addStub(accountEnvPickerModule, 'buildEnvironmentPickItems', (_environments: EnvironmentInfo[], sourceAccount: AccountInfo) => [
		{
			label: 'Target Environment',
			environment,
			sourceAccount,
		},
	]);
	addStub(localWorkspacesModule, 'hasConnectionFileInWorkspace', () => options.attached ?? false);
	addStub(localWorkspacesModule, 'getAllWorkspaces', () => [workspace]);
	addStub(lspClientModule, 'buildLspRequestPayload', async () => ({}));
	addStub(
		lspClientModule.default,
		'_client',
		{
			sendRequest: async (method: string, request: Record<string, unknown>) => {
			requests.push({ method, request });
			if (options.sendRequest) {
				return await options.sendRequest(method, request);
			}
			return {
				code: 200,
				message: '',
				agentSyncInfo: {
					agentId: 'reattached-agent-id',
					environmentId: targetEnvironmentId,
					accountInfo: account,
				},
				requiresLocalPush: false,
			} as ReattachAgentResponse;
			},
		}
	);
	addStub(workspaceScmModule, 'pushNewWorkspace', options.pushNewWorkspace ?? (async () => undefined));
	addStub(
		workspaceSynchronizerModule,
		'getActiveSyncUri',
		() => options.activeSyncUri
	);
	addStub(
		workspaceSynchronizerModule,
		'withSyncCommandBusy',
		options.withSyncCommandBusy ??
			(async <T>(_workspaceUri: string, action: () => Promise<T>) => await action())
	);
	addStub(
		workspaceSynchronizerModule,
		'getOrAddSynchronizer',
		() => ({
			pull: async (_virtualProvider: unknown, pullOptions: PullOptions) =>
				await (options.pull?.(pullOptions) ?? Promise.resolve()),
		})
	);
	addStub(
		virtualKnowledgeModule,
		'registerVirtualKnowledgeProvider',
		async () => ({})
	);
	addStub(
		syncWorkspaceModule,
		'getDiagnosticsErrors',
		options.getDiagnosticsErrors ?? (async () => ({
			count: 0,
			files: 0,
		}))
	);
	addStub(
		connectionManagerModule,
		'autoBindAgentConnections',
		options.autoBindAgentConnections ?? (async () => ({
			needsNewCount: 0,
			boundCount: 0,
			enabledWorkflowCount: 0,
			disabledWorkflowNames: [],
		}))
	);
	addStub(
		connectionManagerModule,
		'promptManageConnectionsForWorkspaces',
		async () => {
			events.push('prompt-connections');
			await (options.promptManageConnectionsForWorkspaces?.() ?? Promise.resolve());
		}
	);
	addStub(
		logger,
		'logInfo',
		(event: unknown, message: unknown, data: unknown) => {
			logs.push({ level: 'info', event, message, data });
			options.onLog?.('info', message);
			if (typeof message === 'string') {
				events.push('terminal-success');
			}
		}
	);
	addStub(
		logger,
		'logWarning',
		(event: unknown, message: unknown, data: unknown) => {
			logs.push({ level: 'warning', event, message, data });
			options.onLog?.('warning', message);
		}
	);
	addStub(
		logger,
		'logError',
		(event: unknown, message: unknown, data: unknown) => {
			logs.push({ level: 'error', event, message, data });
			options.onLog?.('error', message);
		}
	);

	return {
		workspace,
		quickPick,
		logs,
		requests,
		progressOptions,
		events,
		context: { subscriptions: [] } as unknown as vscode.ExtensionContext,
		restore: () => {
			for (const restore of restores.reverse()) {
				restore();
			}
		},
	};
};

describe('executeReattachAgentCommand', () => {
	test('completes the full reattach flow after authentication temporarily hides the picker', async () => {
		let switchAccountCalled = false;
		let pickerDisposedWhileAuthenticationWasActive = false;
		const harness = installFlowHarness({
			switchAccount: async () => {
				switchAccountCalled = true;
			},
			configureQuickPick: quickPick => {
				quickPick.onShow = picker => {
					if (picker.showCount === 1) {
						picker.fireButton(picker.buttons[0]);
						picker.fireHide();
						pickerDisposedWhileAuthenticationWasActive = picker.disposed;
						return;
					}

					picker.selectedItems = [picker.items[0]];
					picker.fireAccept();
				};
			},
		});

		try {
			await executeReattachAgentCommand(harness.context, { workspace: harness.workspace });

			assert.strictEqual(switchAccountCalled, true);
			assert.strictEqual(pickerDisposedWhileAuthenticationWasActive, false);
			assert.strictEqual(harness.quickPick.showCount, 2);
			assert.strictEqual(harness.quickPick.disposed, true);
			assert.strictEqual(harness.requests.length, 1);
			assert.deepStrictEqual(harness.progressOptions, [{
				location: vscode.ProgressLocation.Notification,
				title: 'Reattaching Agent...',
				cancellable: false,
			}]);
			assert.strictEqual(harness.logs.filter(log => log.level === 'error').length, 0);
			assert.strictEqual(harness.logs.filter(log => log.level === 'warning').length, 0);
			assert.strictEqual(
				harness.logs.filter(log => log.level === 'info' && typeof log.message === 'string').length,
				1,
				'the command should emit exactly one terminal success message'
			);
		} finally {
			harness.restore();
		}
	});

	test('logs exactly one cancellation when the environment picker is dismissed', async () => {
		const harness = installFlowHarness({
			configureQuickPick: quickPick => {
				quickPick.onShow = picker => picker.fireHide();
			},
		});

		try {
			await executeReattachAgentCommand(harness.context, { workspace: harness.workspace });

			assert.strictEqual(harness.requests.length, 0);
			assert.strictEqual(harness.logs.filter(log => log.level === 'error').length, 0);
			const cancellationLogs = harness.logs.filter(log => log.level === 'warning');
			assert.strictEqual(cancellationLogs.length, 1);
			assert.deepStrictEqual(cancellationLogs[0].data, {
				message: 'Reattach agent canceled before an environment was selected.',
			});
		} finally {
			harness.restore();
		}
	});

	test('surfaces the account-transition timeout when selecting an account', async () => {
		const originalSetTimeout = globalThis.setTimeout;
		const originalClearTimeout = globalThis.clearTimeout;
		const harness = installFlowHarness({
			accounts: [
				{
					accountId: 'account-a',
					accountEmail: 'a@example.com',
					tenantId: 'tenant-a',
				},
				{
					accountId: 'account-b',
					accountEmail: 'b@example.com',
					tenantId: 'tenant-b',
				},
			],
			listEnvironments: async () => await new Promise<EnvironmentInfo[]>(() => undefined),
		});
		globalThis.setTimeout = ((callback: (...args: unknown[]) => void) => {
			queueMicrotask(callback);
			return 1 as unknown as NodeJS.Timeout;
		}) as typeof globalThis.setTimeout;
		globalThis.clearTimeout = (() => undefined) as typeof globalThis.clearTimeout;

		try {
			await executeReattachAgentCommand(harness.context, { workspace: harness.workspace });

			assert.strictEqual(harness.requests.length, 0);
			assert.strictEqual(harness.logs.filter(log => log.level === 'warning').length, 0);
			const errorLogs = harness.logs.filter(log => log.level === 'error');
			assert.strictEqual(errorLogs.length, 1);
			assert.strictEqual(
				errorLogs[0].message,
				'Account and environment selection did not complete within 2 minutes. Please try again.'
			);
		} finally {
			globalThis.setTimeout = originalSetTimeout;
			globalThis.clearTimeout = originalClearTimeout;
			harness.restore();
		}
	});

	test('reports a sync-lock race as one terminal error instead of silently rejecting', async () => {
		const harness = installFlowHarness({
			withSyncCommandBusy: async () => {
				throw new Error('A sync is already in progress');
			},
		});

		try {
			await executeReattachAgentCommand(harness.context, { workspace: harness.workspace });

			assert.strictEqual(harness.requests.length, 0);
			assert.strictEqual(harness.logs.filter(log => log.level === 'warning').length, 0);
			const errorLogs = harness.logs.filter(log => log.level === 'error');
			assert.strictEqual(errorLogs.length, 1);
			assert.strictEqual(errorLogs[0].message, 'Error reattaching agent');
		} finally {
			harness.restore();
		}
	});

	test('preserves the existing warning when a sync is already in progress', async () => {
		const harness = installFlowHarness({
			activeSyncUri: 'file:///active-sync-workspace',
		});

		try {
			await executeReattachAgentCommand(harness.context, { workspace: harness.workspace });

			assert.strictEqual(harness.requests.length, 0);
			assert.strictEqual(harness.logs.filter(log => log.level === 'error').length, 0);
			const warningLogs = harness.logs.filter(log => log.level === 'warning');
			assert.strictEqual(warningLogs.length, 1);
			assert.strictEqual(warningLogs[0].event, TelemetryEventsKeys.ReattachAgentWarning);
			assert.strictEqual(
				warningLogs[0].message,
				'A sync is already in progress. Please wait for it to finish before retargeting an agent.'
			);
		} finally {
			harness.restore();
		}
	});

	test('continues through normal reattach when the exact connected remote agent is missing', async () => {
		const harness = installFlowHarness({
			attached: true,
			currentEnvironmentId: 'same-environment',
			targetEnvironmentId: 'same-environment',
			sendRequest: async (_method, request) => {
				if (request.checkRemoteAgentOnly === true) {
					return {
						code: 200,
						message: '',
						remoteAgentExists: false,
					};
				}
				if (request.pushSucceeded !== undefined) {
					return {};
				}
				return {
					code: 200,
					message: '',
					agentSyncInfo: {
						agentId: 'replacement-agent-id',
						environmentId: 'same-environment',
					},
					requiresLocalPush: false,
				};
			},
		});

		try {
			await executeReattachAgentCommand(harness.context, { workspace: harness.workspace });

			assert.strictEqual(harness.requests.length, 3);
			assert.strictEqual(harness.requests[0].request.checkRemoteAgentOnly, true);
			assert.strictEqual(harness.requests[1].request.checkRemoteAgentOnly, undefined);
			assert.strictEqual(harness.requests[2].request.pushSucceeded, true);
			assert.strictEqual(harness.logs.filter(log => log.level === 'error').length, 0);
			const successLogs = harness.logs.filter(
				log => log.level === 'info' && typeof log.message === 'string'
			);
			assert.strictEqual(successLogs.length, 1);
			assert.strictEqual(
				successLogs[0].message,
				`Agent ${formatPii(
					'Test Agent',
					PiiRedactionType.AgentName
				)} retargeted successfully.`
			);
		} finally {
			harness.restore();
		}
	});

	test('cancels without pulling when the existing-agent prompt is dismissed', async () => {
		let pullCalled = false;
		const harness = installFlowHarness({
			attached: true,
			currentEnvironmentId: 'same-environment',
			targetEnvironmentId: 'same-environment',
			sendRequest: async () => ({
				code: 200,
				message: '',
				remoteAgentExists: true,
			}),
			pull: async () => {
				pullCalled = true;
			},
			showWarningMessage: async (message, optionsOrItem, ...items) => {
				assert.strictEqual(
					message,
					"This agent (Test Agent) already exists in 'Target Environment'. Refresh from the cloud?"
				);
				assert.deepStrictEqual(optionsOrItem, { modal: true });
				assert.deepStrictEqual(items, ['Refresh']);
				return undefined;
			},
		});

		try {
			await executeReattachAgentCommand(harness.context, { workspace: harness.workspace });

			assert.strictEqual(pullCalled, false);
			assert.strictEqual(harness.requests.length, 1);
			assert.strictEqual(harness.requests[0].request.checkRemoteAgentOnly, true);
			assert.strictEqual(harness.logs.filter(log => log.level === 'error').length, 0);
			assert.strictEqual(
				harness.logs.filter(log => log.level === 'info' && typeof log.message === 'string').length,
				0
			);
			const cancellationLogs = harness.logs.filter(log => log.level === 'warning');
			assert.strictEqual(cancellationLogs.length, 1);
			assert.deepStrictEqual(cancellationLogs[0].data, {
				message: 'Retarget agent canceled because the connected remote agent already exists.',
			});
		} finally {
			harness.restore();
		}
	});

	test('pulls with nested error notifications suppressed when Refresh is selected', async () => {
		let receivedPullOptions: PullOptions | undefined;
		let lockActive = false;
		const selectedAccount: AccountInfo = {
			accountId: 'selected-account-id',
			accountEmail: 'selected@example.com',
			tenantId: 'selected-tenant-id',
		};
		const harness = installFlowHarness({
			attached: true,
			currentEnvironmentId: 'same-environment',
			targetEnvironmentId: 'same-environment',
			accounts: [selectedAccount],
			workspaceAccount: {
				accountId: 'previous-account-id',
				accountEmail: 'previous@example.com',
				tenantId: 'previous-tenant-id',
			},
			sendRequest: async () => {
				assert.strictEqual(lockActive, true, 'the remote existence check must be guarded');
				return {
					code: 200,
					message: '',
					remoteAgentExists: true,
				};
			},
			pull: async pullOptions => {
				assert.strictEqual(lockActive, true, 'Refresh Pull must use the same guard');
				receivedPullOptions = pullOptions;
			},
			withSyncCommandBusy: async (_workspaceUri, action) => {
				lockActive = true;
				try {
					return await action();
				} finally {
					lockActive = false;
				}
			},
		});

		try {
			await executeReattachAgentCommand(harness.context, { workspace: harness.workspace });

			assert.deepStrictEqual(receivedPullOptions, {
				suppressErrorNotification: true,
				suppressSuccessNotification: true,
				account: {
					...selectedAccount,
					tenantId: '',
				},
			});
			assert.strictEqual(harness.requests.length, 1);
			assert.strictEqual(harness.requests[0].request.checkRemoteAgentOnly, true);
			assert.deepStrictEqual(harness.progressOptions, [{
				location: vscode.ProgressLocation.Notification,
				title: 'Retargeting Agent...',
				cancellable: false,
			}]);
			assert.strictEqual(harness.logs.filter(log => log.level === 'error').length, 0);
			assert.strictEqual(harness.logs.filter(log => log.level === 'warning').length, 0);
			const successLogs = harness.logs.filter(
				log => log.level === 'info' && typeof log.message === 'string'
			);
			assert.strictEqual(successLogs.length, 1);
			assert.strictEqual(
				successLogs[0].message,
				'Retarget agent completed by refreshing the agent from its current environment.'
			);
		} finally {
			harness.restore();
		}
	});

	test('rolls back a retarget upload failure and reports one terminal error', async () => {
		const uploadError = new Error('Upload failed');
		let lockActive = false;
		let terminalErrorObservedLock = false;
		const harness = installFlowHarness({
			attached: true,
			currentEnvironmentId: 'old-environment',
			targetEnvironmentId: 'new-environment',
			sendRequest: async (_method, request) => {
				if (request.pushSucceeded !== undefined) {
					assert.strictEqual(lockActive, true, 'rollback must finish while the sync guard is active');
					return {};
				}
				return {
					code: 200,
					message: '',
					agentSyncInfo: {
						agentId: 'retargeted-agent-id',
						environmentId: 'new-environment',
					},
					requiresLocalPush: true,
				};
			},
			pushNewWorkspace: async () => {
				throw uploadError;
			},
			withSyncCommandBusy: async (_workspaceUri, action) => {
				lockActive = true;
				try {
					return await action();
				} finally {
					lockActive = false;
				}
			},
			onLog: level => {
				if (level === 'error') {
					terminalErrorObservedLock = lockActive;
				}
			},
		});

		try {
			await executeReattachAgentCommand(harness.context, { workspace: harness.workspace });

			assert.strictEqual(harness.requests.length, 2);
			assert.strictEqual(harness.requests[1].request.pushSucceeded, false);
			assert.strictEqual(harness.logs.filter(log => log.level === 'warning').length, 0);
			const errorLogs = harness.logs.filter(log => log.level === 'error');
			assert.strictEqual(errorLogs.length, 1);
			assert.strictEqual(terminalErrorObservedLock, true);
			assert.strictEqual(
				errorLogs[0].message,
				'Retargeting failed while uploading content. The workspaces were reverted to their previous environment. Please try again.'
			);
			assert.deepStrictEqual(errorLogs[0].data, { error: uploadError });
		} finally {
			harness.restore();
		}
	});

	test('reports rollback failure when upload fails before the retarget result returns', async () => {
		const uploadError = new Error('Upload failed');
		const rollbackError = new Error('Rollback failed');
		const harness = installFlowHarness({
			attached: true,
			currentEnvironmentId: 'old-environment',
			targetEnvironmentId: 'new-environment',
			sendRequest: async (_method, request) => {
				if (request.pushSucceeded === false) {
					throw rollbackError;
				}
				return {
					code: 200,
					message: '',
					agentSyncInfo: {
						agentId: 'retargeted-agent-id',
						environmentId: 'new-environment',
					},
					requiresLocalPush: true,
				};
			},
			pushNewWorkspace: async () => {
				throw uploadError;
			},
		});

		try {
			await executeReattachAgentCommand(harness.context, { workspace: harness.workspace });

			assert.strictEqual(harness.requests.length, 2);
			const errorLogs = harness.logs.filter(log => log.level === 'error');
			assert.strictEqual(errorLogs.length, 1);
			assert.strictEqual(
				errorLogs[0].message,
				'Retarget failed and rollback to the previous environment failed'
			);
			const reportedError = (errorLogs[0].data as { error: Error }).error;
			assert.match(reportedError.message, /Failed to finalize 1 of 1 retarget operation/);
			assert.match(reportedError.message, /Rollback failed/);
		} finally {
			harness.restore();
		}
	});

	test('keeps automatic connection setup under the sync guard and prompts afterward', async () => {
		let lockActive = false;
		let automaticSetupObservedLock = false;
		let terminalSuccessObservedLock = false;
		let promptObservedReleasedLock = false;
		const harness = installFlowHarness({
			withSyncCommandBusy: async (_workspaceUri, action) => {
				lockActive = true;
				try {
					return await action();
				} finally {
					lockActive = false;
				}
			},
			autoBindAgentConnections: async () => {
				automaticSetupObservedLock = lockActive;
				return {
					needsNewCount: 1,
					boundCount: 0,
					enabledWorkflowCount: 0,
					disabledWorkflowNames: [],
				};
			},
			promptManageConnectionsForWorkspaces: async () => {
				promptObservedReleasedLock = !lockActive;
			},
			onLog: (level, message) => {
				if (level === 'info' && typeof message === 'string') {
					terminalSuccessObservedLock = lockActive;
				}
			},
		});

		try {
			await executeReattachAgentCommand(harness.context, { workspace: harness.workspace });

			assert.strictEqual(automaticSetupObservedLock, true);
			assert.strictEqual(terminalSuccessObservedLock, true);
			assert.strictEqual(promptObservedReleasedLock, true);
			assert.deepStrictEqual(harness.events, [
				'terminal-success',
				'prompt-connections',
			]);
			assert.strictEqual(harness.logs.filter(log => log.level === 'error').length, 0);
		} finally {
			harness.restore();
		}
	});

	test('releases the sync guard before awaiting the diagnostics action', async () => {
		let lockActive = false;
		let resolveLockReleased: (() => void) | undefined;
		const lockReleased = new Promise<void>(resolve => {
			resolveLockReleased = resolve;
		});
		let resolveSelection: ((selection: string | undefined) => void) | undefined;
		const selection = new Promise<string | undefined>(resolve => {
			resolveSelection = resolve;
		});
		let actionObservedReleasedLock = false;
		const harness = installFlowHarness({
			attached: true,
			currentEnvironmentId: 'old-environment',
			targetEnvironmentId: 'new-environment',
			getDiagnosticsErrors: async () => ({ count: 1, files: 1 }),
			showWarningMessage: async (_message, optionsOrItem, ...items) =>
				typeof optionsOrItem === 'string' ? await selection : items[0],
			withSyncCommandBusy: async (_workspaceUri, action) => {
				lockActive = true;
				try {
					return await action();
				} finally {
					lockActive = false;
					resolveLockReleased?.();
				}
			},
			executeCommand: async command => {
				assert.strictEqual(command, 'workbench.actions.view.problems');
				actionObservedReleasedLock = !lockActive;
			},
		});

		try {
			const command = executeReattachAgentCommand(harness.context, { workspace: harness.workspace });
			await lockReleased;
			assert.strictEqual(lockActive, false);
			resolveSelection?.('View Details');
			await command;

			assert.strictEqual(actionObservedReleasedLock, true);
			assert.strictEqual(harness.requests.length, 0);
		} finally {
			resolveSelection?.(undefined);
			harness.restore();
		}
	});

	test('awaits same-environment Pull failure and reports no false success', async () => {
		const pullError = new Error('Pull failed');
		let receivedPullOptions: PullOptions | undefined;
		const harness = installFlowHarness({
			attached: true,
			currentEnvironmentId: 'same-environment',
			targetEnvironmentId: 'same-environment',
			sendRequest: async () => ({
				code: 200,
				message: '',
				remoteAgentExists: true,
			}),
			pull: async pullOptions => {
				receivedPullOptions = pullOptions;
				throw pullError;
			},
		});

		try {
			await executeReattachAgentCommand(harness.context, { workspace: harness.workspace });

			assert.deepStrictEqual(receivedPullOptions, {
				suppressErrorNotification: true,
				suppressSuccessNotification: true,
				account: {
					accountId: 'account-id',
					accountEmail: 'developer@example.com',
					tenantId: '',
				},
			});
			assert.strictEqual(harness.requests.length, 1);
			assert.strictEqual(harness.requests[0].request.checkRemoteAgentOnly, true);
			assert.strictEqual(
				harness.logs.filter(log => log.level === 'info' && typeof log.message === 'string').length,
				0,
				'a failed Pull must not produce a Reattach success'
			);
			const errorLogs = harness.logs.filter(log => log.level === 'error');
			assert.strictEqual(errorLogs.length, 1);
			assert.strictEqual(errorLogs[0].message, 'Error reattaching agent');
			assert.deepStrictEqual(errorLogs[0].data, { error: pullError });
		} finally {
			harness.restore();
		}
	});
});
