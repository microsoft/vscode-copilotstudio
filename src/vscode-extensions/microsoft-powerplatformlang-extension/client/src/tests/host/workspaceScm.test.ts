import * as assert from 'node:assert';
import { afterEach, describe, test } from 'node:test';
import * as vscode from 'vscode';

import * as virtualKnowledgeModule from '../../knowledgeFiles/virtualKnowledgeFile';
import * as localWorkspacesModule from '../../sync/localWorkspaces';
import { CopilotStudioWorkspace, WorkspaceType } from '../../sync/localWorkspaces';
import { pullsBeforePush, pushNewWorkspace, refreshInitialLocalChanges, scheduleLocalChangeRefresh } from '../../sync/workspaceScm';
import * as workspaceSynchronizerModule from '../../sync/workspaceSynchronizer';
import { PushOptions } from '../../sync/workspaceSynchronizer';
import { replace, restoreReplaced } from './helpers/replace';

describe('workspaceScm: pull before push', () => {
	test('pulls an agent workspace before pushing it', () => {
		assert.strictEqual(pullsBeforePush(WorkspaceType.Agent), true);
	});

	test('pulls a workspace of an unknown kind before pushing it', () => {
		assert.strictEqual(pullsBeforePush(WorkspaceType.Unknown), true);
	});

	test('skips the pull for a component collection workspace', () => {
		assert.strictEqual(pullsBeforePush(WorkspaceType.ComponentCollection), false);
	});

	test('skips the pull for a standalone workflow workspace', () => {
		assert.strictEqual(pullsBeforePush(WorkspaceType.StandaloneWorkflow), false);
	});
});

describe('workspaceScm: upload of a reattached workspace', () => {
	afterEach(() => restoreReplaced());

	test('hands the environment variable overwrites the reattach approved to the upload', async () => {
		const workspace = { workspaceUri: 'file:///c%3A/reattach-upload-test/Agent', displayName: 'Agent', type: WorkspaceType.Agent } as CopilotStudioWorkspace;
		const pushes: PushOptions[] = [];
		replace(localWorkspacesModule as any, 'updateWorkspaceCache', async () => [workspace]);
		replace(virtualKnowledgeModule as any, 'registerVirtualKnowledgeProvider', async () => undefined);
		replace(workspaceSynchronizerModule as any, 'getOrAddSynchronizer', () => ({
			pull: async () => ({ localChanges: [] }),
			push: async (options: PushOptions) => {
				pushes.push(options);
				return { localChanges: [] };
			},
		}));

		await pushNewWorkspace({} as vscode.ExtensionContext, workspace, true, ['cr9f2_shared']);

		assert.deepStrictEqual(pushes.map(options => options.approvedEnvironmentVariableOverwrites), [['cr9f2_shared']]);
	});
});

describe('workspaceScm: initial local changes', () => {
	test('keeps setup alive when the initial local diff fails', async () => {
		let attempts = 0;

		await refreshInitialLocalChanges(async () => {
			attempts++;
			throw new Error('invalid local file');
		});

		assert.strictEqual(attempts, 1);
	});

	test('coalesces a burst of workspace changes into the latest local refresh', async () => {
		const calls: string[] = [];

		scheduleLocalChangeRefresh('workspace', async () => {
			calls.push('first');
		}, 5);
		scheduleLocalChangeRefresh('workspace', async () => {
			calls.push('latest');
		}, 5);

		await new Promise(resolve => setTimeout(resolve, 25));

		assert.deepStrictEqual(calls, ['latest']);
	});

	test('never runs two local refreshes at once and finishes with the newest state', async () => {
		const events: string[] = [];
		let running = 0;

		const refresh = (name: string, durationMs: number) => async () => {
			running++;
			assert.strictEqual(running, 1, `${name} started while another refresh was in flight`);
			events.push(`start:${name}`);
			await new Promise(resolve => setTimeout(resolve, durationMs));
			events.push(`end:${name}`);
			running--;
		};

		scheduleLocalChangeRefresh('serialized', refresh('slow', 40), 1);
		await new Promise(resolve => setTimeout(resolve, 10));
		scheduleLocalChangeRefresh('serialized', refresh('fast', 1), 1);

		await new Promise(resolve => setTimeout(resolve, 120));

		assert.deepStrictEqual(events, ['start:slow', 'end:slow', 'start:fast', 'end:fast']);
	});

	test('collapses events that arrive during a refresh into a single trailing refresh', async () => {
		const events: string[] = [];
		let releaseFirst: () => void = () => { };
		const firstStarted = new Promise<void>(resolveStarted => {
			scheduleLocalChangeRefresh('trailing', async () => {
				events.push('start:running');
				resolveStarted();
				await new Promise<void>(resolve => { releaseFirst = resolve; });
				events.push('end:running');
			}, 1);
		});

		await firstStarted;

		scheduleLocalChangeRefresh('trailing', async () => {
			events.push('start:stale');
		}, 1);
		scheduleLocalChangeRefresh('trailing', async () => {
			events.push('start:newest');
		}, 1);

		await new Promise(resolve => setTimeout(resolve, 30));
		assert.deepStrictEqual(events, ['start:running']);

		releaseFirst();
		await new Promise(resolve => setTimeout(resolve, 30));

		assert.deepStrictEqual(events, ['start:running', 'end:running', 'start:newest']);
	});

	test('keeps serializing refreshes after one of them fails', async () => {
		const events: string[] = [];
		let failFirst: (reason: Error) => void = () => { };
		const firstStarted = new Promise<void>(resolveStarted => {
			scheduleLocalChangeRefresh('failing', async () => {
				events.push('start:first');
				resolveStarted();
				await new Promise<void>((_, reject) => { failFirst = reject; });
			}, 1);
		});

		await firstStarted;

		scheduleLocalChangeRefresh('failing', async () => {
			events.push('start:second');
		}, 1);

		await new Promise(resolve => setTimeout(resolve, 30));
		assert.deepStrictEqual(events, ['start:first']);

		failFirst(new Error('local diff failed'));
		await new Promise(resolve => setTimeout(resolve, 30));

		assert.deepStrictEqual(events, ['start:first', 'start:second']);
	});
});
