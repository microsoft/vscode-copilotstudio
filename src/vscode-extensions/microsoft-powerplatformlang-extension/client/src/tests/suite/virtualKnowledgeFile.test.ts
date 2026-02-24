import * as assert from 'assert';
import * as vscode from 'vscode';
import * as fs from 'fs/promises';
import * as path from 'path';
import * as os from 'os';
import { virtualKnowledgeFileSystemProvider } from '../../knowledgeFiles/virtualKnowledgeFile';
import { CopilotStudioWorkspace } from '../../sync/localWorkspaces';
import { getDataverseBotHandler, getFilePaths, safeSaveFile } from '../../knowledgeFiles/syncUtils';
import { ConflictResolution } from '../../constants';
import logger from '../../services/logger';

suite('virtualKnowledgeFileSystemProvider', () => {
    let provider: virtualKnowledgeFileSystemProvider;
    let workspace: CopilotStudioWorkspace;

    const workspaceDir = path.join(os.tmpdir(), 'vscode-copilotstudio', 'knowledge', 'files');
    const trackPath = path.join(workspaceDir, '.mcs', 'filechangetrack.json');

    const syncInfo = {
        agentId: 'agent-123',
        dataverseEndpoint: 'https://my-copilotstudio-endpoint'
    };

    setup(async () => {
        workspace = {
            workspaceUri: vscode.Uri.file(workspaceDir),
            syncInfo
        } as any;

        await fs.mkdir(workspaceDir, { recursive: true });

        provider = new virtualKnowledgeFileSystemProvider(workspace);

        (getFilePaths as any) = () => ({ filesDir: workspaceDir, trackPath });
        (getDataverseBotHandler as any) = async () => ({
            listWsComponentMetadata: async () => [
                { id: '1', schemaName: 'bot.file.test1', modifiedOn: Date.now(), filename: 'file1.txt' },
                { id: '2', schemaName: 'bot.knowledge.test2', modifiedOn: Date.now(), filename: 'file2.txt' }
            ],
            downloadKnowledgeFile: async (id: string) => Buffer.from(`content-${id}`)
        });

        const track: Record<string, any> = {};
        (require('../../knowledgeFiles/fileHelper').loadChangeTrack as any) = async () => track;
        (require('../../knowledgeFiles/fileHelper').saveChangeTrack as any) = async (_path: string, _track: any) => {};
        (require('../../knowledgeFiles/fileHelper').isTextFile as any) = async (_file: string) => true;
        (require('../../knowledgeFiles/fileHelper').resolveConflict as any) = async (_f: any, _lp: any, _remote: any, _isText: any) => ConflictResolution.UseRemote;
        (safeSaveFile as any) = async (dest: string, tmp: string, buf: Buffer) => {
            await fs.mkdir(path.dirname(dest), { recursive: true });
            await fs.writeFile(dest, buf);
        };

        (logger.logInfo as any) = (_event: any, _message: string) => {};
        (logger.logError as any) = (_event: any, _message: string, _props?: any) => {};
    });

    teardown(async () => {
        await fs.rm(path.join(os.tmpdir(), 'vscode-copilotstudio'), { recursive: true, force: true });
    });

    test('refresh populates components map', async () => {
        await provider.refresh();
        const keys = Array.from(provider['components'].keys());
        assert.ok(keys.includes('file1.txt (bot.file.test1)'));
        assert.ok(keys.includes('file2.txt (bot.knowledge.test2)'));
    });

    test('stat returns FileStat for known file', async () => {
        await provider.refresh();
        const key = 'file1.txt (bot.file.test1)';
        const stat = await provider.stat(vscode.Uri.parse(`virtualKnowledge:/${key}`));
        assert.strictEqual(stat.type, vscode.FileType.File);
    });

    test('readDirectory returns all file names', async () => {
        await provider.refresh();
        const files = await provider.readDirectory(vscode.Uri.parse('virtualKnowledge:/'));
        const names = files.map(f => f[0]);
        assert.ok(names.includes('file1.txt (bot.file.test1)'));
        assert.ok(names.includes('file2.txt (bot.knowledge.test2)'));
    });

    test('readFile downloads and saves remote content', async () => {(
        require('../../knowledgeFiles/fileHelper').isTextFile as any) = async (_file: string) => false;
        await provider.refresh();
        const uri = vscode.Uri.parse('virtualKnowledge:/file1.txt (bot.file.test1)');
        const content = await provider.readFile(uri);

        assert.strictEqual(content.toString(), 'content-1');

        const localPath = path.join(workspaceDir, 'file1.txt');
        const exists = await fs.stat(localPath).then(() => true).catch(() => false);
        assert.ok(exists, 'Local file should exist after readFile');

        const localContent = await fs.readFile(localPath, 'utf8');
        assert.strictEqual(localContent, 'content-1');
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
