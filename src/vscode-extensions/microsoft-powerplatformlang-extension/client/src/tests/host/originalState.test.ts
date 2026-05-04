import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import * as vscode from 'vscode';
import { waitForFirstWorkspace } from './helpers/workspaceWait';

describe('TextDocumentContentProvider Integration', () => {
	test('Local cache provider returns content', { timeout: 10_000 }, async () => {
		const ext = vscode.extensions.all.find(e => e.id === 'ms-CopilotStudio.vscode-copilotstudio');
		assert.ok(ext, 'extension not found');
		const api = await ext!.activate() as {
			addWorkspaceChangeSubscription: typeof import('../../sync/localWorkspaces').addWorkspaceChangeSubscription;
			getAllWorkspaces: typeof import('../../sync/localWorkspaces').getAllWorkspaces;
		};

		const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
		assert.ok(workspaceFolder, 'workspace folder should be defined');
		const agentUri = vscode.Uri.joinPath(workspaceFolder.uri, 'agent.mcs.yml');

        // this will trigger the extension language server to initialize the workspace internally
		await vscode.commands.executeCommand('vscode.open', agentUri);
		await waitForFirstWorkspace(api.addWorkspaceChangeSubscription, api.getAllWorkspaces);

		// test missing file
		const query = workspaceFolder.uri.toString();
		const normalizedQuery = query.endsWith('/') ? query : query + '/';
		const missingFileTestUri = vscode.Uri.from({
			scheme: 'mcs',
			authority: 'local',
			path: '/nonExisting.schema',
			query: normalizedQuery
		});
		
		const missingDoc = await vscode.workspace.openTextDocument(missingFileTestUri);
		assert.ok(missingDoc, 'Document should still be returned even if missing');
		assert.strictEqual(missingDoc.getText(), '', 'Content of missing file should be empty');

		// test existing file (in test fixture)
		const agentFileTestUri = vscode.Uri.from({
			scheme: 'mcs',
			authority: 'local',
			path: '/crd1c_agent.gpt.default',
			query: normalizedQuery
		});
		const remoteDoc = await vscode.workspace.openTextDocument(agentFileTestUri);
		assert.ok(remoteDoc, 'remote document should be returned');
        assert.ok(remoteDoc.lineCount > 0, "remote document should have content");

        // verify that the virtual (remote) document contains the same
        // text as the file that exists on disk inside the workspace.
        // Line endings are normalized: the cache provider returns CRLF
        // on Windows while raw fs bytes are whatever the file uses (LF
        // here); the assertion is about content, not encoding.
        const localFileBytes = await vscode.workspace.fs.readFile(agentUri);
        const localFileText = Buffer.from(localFileBytes).toString('utf8');
        const normalizeEol = (s: string) => s.replace(/\r\n/g, '\n');

        assert.strictEqual(
            normalizeEol(remoteDoc.getText()),
            normalizeEol(localFileText),
            'remote content should match local content'
        );
	});
});
