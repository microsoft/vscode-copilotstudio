import * as assert from 'assert';
import * as vscode from 'vscode';
import * as fs from 'fs/promises';
import * as path from 'path';
import * as os from 'os';
import { uploadKnowledgeFiles } from '../../knowledgeFiles/uploadKnowledgeFiles';
import { CopilotStudioWorkspace } from '../../sync/localWorkspaces';
import { getDataverseBotHandler, getFilesDir, getTrackPath } from '../../knowledgeFiles/syncUtils';
import { loadChangeTrack, saveChangeTrack, isTextFile, resolveConflict } from '../../knowledgeFiles/fileHelper';
import logger from '../../services/logger';
import { ConflictResolution } from '../../constants';

suite('uploadKnowledgeFiles', () => {
    let workspaceDir: string;
    let trackPath: string;
    let workspace: CopilotStudioWorkspace;

    setup(async () => {
        workspaceDir = path.join(os.tmpdir(), 'vscode-copilotstudio', 'knowledge', 'files-upload');
        trackPath = path.join(workspaceDir, '.mcs', 'filechangetrack.json');
        workspace = {
            workspaceUri: vscode.Uri.file(workspaceDir),
            syncInfo: { agentId: 'agent-123', dataverseEndpoint: 'dataverse-endpoint' }
        } as any;

        await fs.mkdir(workspaceDir, { recursive: true });
        await fs.mkdir(path.dirname(trackPath), { recursive: true });

        await fs.writeFile(path.join(workspaceDir, 'file1.txt'), 'local-1');
        await fs.writeFile(path.join(workspaceDir, 'file2.txt'), 'local-2');

        (loadChangeTrack as any) = async () => ({
            'file1.txt': { localModifiedOn: 0 },
            'file2.txt': { localModifiedOn: 0 }
        });
        (saveChangeTrack as any) = async (_path: string, _track: any) => {};
        (isTextFile as any) = async (_file: string) => true;
        (resolveConflict as any) = async (_file: string, _local: string, _remote: Buffer, _isText: boolean) => ConflictResolution.UseLocal;
        (getTrackPath as any) = (_ws: vscode.Uri) => trackPath;
        (getFilesDir as any) = (_ws: vscode.Uri) => workspaceDir;    
        
        (getDataverseBotHandler as any) = async () => ({
            listWsComponentMetadata: async () => [
                { id: '1', schemaName: 'bot.file.file1s', modifiedOn: Date.now(), filename: 'file1.txt', sizeInBytes: 7, agentSchemaName: 'bot.file.file1' },
                { id: '2', schemaName: 'bot.file.file2s', modifiedOn: Date.now(), filename: 'file2.txt', sizeInBytes: 7, agentSchemaName: 'bot.file.file2' }
            ],
            downloadKnowledgeFile: async (id: string) => Buffer.from(`remote-${id}`),
            getChildAgents: async (_syncInfo: any, _prefix: string) => [],
            getBotPrefix: async () => 'bot',
            getBotComponentId: async (_file: string, _schema: string, _agentId: string) => `id`,
            dataverseHttpRequest: async () => ({ statusCode: 200, headers: {}, body: Buffer.from('') }),
            deleteBotComponent: async (_id: string) => {}
        });

        (logger.logInfo as any) = (_event: any, _message: string) => {};
        (logger.logError as any) = (_event: any, _message: string, _props?: any) => {};
    });

    teardown(async () => {
        await fs.rm(workspaceDir, { recursive: true, force: true });
    });

    test('uploads files and updates change track', async () => {
        await uploadKnowledgeFiles(workspace);

        const file1 = await fs.readFile(path.join(workspaceDir, 'file1.txt'), 'utf8');
        const file2 = await fs.readFile(path.join(workspaceDir, 'file2.txt'), 'utf8');

        assert.strictEqual(file1, 'local-1');
        assert.strictEqual(file2, 'local-2');
    });
});
