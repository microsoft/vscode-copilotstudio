import * as assert from 'node:assert';
import { describe, test, beforeEach, afterEach } from 'node:test';
import * as vscode from 'vscode';
import * as fs from 'fs/promises';
import * as path from 'path';
import * as os from 'os';
import { uploadKnowledgeFiles } from '../../knowledgeFiles/uploadKnowledgeFiles';
import { CopilotStudioWorkspace } from '../../sync/localWorkspaces';
import { LspMethods } from '../../constants';
import logger from '../../services/logger';

describe('uploadKnowledgeFiles', () => {
    let workspaceDir: string;
    let workspace: CopilotStudioWorkspace;
    let sentMethods: string[];
    let repairCalls: number;
    let originalRepair: any;

    beforeEach(async () => {
        workspaceDir = path.join(os.tmpdir(), 'vscode-copilotstudio', 'knowledge', 'files-upload');
        workspace = {
            workspaceUri: vscode.Uri.file(workspaceDir).toString(),
            syncInfo: { agentId: 'agent-123', dataverseEndpoint: 'dataverse-endpoint' }
        } as any;

        await fs.mkdir(workspaceDir, { recursive: true });

        sentMethods = [];
        const lspMod = require('../../services/lspClient');
        lspMod.buildLspRequestPayload = async () => ({});
        lspMod.lspClient = {
            sendRequest: async (method: string, _request: any) => {
                sentMethods.push(method);
                return { code: 200, uploaded: ['file1.txt'] };
            }
        };

        repairCalls = 0;
        const wsMod = require('../../sync/localWorkspaces');
        originalRepair = wsMod.tryRepairAgentManagementEndpoint;
        wsMod.tryRepairAgentManagementEndpoint = async () => {
            repairCalls++;
            return false;
        };

        (logger.logInfo as any) = (_event: any, _message: string) => {};
        (logger.logError as any) = (_event: any, _message: string, _props?: any) => {};
        (logger.logWarning as any) = (_event: any, _message: string, _props?: any) => {};
    });

    afterEach(async () => {
        const wsMod = require('../../sync/localWorkspaces');
        wsMod.tryRepairAgentManagementEndpoint = originalRepair;
        await fs.rm(workspaceDir, { recursive: true, force: true });
    });

    test('upload knowledge files', async () => {
        await uploadKnowledgeFiles(workspace);
        assert.ok(sentMethods.includes(LspMethods.UPLOAD_KNOWLEDGE_FILES));
    });

    test('returns early when syncInfo.agentId is missing (component collection workspace)', async () => {
        const collectionWorkspace = {
            workspaceUri: vscode.Uri.file(workspaceDir).toString(),
            syncInfo: { agentId: undefined, dataverseEndpoint: 'dataverse-endpoint' }
        } as any;

        await uploadKnowledgeFiles(collectionWorkspace);

        assert.strictEqual(sentMethods.length, 0);
    });

    test('repairs missing agentManagementEndpoint before uploading', async () => {
        await uploadKnowledgeFiles(workspace);

        assert.strictEqual(repairCalls, 1);
        assert.ok(sentMethods.includes(LspMethods.UPLOAD_KNOWLEDGE_FILES));
    });

    test('skips repair when agentManagementEndpoint is already present', async () => {
        const repairedWorkspace = {
            workspaceUri: vscode.Uri.file(workspaceDir).toString(),
            syncInfo: { agentId: 'agent-123', dataverseEndpoint: 'dataverse-endpoint', agentManagementEndpoint: 'mgmt-endpoint' }
        } as any;

        await uploadKnowledgeFiles(repairedWorkspace);

        assert.strictEqual(repairCalls, 0);
        assert.ok(sentMethods.includes(LspMethods.UPLOAD_KNOWLEDGE_FILES));
    });
});
