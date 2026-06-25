import * as assert from 'node:assert';
import { describe, test, beforeEach, afterEach } from 'node:test';
import * as vscode from 'vscode';
import * as fs from 'fs/promises';
import * as path from 'path';
import * as os from 'os';
import { virtualKnowledgeFileSystemProvider } from '../../knowledgeFiles/virtualKnowledgeFile';
import { CopilotStudioWorkspace } from '../../sync/localWorkspaces';
import { LspMethods } from '../../constants';
import logger from '../../services/logger';

describe('virtualKnowledgeFileSystemProvider', () => {
    let provider: virtualKnowledgeFileSystemProvider;
    let workspace: CopilotStudioWorkspace;
    let originalOpenTextDocument: typeof vscode.workspace.openTextDocument;
    let originalShowTextDocument: typeof vscode.window.showTextDocument;

    const workspaceDir = path.join(os.tmpdir(), 'vscode-copilotstudio', 'knowledge', 'files');

    const syncInfo = {
        agentId: 'agent-123',
        dataverseEndpoint: 'https://my-copilotstudio-endpoint'
    };

    const remoteFiles = [
        { schemaName: 'bot.file.test1', fileName: 'file1.txt', relativePath: 'knowledge/files/file1.txt' },
        { schemaName: 'bot.knowledge.test2', fileName: 'file2.txt', relativePath: 'agents/child/knowledge/files/file2.txt' }
    ];

    beforeEach(async () => {
        workspace = {
            workspaceUri: vscode.Uri.file(workspaceDir).toString(),
            displayName: 'Root Agent',
            syncInfo
        } as any;

        await fs.mkdir(workspaceDir, { recursive: true });

        provider = new virtualKnowledgeFileSystemProvider();
        provider.addWorkspace(workspace);

        const lspMod = require('../../services/lspClient');
        lspMod.buildLspRequestPayload = async () => ({});
        lspMod.lspClient = {
            sendRequest: async (method: string, request: any) => {
                if (method === LspMethods.LIST_KNOWLEDGE_FILES) {
                    return { code: 200, files: remoteFiles };
                }
                if (method === LspMethods.DOWNLOAD_KNOWLEDGE_FILES) {
                    const wanted: string[] | undefined = request.schemaNames;
                    const root = vscode.Uri.parse(request.workspaceUri).fsPath;
                    const downloaded = [];
                    for (const file of remoteFiles) {
                        if (wanted && !wanted.includes(file.schemaName)) {
                            continue;
                        }
                        const target = path.join(root, file.relativePath);
                        await fs.mkdir(path.dirname(target), { recursive: true });
                        await fs.writeFile(target, `content-${file.fileName}`);
                        downloaded.push(file);
                    }
                    return { code: 200, downloaded };
                }
                throw new Error(`unexpected method ${method}`);
            }
        };

        (logger.logInfo as any) = (_event: any, _message: string) => {};
        (logger.logError as any) = (_event: any, _message: string, _props?: any) => {};
        (logger.logWarning as any) = (_event: any, _message: string, _props?: any) => {};

        originalOpenTextDocument = vscode.workspace.openTextDocument;
        originalShowTextDocument = vscode.window.showTextDocument;
        (vscode.workspace as any).openTextDocument = async (_target: any) => ({} as any);
        (vscode.window as any).showTextDocument = async (_doc: any, _options?: any) => ({} as any);
    });

    afterEach(async () => {
        (vscode.workspace as any).openTextDocument = originalOpenTextDocument;
        (vscode.window as any).showTextDocument = originalShowTextDocument;
        await fs.rm(path.join(os.tmpdir(), 'vscode-copilotstudio'), { recursive: true, force: true });
    });

    test('refresh populates components map', async () => {
        await provider.refresh();
        const labels = provider.getEntries().map(e => e.label);
        assert.ok(labels.includes('file1.txt (Root Agent)'));
        assert.ok(labels.includes('file2.txt (Root Agent/child)'));
    });

    test('addWorkspace replaces stale workspace so retarget uses new syncInfo', async () => {
        const lspMod = require('../../services/lspClient');
        const seenSyncInfos: any[] = [];
        lspMod.buildLspRequestPayload = async (info: any) => {
            seenSyncInfos.push(info);
            return {};
        };

        await provider.refresh();
        assert.ok(seenSyncInfos.some(info => info?.agentId === 'agent-123'), 'initial refresh should use the original syncInfo');

        const retargetedWorkspace = {
            workspaceUri: workspace.workspaceUri,
            displayName: 'Root Agent',
            syncInfo: {
                agentId: 'agent-456',
                dataverseEndpoint: 'https://my-other-copilotstudio-endpoint'
            }
        } as any;

        seenSyncInfos.length = 0;
        provider.addWorkspace(retargetedWorkspace);
        await provider.refresh();

        assert.ok(seenSyncInfos.length > 0, 'refresh should issue a list request after retarget');
        assert.ok(seenSyncInfos.every(info => info?.agentId === 'agent-456'), 'refresh should use the retargeted syncInfo');
        assert.ok(!seenSyncInfos.some(info => info?.agentId === 'agent-123'), 'refresh must not reuse the stale syncInfo');
    });

    test('stat returns FileStat for known file', async () => {
        await provider.refresh();
        const entry = provider.getEntries().find(e => e.label === 'file1.txt (Root Agent)');
        assert.ok(entry, 'Entry for file1.txt should exist');
        const stat = await provider.stat(entry!.uri);
        assert.strictEqual(stat.type, vscode.FileType.File);
    });

    test('getEntries returns all file names', async () => {
        await provider.refresh();
        const entries = provider.getEntries();
        assert.strictEqual(entries.length, 2);
        const labels = entries.map(e => e.label);
        assert.ok(labels.includes('file1.txt (Root Agent)'));
        assert.ok(labels.includes('file2.txt (Root Agent/child)'));
    });

    test('readFile downloads and returns remote content', async () => {
        await provider.refresh();
        const entry = provider.getEntries().find(e => e.label === 'file1.txt (Root Agent)');
        assert.ok(entry, 'Entry for file1.txt should exist');
        const content = await provider.readFile(entry!.uri);

        assert.strictEqual(content.toString(), 'content-file1.txt');

        const localPath = path.join(workspaceDir, 'knowledge', 'files', 'file1.txt');
        const exists = await fs.stat(localPath).then(() => true).catch(() => false);
        assert.ok(exists, 'Local file should exist after readFile');
    });

    test('readDirectory names round-trip through stat and readFile', async () => {
        await provider.refresh();
        const root = vscode.Uri.parse('virtualKnowledge:/');
        const dir = await provider.readDirectory(root);
        assert.strictEqual(dir.length, 2);

        for (const [name, type] of dir) {
            assert.strictEqual(type, vscode.FileType.File);
            // VS Code builds child URIs by appending the readDirectory name as a path segment.
            const childUri = root.with({ path: `${root.path}${name}` });
            const stat = await provider.stat(childUri);
            assert.strictEqual(stat.type, vscode.FileType.File);
            const content = await provider.readFile(childUri);
            assert.ok(content.toString().startsWith('content-'));
        }
    });

    test('writeFile throws no-permissions', async () => {
        assert.throws(() => provider.writeFile(), vscode.FileSystemError);
    });

    test('delete throws no-permissions', async () => {
        assert.throws(() => provider.delete(), vscode.FileSystemError);
    });

    test('createDirectory throws no-permissions', async () => {
        assert.throws(() => provider.createDirectory(), vscode.FileSystemError);
    });

    test('rename throws no-permissions', async () => {
        assert.throws(() => provider.rename(), vscode.FileSystemError);
    });
});
