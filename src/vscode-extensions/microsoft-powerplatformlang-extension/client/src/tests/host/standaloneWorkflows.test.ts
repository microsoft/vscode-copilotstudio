import * as assert from 'node:assert';
import { describe, test, beforeEach, afterEach } from 'node:test';
import * as vscode from 'vscode';
import { LspMethods, TelemetryEventsKeys } from '../../constants';
import logger, { formatEnvironmentId, formatPii, PiiRedactionType, prepareLogData } from '../../services/logger';
import {
    cloneStandaloneWorkflow,
    createStandaloneWorkflow,
    deleteStandaloneWorkflow,
    listStandaloneWorkflows,
    reportStandaloneWorkflowFailure,
    resolveStandaloneWorkflowProject,
    StandaloneWorkflowRequestError,
} from '../../workflows/standaloneWorkflows';

describe('standaloneWorkflows', () => {
    const environmentInfo = {
        environmentId: 'env-01',
        dataverseUrl: 'https://contoso.crm.dynamics.com',
        agentManagementUrl: 'https://contoso.agents.microsoft.com',
        displayName: 'Contoso',
    } as any;

    const syncInfo = {
        agentId: '00000000-0000-0000-0000-000000000000',
        dataverseEndpoint: 'https://contoso.crm.dynamics.com',
        environmentId: 'env-01',
        accountInfo: { accountId: 'account.tenant', accountEmail: 'dev@contoso.com', tenantId: 'tenant' },
    } as any;

    const workflow = {
        workflowId: '11111111-2222-3333-4444-555555555555',
        name: 'My Flow',
        isActivated: true,
        connectionReferences: ['new_weather'],
    };

    let sent: { method: string; request: any }[];

    const stubLsp = (respond: (method: string, request: any) => unknown) => {
        const lspMod = require('../../services/lspClient');
        lspMod.buildLspRequestPayload = async () => ({ environmentInfo });
        lspMod.lspClient = {
            sendRequest: async (method: string, request: any) => {
                sent.push({ method, request });
                return respond(method, request);
            },
        };
    };

    beforeEach(() => {
        sent = [];
    });

    test('list returns the workflows the server reported', async () => {
        stubLsp(() => ({ code: 200, message: '', workflows: [workflow] }));

        const workflows = await listStandaloneWorkflows(environmentInfo, {});

        assert.strictEqual(workflows.length, 1);
        assert.strictEqual(workflows[0].name, 'My Flow');
        assert.strictEqual(sent[0].method, LspMethods.LIST_STANDALONE_WORKFLOWS);
    });

    test('list forwards the filter and the limit', async () => {
        stubLsp(() => ({ code: 200, message: '', workflows: [] }));

        await listStandaloneWorkflows(environmentInfo, {}, 'weather', 5);

        assert.strictEqual(sent[0].request.nameFilter, 'weather');
        assert.strictEqual(sent[0].request.maximumCount, 5);
    });

    test('list treats an absent workflow array as empty', async () => {
        stubLsp(() => ({ code: 200, message: '' }));

        assert.deepStrictEqual(await listStandaloneWorkflows(environmentInfo, {}), []);
    });

    test('a non-success code is raised as an error carrying the code', async () => {
        stubLsp(() => ({ code: 403, message: 'not allowed' }));

        await assert.rejects(
            () => listStandaloneWorkflows(environmentInfo, {}),
            (error: StandaloneWorkflowRequestError) => error.code === 403 && error.message === 'not allowed');
    });

    test('an empty response is raised as an error', async () => {
        stubLsp(() => undefined);

        await assert.rejects(() => listStandaloneWorkflows(environmentInfo, {}), StandaloneWorkflowRequestError);
    });

    test('clone sends the workflow id and the root folder', async () => {
        stubLsp(() => ({ code: 200, message: '', workflow, projectFolder: 'MyFlow', folderName: 'MyFlow/workflows/MyFlow-1', changed: true }));

        const response = await cloneStandaloneWorkflow(environmentInfo, {}, workflow.workflowId, 'C:/ws');

        assert.strictEqual(sent[0].method, LspMethods.CLONE_STANDALONE_WORKFLOW);
        assert.strictEqual(sent[0].request.workflowId, workflow.workflowId);
        assert.strictEqual(sent[0].request.rootFolder, 'C:/ws');
        assert.strictEqual(response.projectFolder, 'MyFlow');
        assert.strictEqual(response.folderName, 'MyFlow/workflows/MyFlow-1');
        assert.strictEqual(response.changed, true);
    });

    test('create scaffolds without any environment payload', async () => {
        stubLsp(() => ({ code: 200, message: '', workflowId: 'wf-1', projectFolder: 'MyFlow', folderName: 'MyFlow/workflows/MyFlow-wf-1', writtenFiles: ['MyFlow/workflows/MyFlow-wf-1/workflow.json'] }));

        const response = await createStandaloneWorkflow('My Flow', 'C:/ws');

        assert.strictEqual(sent[0].method, LspMethods.CREATE_STANDALONE_WORKFLOW);
        assert.strictEqual(sent[0].request.name, 'My Flow');
        assert.strictEqual(sent[0].request.rootFolder, 'C:/ws');
        assert.strictEqual(sent[0].request.environmentInfo, undefined);
        assert.strictEqual(response.workflowId, 'wf-1');
        assert.strictEqual(response.projectFolder, 'MyFlow');
    });

    test('the container is opened, not the new project, so an existing workspace survives', () => {
        const root = vscode.Uri.file('C:/ws');
        const resolved = resolveStandaloneWorkflowProject(root, { projectFolder: 'MyFlow', folderName: 'MyFlow/workflows/MyFlow-wf-1' });

        assert.strictEqual(resolved.workspaceRoot.fsPath, root.fsPath);
        assert.strictEqual(resolved.entryFile!.fsPath, vscode.Uri.joinPath(root, 'MyFlow', 'workflows', 'MyFlow-wf-1', 'workflow.json').fsPath);
    });

    test('the container is opened when the server reports no project folder', () => {
        const root = vscode.Uri.file('C:/ws');
        const resolved = resolveStandaloneWorkflowProject(root, { folderName: 'workflows/MyFlow-wf-1' });

        assert.strictEqual(resolved.workspaceRoot.fsPath, root.fsPath);
        assert.strictEqual(resolved.entryFile!.fsPath, vscode.Uri.joinPath(root, 'workflows', 'MyFlow-wf-1', 'workflow.json').fsPath);
    });

    test('no entry file is resolved when the server reports no folder', () => {
        const resolved = resolveStandaloneWorkflowProject(vscode.Uri.file('C:/ws'), { projectFolder: 'MyFlow' });

        assert.strictEqual(resolved.entryFile, undefined);
        assert.strictEqual(resolved.workspaceRoot.fsPath, vscode.Uri.file('C:/ws').fsPath);
    });

    test('the entry file stays beneath the container for a deeply nested project', () => {
        const root = vscode.Uri.file('C:/ws');
        const resolved = resolveStandaloneWorkflowProject(root, { folderName: 'My Flow-wf-1/workflows/My Flow-wf-1' });

        assert.strictEqual(resolved.workspaceRoot.fsPath, root.fsPath);
        assert.strictEqual(
            resolved.entryFile!.fsPath,
            vscode.Uri.joinPath(root, 'My Flow-wf-1', 'workflows', 'My Flow-wf-1', 'workflow.json').fsPath);
    });

    test('delete reports what the server removed', async () => {
        stubLsp(() => ({ code: 200, message: '', deleted: true }));

        const deleted = await deleteStandaloneWorkflow(environmentInfo, {}, workflow.workflowId);

        assert.strictEqual(sent[0].method, LspMethods.DELETE_STANDALONE_WORKFLOW);
        assert.strictEqual(sent[0].request.workflowId, workflow.workflowId);
        assert.strictEqual(deleted, true);
    });

    test('delete of an already absent flow is not an error', async () => {
        stubLsp(() => ({ code: 200, message: '', deleted: false }));

        assert.strictEqual(await deleteStandaloneWorkflow(environmentInfo, {}, workflow.workflowId), false);
    });

    test('delete surfaces a server failure', async () => {
        stubLsp(() => ({ code: 403, message: 'not allowed' }));

        await assert.rejects(
            () => deleteStandaloneWorkflow(environmentInfo, {}, workflow.workflowId),
            (error: unknown) => error instanceof StandaloneWorkflowRequestError && error.code === 403);
    });
});

describe('reportStandaloneWorkflowFailure', () => {
    type Reported = { event: string; message?: string; data?: Record<string, unknown> };

    let reported: Reported[];
    let original: typeof logger.logError;

    const telemetryOf = (entry: Reported) =>
        prepareLogData(entry.message, { errorMessage: entry.data?.error as string });

    beforeEach(() => {
        reported = [];
        original = logger.logError;
        logger.logError = ((event: string, message?: string, data?: Record<string, unknown>) => {
            reported.push({ event, message, data });
        }) as typeof logger.logError;
    });

    afterEach(() => {
        logger.logError = original;
    });

    test('reports a failure exactly once', () => {
        reportStandaloneWorkflowFailure(
            TelemetryEventsKeys.CloneAgentError,
            'Error cloning cloud flow',
            new Error('The environment refused the request.'),
            'The cloud flow could not be cloned.',
            'My Flow');

        assert.strictEqual(reported.length, 1);
        assert.strictEqual(reported[0].event, TelemetryEventsKeys.CloneAgentError);
    });

    test('redacts the workflow name the server echoed back while keeping the reason readable', () => {
        reportStandaloneWorkflowFailure(
            TelemetryEventsKeys.CloneAgentError,
            'Error cloning cloud flow',
            new Error("A workflow already occupies 'workflows/My Flow-1'."),
            'The cloud flow could not be cloned.',
            'My Flow');

        const prepared = telemetryOf(reported[0]);

        assert.strictEqual(
            prepared.telemetryProperties.errorMessage,
            "A workflow already occupies 'workflows/[REDACTED WORKFLOW NAMES]-1'.");
        assert.strictEqual(
            prepared.displayMessage,
            "Error cloning cloud flow: A workflow already occupies 'workflows/My Flow-1'.");
    });

    test('redacts every occurrence of the workflow name', () => {
        reportStandaloneWorkflowFailure(
            TelemetryEventsKeys.CloneAgentError,
            'Error deleting cloud flow',
            new Error("The workflow 'My Flow' could not be deleted because My Flow is in use."),
            'The cloud flow could not be deleted.',
            'My Flow');

        const telemetry = telemetryOf(reported[0]).telemetryProperties.errorMessage;

        assert.ok(!telemetry.includes('My Flow'));
        assert.strictEqual(telemetry.split('[REDACTED WORKFLOW NAMES]').length - 1, 2);
    });

    test('leaves a reason that names no workflow unchanged', () => {
        reportStandaloneWorkflowFailure(
            TelemetryEventsKeys.CloneAgentError,
            'Error cloning cloud flow',
            new Error('Request timed out after 30 seconds'),
            'The cloud flow could not be cloned.',
            'My Flow');

        assert.strictEqual(
            telemetryOf(reported[0]).telemetryProperties.errorMessage,
            'Request timed out after 30 seconds');
    });

    test('still redacts paths and emails the reason carries', () => {
        reportStandaloneWorkflowFailure(
            TelemetryEventsKeys.CloneAgentError,
            'Error creating workflow',
            new Error('Could not write C:\\flows\\My Flow\\workflow.json for alex@contoso.com'),
            'The workflow could not be created.',
            'My Flow');

        const telemetry = telemetryOf(reported[0]).telemetryProperties.errorMessage;

        assert.ok(telemetry.includes('[REDACTED .JSON FILE NAME]'));
        assert.ok(telemetry.includes('[REDACTED EMAIL ADDRESS]'));
        assert.ok(!telemetry.includes('My Flow'));
    });

    test('falls back to the supplied reason when the failure carries no message', () => {
        reportStandaloneWorkflowFailure(
            TelemetryEventsKeys.CloneAgentError,
            'Error creating workflow',
            undefined,
            'The workflow could not be created.');

        assert.strictEqual(
            telemetryOf(reported[0]).displayMessage,
            'Error creating workflow: The workflow could not be created.');
    });

    test('reports without a workflow name when none is known yet', () => {
        reportStandaloneWorkflowFailure(
            TelemetryEventsKeys.CloneAgentError,
            'Error cloning cloud flow',
            new Error('Sign-in was cancelled.'),
            'The cloud flow could not be cloned.');

        assert.strictEqual(
            telemetryOf(reported[0]).telemetryProperties.errorMessage,
            'Sign-in was cancelled.');
    });

    test('redacts the project folder the server derived from the workflow name', () => {
        reportStandaloneWorkflowFailure(
            TelemetryEventsKeys.CloneAgentError,
            'Error cloning cloud flow',
            new Error("The folder 'MyFlow-7f1c2a' already holds another project."),
            'The cloud flow could not be cloned.',
            'My Flow');

        const telemetry = telemetryOf(reported[0]).telemetryProperties.errorMessage;

        assert.ok(!telemetry.includes('MyFlow'));
        assert.ok(telemetry.includes('[REDACTED WORKFLOW NAMES]'));
    });

    test('redacts the environment identifier a failure echoed back', () => {
        reportStandaloneWorkflowFailure(
            TelemetryEventsKeys.CloneAgentError,
            'Error cloning cloud flow',
            new Error('No workflow with id 4a1ee82e-53b2-4648-9be2-686ec900199d exists in d3e21992-3138-ec9e-855f-94ae510c3a1e.'),
            'The cloud flow could not be cloned.',
            'My Flow',
            'd3e21992-3138-ec9e-855f-94ae510c3a1e');

        const prepared = telemetryOf(reported[0]);

        assert.ok(!prepared.telemetryProperties.errorMessage.includes('d3e21992-3138-ec9e-855f-94ae510c3a1e'));
        assert.ok(prepared.telemetryProperties.errorMessage.includes('[REDACTED ENVIRONMENT ID]'));
        assert.ok(prepared.displayMessage?.includes('d3e21992-3138-ec9e-855f-94ae510c3a1e'));
    });

    test('leaves the environment identifier alone when the caller knows none', () => {
        reportStandaloneWorkflowFailure(
            TelemetryEventsKeys.CloneAgentError,
            'Error creating workflow',
            new Error('The workflow could not be written.'),
            'The workflow could not be created.',
            'My Flow');

        assert.strictEqual(
            telemetryOf(reported[0]).telemetryProperties.errorMessage,
            'The workflow could not be written.');
    });

    test('redacts a name whose separators the server replaced', () => {
        reportStandaloneWorkflowFailure(
            TelemetryEventsKeys.CloneAgentError,
            'Error cloning cloud flow',
            new Error("'My-Flow' and 'My_Flow' and 'My Flow' all name the same workflow."),
            'The cloud flow could not be cloned.',
            'My Flow');

        const telemetry = telemetryOf(reported[0]).telemetryProperties.errorMessage;

        assert.strictEqual(telemetry.split('[REDACTED WORKFLOW NAMES]').length - 1, 3);
    });

    test('does not redact an unrelated name that merely shares a prefix', () => {
        reportStandaloneWorkflowFailure(
            TelemetryEventsKeys.CloneAgentError,
            'Error cloning cloud flow',
            new Error('MyFlowchart was not affected.'),
            'The cloud flow could not be cloned.',
            'My Flow');

        assert.strictEqual(
            telemetryOf(reported[0]).telemetryProperties.errorMessage,
            'MyFlowchart was not affected.');
    });
});

describe('standalone workflow telemetry redaction', () => {
    test('an environment display name is redacted in telemetry but stays readable in the output channel', () => {
        const prepared = prepareLogData(undefined, {
            message: `${formatPii('Contoso Dev (default)', PiiRedactionType.EnvironmentName)}: Loaded 3 workflow(s)`,
        });

        assert.strictEqual(prepared.telemetryProperties.message, '[REDACTED ENVIRONMENT NAME]: Loaded 3 workflow(s)');
        assert.strictEqual(prepared.outputMessage, 'Contoso Dev (default): Loaded 3 workflow(s)');
    });

    test('an environment id is redacted in the telemetry dimension', () => {
        const prepared = prepareLogData(undefined, {
            environmentId: formatEnvironmentId('Default-a30263b9-1caf-4db5-ab53-ed3850c0bd1f'),
        });

        assert.strictEqual(prepared.telemetryProperties.environmentId, '[REDACTED ENVIRONMENT ID]');
    });

    test('a redacted environment id never carries the raw value into telemetry', () => {
        const environmentId = 'Default-a30263b9-1caf-4db5-ab53-ed3850c0bd1f';
        const prepared = prepareLogData(undefined, {
            message: 'Loaded 3 workflow(s)',
            environmentId: formatEnvironmentId(environmentId),
        });

        assert.ok(!JSON.stringify(prepared.telemetryProperties).includes(environmentId));
        assert.strictEqual(prepared.telemetryProperties.message, 'Loaded 3 workflow(s)');
    });
});

describe('standalone workflow activation', () => {
    const extension = () => vscode.extensions.all.find(e => e.id === 'ms-CopilotStudio.vscode-copilotstudio');

    test('a standalone workflow project marker activates the extension', () => {
        const found = extension();

        assert.ok(found, 'extension not found');
        assert.ok((found!.packageJSON.activationEvents as string[]).includes('workspaceContains:**/workflow.sync.yaml'));
    });

    test('the agent markers still activate the extension', () => {
        const activationEvents = extension()!.packageJSON.activationEvents as string[];

        for (const marker of ['**/agent.mcs.yml', '**/settings.mcs.yml', '**/agent.sync.yaml']) {
            assert.ok(activationEvents.includes(`workspaceContains:${marker}`), marker);
        }
    });
});
