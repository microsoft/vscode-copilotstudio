import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import * as vscode from 'vscode';
import { AuthError } from '../../clients/account';
import { handleSyncAuthError } from '../../sync/authFailureNotification';
import { CopilotStudioWorkspace } from '../../sync/localWorkspaces';

const workspace: CopilotStudioWorkspace = {
	workspaceUri: 'file:///agents/test/',
	displayName: 'Test Agent',
	description: '',
	icon: undefined as any,
	type: 0 as any,
	syncInfo: { accountInfo: { accountId: 'acc-1', accountEmail: 'dev@b.com' } } as any,
};

function stubWarning(handler: (message: string, ...items: string[]) => Promise<string | undefined>): () => void {
	const original = vscode.window.showWarningMessage;
	(vscode.window as any).showWarningMessage = handler;
	return () => { (vscode.window as any).showWarningMessage = original; };
}

function stubExecuteCommand(handler: (...args: unknown[]) => Promise<unknown>): () => void {
	const original = vscode.commands.executeCommand;
	(vscode.commands as any).executeCommand = handler;
	return () => { (vscode.commands as any).executeCommand = original; };
}

describe('handleSyncAuthError', () => {
	test('returns false for a non-AuthError so the caller logs it generically', async () => {
		const handled = await handleSyncAuthError(workspace, new Error('boom'));
		assert.strictEqual(handled, false);
	});

	test('stays silent and returns handled for a cancelled prompt', async () => {
		let shown = false;
		const restore = stubWarning(async () => { shown = true; return undefined; });
		try {
			const handled = await handleSyncAuthError(workspace, new AuthError('cancelled', 'x', 'acc-1', 'dev@b.com'));
			assert.strictEqual(handled, true);
			assert.strictEqual(shown, false, 'a cancelled prompt must not show a notification');
		} finally {
			restore();
		}
	});

	test('terminal failure invokes the reattach command when Retarget is chosen', async () => {
		let executed: unknown[] | undefined;
		const restoreWarning = stubWarning(async (_message, ...items) => items.find((item) => item.startsWith('Retarget')));
		const restoreExec = stubExecuteCommand(async (...args) => { executed = args; return undefined; });
		try {
			const handled = await handleSyncAuthError(workspace, new AuthError('terminal', 'x', 'acc-1', 'dev@b.com'));
			assert.strictEqual(handled, true);
			assert.ok(executed, 'the reattach command should be executed');
			assert.strictEqual(executed![0], 'microsoft-copilot-studio.reattachAgent');
			assert.deepStrictEqual(executed![1], { workspace });
		} finally {
			restoreExec();
			restoreWarning();
		}
	});

	test('transient failure runs the retry callback when Sign in is chosen', async () => {
		let retried = false;
		const restore = stubWarning(async (_message, ...items) => items[0]);
		try {
			const handled = await handleSyncAuthError(workspace, new AuthError('transient', 'x', 'acc-1', 'dev@b.com'), async () => { retried = true; });
			assert.strictEqual(handled, true);
			assert.strictEqual(retried, true);
		} finally {
			restore();
		}
	});

	test('transient failure also offers Retarget', async () => {
		let executed: unknown[] | undefined;
		const restoreWarning = stubWarning(async (_message, ...items) => items.find((item) => item.startsWith('Retarget')));
		const restoreExec = stubExecuteCommand(async (...args) => { executed = args; return undefined; });
		try {
			await handleSyncAuthError(workspace, new AuthError('transient', 'x', 'acc-1', 'dev@b.com'));
			assert.ok(executed, 'a transient failure should also offer Retarget');
			assert.strictEqual(executed![0], 'microsoft-copilot-studio.reattachAgent');
		} finally {
			restoreExec();
			restoreWarning();
		}
	});

	test('transient failure does not retry when the notification is dismissed', async () => {
		let retried = false;
		const restore = stubWarning(async () => undefined);
		try {
			await handleSyncAuthError(workspace, new AuthError('transient', 'x', 'acc-1', 'dev@b.com'), async () => { retried = true; });
			assert.strictEqual(retried, false);
		} finally {
			restore();
		}
	});
});
