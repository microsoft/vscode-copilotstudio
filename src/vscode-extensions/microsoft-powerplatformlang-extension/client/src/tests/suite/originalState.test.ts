import * as assert from 'assert';
import * as vscode from 'vscode';
import { waitForFirstWorkspace } from '../helpers/workspaceWait';

suite('TextDocumentContentProvider Integration', function () {
	// allow more time for integration tests
	this.timeout(10_000);
	test('Local cache provider returns content', async () => {
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
        assert.strictEqual(remoteDoc.lineCount, 19, "remote document should have content");

        // verify that the virtual (remote) document contains exactly the same
        // text as the file that exists on disk inside the workspace
        const localFileBytes = await vscode.workspace.fs.readFile(agentUri);
        const localFileText = Buffer.from(localFileBytes).toString('utf8');

        assert.strictEqual(
            remoteDoc.getText(),
            localFileText,
            'remote content should match local content'
        );
	});
});
