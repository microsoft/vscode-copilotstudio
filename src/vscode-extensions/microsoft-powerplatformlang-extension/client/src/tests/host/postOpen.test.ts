import * as assert from 'node:assert';
import { describe, test, beforeEach, afterEach } from 'node:test';
import * as vscode from 'vscode';
import * as fs from 'fs/promises';
import * as path from 'path';
import * as os from 'os';
import { maybeOpenFileFromPostOpen, prepareClonedWorkspace, PostOpenInstruction } from '../../startup/postOpen';

describe('prepareClonedWorkspace', () => {
    const stateKey = 'mcs.postOpenInstruction';

    let root: string;
    let stored: Record<string, unknown>;
    let context: vscode.ExtensionContext;

    const read = (): PostOpenInstruction | undefined => stored[stateKey] as PostOpenInstruction | undefined;

    beforeEach(async () => {
        root = path.join(os.tmpdir(), 'mcs-postopen-' + Date.now() + '-' + Math.random().toString(36).slice(2));
        await fs.mkdir(root, { recursive: true });

        stored = {};
        context = {
            globalState: {
                get: (key: string) => stored[key],
                update: async (key: string, value: unknown) => { stored[key] = value; },
            },
        } as unknown as vscode.ExtensionContext;
    });

    afterEach(async () => {
        await fs.rm(root, { recursive: true, force: true });
    });

    test('queues the entry file when it exists on disk', async () => {
        const folder = path.join(root, 'workflows', 'MyFlow-1');
        await fs.mkdir(folder, { recursive: true });
        await fs.writeFile(path.join(folder, 'workflow.json'), '{}');

        const workspaceUri = vscode.Uri.file(root);
        const entryFile = vscode.Uri.file(path.join(folder, 'workflow.json'));

        assert.strictEqual(await prepareClonedWorkspace(context, workspaceUri, entryFile), true);

        const instruction = read();
        assert.ok(instruction);
        assert.strictEqual(instruction!.workspaceUri, workspaceUri.toString());
        assert.strictEqual(instruction!.targetFileUri, entryFile.toString());
    });

    test('queues nothing when the entry file is absent', async () => {
        const missing = vscode.Uri.file(path.join(root, 'workflows', 'Gone-1', 'workflow.json'));

        assert.strictEqual(await prepareClonedWorkspace(context, vscode.Uri.file(root), missing), false);
        assert.strictEqual(read(), undefined);
    });

    test('queues nothing when no entry file is supplied', async () => {
        assert.strictEqual(await prepareClonedWorkspace(context, vscode.Uri.file(root), undefined), false);
        assert.strictEqual(read(), undefined);
    });

    test('a queued instruction carries a future expiry', async () => {
        await fs.writeFile(path.join(root, 'settings.mcs.yml'), 'displayName: Agent');

        await prepareClonedWorkspace(context, vscode.Uri.file(root), vscode.Uri.file(path.join(root, 'settings.mcs.yml')));

        assert.ok(read()!.expiresAt! > Date.now());
    });

    test('the queued instruction is dropped when the opened workspace does not match', async () => {
        await fs.writeFile(path.join(root, 'settings.mcs.yml'), 'displayName: Agent');

        await prepareClonedWorkspace(context, vscode.Uri.file(root), vscode.Uri.file(path.join(root, 'settings.mcs.yml')));
        await maybeOpenFileFromPostOpen(context);

        assert.strictEqual(read(), undefined);
    });

    test('a workflow clone queues the definition the server reported', async () => {
        const folderName = 'workflows/S5D1WF1-1eeeda41';
        const folder = path.join(root, ...folderName.split('/'));
        await fs.mkdir(folder, { recursive: true });
        await fs.writeFile(path.join(folder, 'workflow.json'), '{}');

        const workspaceUri = vscode.Uri.file(root);
        const entryFile = vscode.Uri.joinPath(workspaceUri, ...folderName.split('/'), 'workflow.json');

        assert.strictEqual(await prepareClonedWorkspace(context, workspaceUri, entryFile), true);
        assert.strictEqual(read()!.targetFileUri, vscode.Uri.file(path.join(folder, 'workflow.json')).toString());
    });
});
