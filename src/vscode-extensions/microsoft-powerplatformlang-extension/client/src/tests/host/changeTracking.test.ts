import * as assert from 'node:assert';
import { afterEach, describe, test } from 'node:test';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import * as vscode from 'vscode';

import {
	LocalChangeResourceCommandResolver,
	RemoteChangeResourceCommandResolver,
	Resource
} from '../../sync/changeTracking';
import { OriginalFileSystem } from '../../sync/originalFileSystem';
import { RemoteFileSystem } from '../../sync/remoteFileSystem';
import { AIPromptSchemaNamePrefix, CustomConnectorSchemaNamePrefix } from '../../constants';
import { ChangeType } from '../../types';
import { replace, restoreReplaced } from './helpers/replace';

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

	for (const fileSchemaName of [`${CustomConnectorSchemaNamePrefix}11111111-1111-1111-1111-111111111111.openapidefinition`, `${AIPromptSchemaNamePrefix}11111111-1111-1111-1111-111111111111.metadata`]) {
		const createResource = (resolver: LocalChangeResourceCommandResolver | RemoteChangeResourceCommandResolver) => new Resource(
			resolver,
			vscode.Uri.parse('assets/Weather/openapidefinition.json'),
			fileSchemaName,
			'Asset',
			ChangeType.Update,
		);

		test(`A local ${fileSchemaName} update diffs the cached cloud copy against the workspace file`, () => {
			const resource = createResource(new LocalChangeResourceCommandResolver(workspaceUri));

			const args = resource.command.arguments as vscode.Uri[];
			assert.strictEqual(resource.command.command, 'vscode.diff');
			assert.strictEqual(args[0].authority, 'local');
			assert.strictEqual(args[0].path, `/Local Cache: ${fileSchemaName}`);
			assert.strictEqual(args[1].scheme, 'file');
		});

		test(`A remote ${fileSchemaName} update diffs the cached cloud copy against the remote copy`, () => {
			const resource = createResource(new RemoteChangeResourceCommandResolver(workspaceUri));

			const args = resource.command.arguments as vscode.Uri[];
			assert.strictEqual(resource.command.command, 'vscode.diff');
			assert.strictEqual(args[0].path, `/Local Cache: ${fileSchemaName}`);
			assert.strictEqual(args[1].path, `/Remote: ${fileSchemaName}`);
		});
	}
});

describe('Connector icon diff', () => {
	const workspaceUri = vscode.Uri.parse('file:///tmp/ws/agent/');
	const connectorSchemaName = `${CustomConnectorSchemaNamePrefix}11111111-1111-1111-1111-111111111111`;
	const iconSchemaName = `${connectorSchemaName}.iconblob`;
	const openApiSchemaName = `${connectorSchemaName}.openapidefinition`;
	const icon = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0xff]);
	const openApiDefinition = '{ "swagger": "2.0" }';

	const serveCloudCopies = () => {
		const requestedSchemaNames: string[] = [];
		const workspace = { workspaceUri: workspaceUri.toString(), displayName: 'Agent', syncInfo: { agentManagementEndpoint: 'https://api.example.com' } };
		replace(require('../../sync/localWorkspaces'), 'getWorkspaceByUri', () => workspace);
		replace(require('../../sync/localWorkspaces'), 'findWorkspaceForUri', () => workspace);
		replace(require('../../services/lspClient'), 'buildLspRequestPayload', async () => ({}));
		replace(require('../../services/lspClient'), 'lspClient', {
			sendRequest: async (_method: string, request: { schemaName: string }) => {
				requestedSchemaNames.push(request.schemaName);
				return { code: 200, content: request.schemaName === iconSchemaName ? icon.toString('base64') : openApiDefinition };
			},
		});
		return requestedSchemaNames;
	};

	const getDiffUris = (resolver: LocalChangeResourceCommandResolver | RemoteChangeResourceCommandResolver, schemaName: string, fileName: string) =>
		new Resource(resolver, vscode.Uri.parse(`connectors/Weather/${fileName}`), schemaName, 'Asset', ChangeType.Update).command.arguments as vscode.Uri[];

	afterEach(restoreReplaced);

	test('A local icon update shows the cached cloud icon as a PNG image', async () => {
		const requestedSchemaNames = serveCloudCopies();

		const [cached, local] = getDiffUris(new LocalChangeResourceCommandResolver(workspaceUri), iconSchemaName, 'iconblob.png');

		assert.strictEqual(path.extname(cached.path), '.png');
		assert.strictEqual(path.extname(local.path), '.png');
		assert.deepStrictEqual(Buffer.from(await new OriginalFileSystem().readFile(cached)), icon);
		assert.deepStrictEqual(requestedSchemaNames, [iconSchemaName]);
	});

	test('A remote icon update shows both the cached and the remote cloud icon as PNG images', async () => {
		const requestedSchemaNames = serveCloudCopies();

		const [cached, remote] = getDiffUris(new RemoteChangeResourceCommandResolver(workspaceUri), iconSchemaName, 'iconblob.png');

		assert.strictEqual(path.extname(cached.path), '.png');
		assert.strictEqual(path.extname(remote.path), '.png');
		assert.deepStrictEqual(Buffer.from(await new OriginalFileSystem().readFile(cached)), icon);
		assert.deepStrictEqual(Buffer.from(await new RemoteFileSystem().readFile(remote)), icon);
		assert.deepStrictEqual(requestedSchemaNames, [iconSchemaName, iconSchemaName]);
	});

	test('Other connector files still diff as text', async () => {
		const requestedSchemaNames = serveCloudCopies();

		const [cached, remote] = getDiffUris(new RemoteChangeResourceCommandResolver(workspaceUri), openApiSchemaName, 'openapidefinition.json');

		assert.strictEqual(Buffer.from(await new OriginalFileSystem().readFile(cached)).toString('utf8'), openApiDefinition);
		assert.strictEqual(Buffer.from(await new RemoteFileSystem().readFile(remote)).toString('utf8'), openApiDefinition);
		assert.deepStrictEqual(requestedSchemaNames, [openApiSchemaName, openApiSchemaName]);
	});
});
