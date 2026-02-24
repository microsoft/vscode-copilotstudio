import * as assert from 'assert';
import * as vscode from 'vscode';

import {
	LocalChangeResourceCommandResolver,
	Resource
} from '../../sync/changeTracking';
import { ChangeType } from '../../types';

suite('SCM Diff Command Tests', () => {
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
		assert.strictEqual(args[0].toString(), 'mcs://local/test.schema?file%3A%2F%2F%2Ftmp%2Fws%2Fspace%2520separated%2520folder%2F');
		// full (local)
		assert.strictEqual(args[1].toString(), 'file:///tmp/ws/space%20separated%20folder/tmp/ws/space%20separated%20folder/test.schema.mcs.yml');
		// current (local)
		assert.strictEqual(args[2].toString(), 'file:///tmp/ws/space%20separated%20folder/test.schema.mcs.yml');
	});
});
