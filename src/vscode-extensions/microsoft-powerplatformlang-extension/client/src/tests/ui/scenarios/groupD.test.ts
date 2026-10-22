import { describe, test } from 'node:test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { SHAPES, runsScenario } from '../lib/shapes';
import { runScenario, settings, normaliseId, workPathFor } from '../lib/run';
import { ensureCloned, cloneInto, trackedWorkspace } from '../lib/workingCopies';
import { runCommand } from '../lib/commands';
import { signedInAccount } from '../lib/dataverse';
import { assertThat, detail } from '../lib/log';
import { listFiles } from '../lib/tree';

const KNOWLEDGE_REFRESH_COMMAND = 'microsoft-copilot-studio.refreshKnowledgeFilesTreeView';
const KNOWLEDGE_DOWNLOAD_COMMAND = 'microsoft-copilot-studio.downloadAllKnowledgeFiles';
const AGENT_TREE_REFRESH_COMMAND = 'microsoft-copilot-studio.refreshAgentTreeView';
const SELECT_ACCOUNT_COMMAND = 'microsoft-copilot-studio.selectAgentAccount';
const MANAGE_CONNECTIONS_COMMAND = 'microsoft-copilot-studio.manageConnections';

const VIRTUAL_KNOWLEDGE_SCHEME = 'virtualKnowledge';
const NOT_CONNECTED_TEXT = 'not connected to an environment';

const virtualKnowledgeUri = (encodedName = ''): vscode.Uri =>
    vscode.Uri.parse(`${VIRTUAL_KNOWLEDGE_SCHEME}:/${encodedName}`);

const readAccountBinding = (agentRoot: string): { accountId?: string; accountEmail?: string } => {
    const raw = JSON.parse(fs.readFileSync(path.join(agentRoot, '.mcs', 'conn.json'), 'utf8')) as Record<string, unknown>;
    const info = (raw.AccountInfo ?? raw.accountInfo ?? {}) as Record<string, unknown>;

    return {
        accountId: (info.AccountId ?? info.accountId) as string | undefined,
        accountEmail: (info.AccountEmail ?? info.accountEmail) as string | undefined,
    };
};

const knowledgeDirOf = (agentRoot: string, relativeDir: string): string =>
    path.join(agentRoot, ...relativeDir.split('/'));

describe('Group D — the commands around sync', () => {
    for (const shape of SHAPES) {
        if (runsScenario(shape, 'D-01')) {
            test(`D-01 [${shape.id}] the remote knowledge tree lists what the cloud holds`, { timeout: 1_800_000 }, async () => {
                await runScenario('D-01', 'remote knowledge tree lists the cloud files', shape, async seed => {
                    const pair = await ensureCloned(shape, seed.agentId);

                    await runCommand(KNOWLEDGE_REFRESH_COMMAND, await trackedWorkspace(pair.v1), {
                        timeoutMs: 600_000,
                        label: `${KNOWLEDGE_REFRESH_COMMAND} (${shape.id})`,
                    });

                    const entries = await vscode.workspace.fs.readDirectory(virtualKnowledgeUri());
                    const names = entries.map(([name]) => decodeURIComponent(name));
                    detail('remote knowledge', `${entries.length} entr(ies)${names.length > 0 ? `: ${names.slice(0, 4).join(', ')}` : ''}`);

                    assertThat('the refreshed tree lists remote knowledge files', '> 0 entries', String(entries.length), entries.length > 0);

                    const content = await vscode.workspace.fs.readFile(virtualKnowledgeUri(entries[0][0]));
                    detail('first entry', `${names[0]} — ${content.length} bytes`);
                    assertThat('a listed knowledge file resolves through the virtual provider', '> 0 bytes', String(content.length), content.length > 0);
                });
            });
        }

        if (runsScenario(shape, 'D-02')) {
            test(`D-02 [${shape.id}] downloading all knowledge files writes them to disk`, { timeout: 1_800_000 }, async () => {
                await runScenario('D-02', 'download all writes the knowledge files to disk', shape, async seed => {
                    const pair = await ensureCloned(shape, seed.agentId);
                    const knowledgeDir = knowledgeDirOf(pair.v1.agentRoot, shape.knowledgeRelativeDir);
                    const before = fs.existsSync(knowledgeDir) ? listFiles(knowledgeDir) : [];

                    await runCommand(KNOWLEDGE_DOWNLOAD_COMMAND, await trackedWorkspace(pair.v1), {
                        timeoutMs: 900_000,
                        label: `${KNOWLEDGE_DOWNLOAD_COMMAND} (${shape.id})`,
                    });

                    const after = fs.existsSync(knowledgeDir) ? listFiles(knowledgeDir) : [];
                    detail('knowledge on disk', `${shape.knowledgeRelativeDir} — ${before.length} before, ${after.length} after`);

                    assertThat('the download left knowledge files on disk', '> 0 files', String(after.length), after.length > 0);
                    assertThat('the download removed nothing that was already there', `>= ${before.length} files`, String(after.length), after.length >= before.length);

                    const empty = after.filter(file => fs.statSync(file).size === 0).map(file => path.basename(file));
                    assertThat('every knowledge file on disk has content', 'no empty files', empty.length === 0 ? 'none empty' : empty.join(', '), empty.length === 0);
                });
            });
        }

        if (runsScenario(shape, 'D-03')) {
            test(`D-03 [${shape.id}] refreshing the agents tree still resolves the run's agent`, { timeout: 1_800_000 }, async () => {
                await runScenario('D-03', "refreshed agents tree resolves the run's agent", shape, async seed => {
                    await runCommand(AGENT_TREE_REFRESH_COMMAND, undefined, {
                        timeoutMs: 600_000,
                        label: `${AGENT_TREE_REFRESH_COMMAND} (${shape.id})`,
                    });

                    const scratch = path.join(workPathFor('d-03'), shape.id);
                    const copy = await cloneInto(seed.agentId, scratch);
                    const marker = path.join(copy.agentRoot, shape.markerFile);

                    detail('cloned after refresh', copy.agentRoot);
                    assertThat('the refreshed tree still clones the run agent', shape.markerFile, fs.existsSync(marker) ? 'present' : 'absent', fs.existsSync(marker));
                    assertThat('the clone bound the agent the refresh offered', seed.agentId, copy.connection.AgentId,
                        normaliseId(copy.connection.AgentId) === normaliseId(seed.agentId));
                });
            });
        }

        if (runsScenario(shape, 'D-04')) {
            test(`D-04 [${shape.id}] selecting the account keeps the workspace on the signed-in account`, { timeout: 1_800_000 }, async () => {
                await runScenario('D-04', 'account selection keeps the signed-in account', shape, async seed => {
                    const pair = await ensureCloned(shape, seed.agentId);
                    const before = readAccountBinding(pair.v1.agentRoot);
                    const account = await signedInAccount();

                    await runCommand(SELECT_ACCOUNT_COMMAND, await trackedWorkspace(pair.v1), {
                        timeoutMs: 600_000,
                        label: `${SELECT_ACCOUNT_COMMAND} (${shape.id})`,
                    });

                    const after = readAccountBinding(pair.v1.agentRoot);
                    detail('account binding', `before ${before.accountId ?? '(none)'} → after ${after.accountId ?? '(none)'}, signed in as ${account.id}`);

                    assertThat('the workspace is bound to an account', 'an account id', after.accountId ?? '(none)', Boolean(after.accountId));
                    assertThat('selecting the account kept the signed-in account', account.id, after.accountId ?? '(none)',
                        normaliseId(after.accountId) === normaliseId(account.id));
                    assertThat('the agent binding was left alone', seed.agentId, pair.v1.connection.AgentId,
                        normaliseId(pair.v1.connection.AgentId) === normaliseId(seed.agentId));
                });
            });
        }

        if (runsScenario(shape, 'D-05')) {
            test(`D-05 [${shape.id}] manage connections opens for a connected agent`, { timeout: 1_800_000 }, async () => {
                await runScenario('D-05', 'manage connections opens for a connected agent', shape, async seed => {
                    const pair = await ensureCloned(shape, seed.agentId);
                    const workspace = await trackedWorkspace(pair.v1);

                    const seen = await runCommand(MANAGE_CONNECTIONS_COMMAND, workspace, {
                        timeoutMs: 600_000,
                        label: `${MANAGE_CONNECTIONS_COMMAND} (${shape.id})`,
                    });

                    const refusal = seen.find(record => record.message.includes(NOT_CONNECTED_TEXT));
                    const complaint = seen.find(record => record.message.includes('Open or select a connected agent'));
                    detail('manage connections', seen.length === 0 ? 'opened without a message' : seen.map(record => record.message).join(' | '));

                    assertThat('a connected agent is not refused as unconnected', 'no refusal', refusal?.message ?? 'no refusal', refusal === undefined);
                    assertThat('the command resolved the workspace it was handed', 'resolved', complaint?.message ?? 'resolved', complaint === undefined);

                    await vscode.commands.executeCommand('workbench.action.closeAllEditors');

                    const conn = fs.existsSync(path.join(pair.v1.agentRoot, '.mcs', 'conn.json'));
                    assertThat('managing connections left the workspace attached', 'conn.json present', conn ? 'present' : 'absent', conn);
                    assertThat('the workspace still points at the run environment', settings.environmentId, pair.v1.connection.EnvironmentId,
                        normaliseId(pair.v1.connection.EnvironmentId) === normaliseId(settings.environmentId));
                });
            });
        }
    }
});
