import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import * as vscode from 'vscode';

import {
	LocalChangeResourceCommandResolver,
	Resource
} from '../../sync/changeTracking';
import { ChangeType } from '../../types';

describe('SCM Diff Command Tests', () => {
	const workspaceUri = vscode.Uri.parse('file:///tmp/ws/space separated folder/');
	const schemaName = 'test.schema';
	const fileUri = vscode.Uri.joinPath(workspaceUri, `${schemaName}.mcs.yml`);

	test('Local resolver returns vscode.diff command with escaped arguments', () => {
		const resolver = new LocalChangeResourceCommandResolver(workspaceUri);
		const resource = new Resource(
			resolver,
			fileUri,
			schemaName,
			"Unknown",
			ChangeType.Update
		);

		const cmd = resource.command;
		assert.ok(cmd, 'command should be defined');
		assert.strictEqual(cmd.command, 'vscode.diff');
		const args = cmd.arguments as vscode.Uri[];
		// original (remote)
		assert.strictEqual(args[0].toString(), 'mcs://local/Local%20Cache%3A%20test.schema?file%3A%2F%2F%2Ftmp%2Fws%2Fspace%2520separated%2520folder%2F');
		// full (local)
		assert.strictEqual(args[1].toString(), 'file:///tmp/ws/space%20separated%20folder/tmp/ws/space%20separated%20folder/test.schema.mcs.yml');
		// current (local)
		assert.strictEqual(args[2].toString(), 'file:///tmp/ws/space%20separated%20folder/test.schema.mcs.yml');
	});

	test('Modified conflict opens the actual local text rather than an empty deletion preview', async () => {
		const root = await fs.mkdtemp(path.join(os.tmpdir(), 'mcs-conflict-diff-'));
		const relativePath = 'topics/Goodbye.mcs.yml';
		const content = 'kind: AdaptiveDialog\n<<<<<<< ours\nmodelDescription: local\n=======\nmodelDescription: remote\n>>>>>>> theirs\n';
		try {
			await fs.mkdir(path.join(root, 'topics'));
			await fs.writeFile(path.join(root, 'topics', 'Goodbye.mcs.yml'), content);
			const rootUri = vscode.Uri.file(root);
			const resource = new Resource(
				new LocalChangeResourceCommandResolver(rootUri),
				vscode.Uri.parse(relativePath),
				'test_agent.topic.Goodbye',
				'DialogComponent',
				ChangeType.Update,
			);

			const command = resource.command;
			const args = command.arguments as vscode.Uri[];
			assert.strictEqual(command.command, 'vscode.diff');
			assert.strictEqual(args[0].scheme, 'mcs');
			assert.strictEqual(args[0].authority, 'local');
			assert.strictEqual(args[1].toString(), vscode.Uri.joinPath(rootUri, relativePath).toString());
			assert.strictEqual(resource.letter, 'M');
			assert.strictEqual(resource.decorations.strikeThrough, false);
			const document = await vscode.workspace.openTextDocument(args[1]);
			assert.strictEqual(document.getText(), content);
		} finally {
			await fs.rm(root, { recursive: true, force: true });
		}
	});

	test('An actual deletion still opens an empty right-hand side', () => {
		const resource = new Resource(
			new LocalChangeResourceCommandResolver(workspaceUri),
			vscode.Uri.parse('topics/Goodbye.mcs.yml'),
			'test_agent.topic.Goodbye',
			'DialogComponent',
			ChangeType.Delete,
		);

		const args = resource.command.arguments as vscode.Uri[];
		assert.strictEqual(args[1].scheme, 'mcs');
		assert.strictEqual(args[1].authority, 'empty');
		assert.strictEqual(resource.letter, 'D');
		assert.strictEqual(resource.decorations.strikeThrough, true);
	});
});
