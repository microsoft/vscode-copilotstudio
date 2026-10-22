import * as vscode from 'vscode';

const AUTH_PROVIDER_ID = 'microsoft';
const VSCODE_CLIENT_ID = 'VSCODE_CLIENT_ID:41c3658f-468d-40c9-92cd-2af217093eaa';

export const BAP_SCOPE = 'https://api.bap.microsoft.com/.default';

export const resourceScope = (resourceUrl: string): string => {
    const resource = vscode.Uri.parse(resourceUrl);
    return vscode.Uri.from({ scheme: resource.scheme, authority: resource.authority, path: '/.default' }).toString(true);
};

export const signedInAccount = async (): Promise<vscode.AuthenticationSessionAccountInformation> => {
    const accounts = await vscode.authentication.getAccounts(AUTH_PROVIDER_ID);

    if (accounts.length === 0) {
        const provider = vscode.extensions.all.find(candidate => candidate.id === 'vscode.microsoft-authentication');
        const providerState = provider ? (provider.isActive ? 'active' : 'installed but not active') : 'NOT INSTALLED';

        throw new Error([
            `no Microsoft account is cached in the test profile (auth provider: ${providerState}).`,
            'VS Code refuses modal dialogs in test mode, so sign-in cannot happen inside a run.',
            'Seed the profile once with:  npm run test:ui:signin',
        ].join(' '));
    }

    return accounts[0];
};

export const signIn = async (scope: string): Promise<vscode.AuthenticationSession> => {
    const scopes = [VSCODE_CLIENT_ID, scope];
    const existing = await vscode.authentication.getSession(AUTH_PROVIDER_ID, scopes, { createIfNone: false, silent: true });

    if (existing) {
        return existing;
    }

    const account = await signedInAccount();
    const forAccount = await vscode.authentication.getSession(AUTH_PROVIDER_ID, scopes, { createIfNone: false, silent: true, account });

    if (forAccount) {
        return forAccount;
    }

    throw new Error([
        `no cached Microsoft session for scope '${scope}' (account ${account.label}).`,
        'VS Code matches cached sessions by exact scope set, and consent cannot be granted in test mode.',
        'Re-run  npm run test:ui:signin  and make sure you CLONE an agent as well as signing in —',
        'a clone is what grants the Dataverse scope this run needs.',
    ].join(' '));
};

export interface AgentConnection {
    DataverseEndpoint: string;
    EnvironmentId: string;
    AgentId: string;
}

export interface BotComponentRow {
    name: string;
    schemaname: string;
}

const dataverseApi = (dataverseUrl: string): string => `${dataverseUrl.replace(/\/$/, '')}/api/data/v9.2`;

const authorized = (accessToken: string): Record<string, string> => ({
    Authorization: ['Bearer', accessToken].join(' '),
    Accept: 'application/json',
});

export class DataverseError extends Error {
    readonly status: number;

    constructor(status: number, message: string) {
        super(message);
        this.status = status;
    }
}

const send = async (url: string, accessToken: string, method = 'GET'): Promise<string> => {
    const response = await fetch(url, { method, headers: authorized(accessToken) });
    const body = await response.text();

    if (!response.ok) {
        throw new DataverseError(response.status, `Dataverse ${method} failed: ${response.status} ${body}`);
    }

    return body;
};

export const listRows = async <T>(dataverseUrl: string, accessToken: string, odata: string): Promise<T[]> => {
    const rows: T[] = [];
    let next: string | undefined = `${dataverseApi(dataverseUrl)}/${odata}`;

    while (next) {
        const page = JSON.parse(await send(next, accessToken)) as { value: T[]; '@odata.nextLink'?: string };
        rows.push(...(page.value ?? []));
        next = page['@odata.nextLink'];
    }

    return rows;
};

export const deleteRow = async (dataverseUrl: string, accessToken: string, entitySet: string, id: string): Promise<void> => {
    await send(`${dataverseApi(dataverseUrl)}/${entitySet}(${id})`, accessToken, 'DELETE');
};

export const environmentDataverseUrl = async (environmentId: string, bapToken: string): Promise<string | undefined> => {
    const response = await fetch(
        `https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/environments/${environmentId}?api-version=2020-06-01`,
        { headers: authorized(bapToken) });

    if (!response.ok) {
        throw new Error(`could not resolve environment '${environmentId}' through BAP: ${response.status} ${await response.text()}`);
    }

    const body = JSON.parse(await response.text()) as { properties?: { linkedEnvironmentMetadata?: { instanceUrl?: string } } };
    return body.properties?.linkedEnvironmentMetadata?.instanceUrl;
};

export const listBotComponents = async (connection: AgentConnection, accessToken: string): Promise<BotComponentRow[]> =>
    listRows<BotComponentRow>(
        connection.DataverseEndpoint,
        accessToken,
        `botcomponents?$select=name,schemaname&$filter=_parentbotid_value eq ${connection.AgentId}`);

export interface WorkflowRow {
    workflowid: string;
    name: string;
    description: string | null;
    statecode: number;
}

export const WORKFLOW_SELECT = 'workflowid,name,description,statecode';

export const cloudWorkflow = async (dataverseUrl: string, accessToken: string, workflowId: string): Promise<WorkflowRow | undefined> =>
    (await listRows<WorkflowRow>(dataverseUrl, accessToken, `workflows?$select=${WORKFLOW_SELECT}&$filter=workflowid eq ${workflowId}`))[0];

export interface ComponentState {
    id: string;
    schemaName: string;
    name: string;
    body: string;
}

export const componentStates = async (connection: AgentConnection, accessToken: string): Promise<ComponentState[]> =>
    (await listRows<Record<string, string>>(
        connection.DataverseEndpoint,
        accessToken,
        `botcomponents?$select=botcomponentid,name,schemaname,content,data&$filter=_parentbotid_value eq ${connection.AgentId}`))
        .map(row => ({
            id: row.botcomponentid ?? '',
            schemaName: row.schemaname ?? '',
            name: row.name ?? '',
            body: `${row.content ?? ''}\n${row.data ?? ''}`,
        }))
        .sort((left, right) => left.id.localeCompare(right.id));

export const describeComponentDrift = (before: readonly ComponentState[], after: readonly ComponentState[]): string[] => {
    const beforeById = new Map(before.map(state => [state.id, state]));
    const afterById = new Map(after.map(state => [state.id, state]));
    const label = (state: ComponentState): string => state.schemaName || state.name || state.id;
    const identical = (left: ComponentState, right: ComponentState): boolean =>
        left.body === right.body && left.name === right.name && left.schemaName === right.schemaName;
    const drift: string[] = [];

    for (const [id, state] of beforeById) {
        const current = afterById.get(id);

        if (!current) {
            drift.push(`removed ${label(state)}`);
        } else if (!identical(state, current)) {
            drift.push(`rewrote ${label(state)}`);
        }
    }

    for (const [id, state] of afterById) {
        if (!beforeById.has(id)) {
            drift.push(`added ${label(state)}`);
        }
    }

    return drift.sort();
};

export const componentContent = async (connection: AgentConnection, accessToken: string, schemaNameFragment: string): Promise<string> =>
    (await componentStates(connection, accessToken))
        .filter(state => state.schemaName.toLowerCase().includes(schemaNameFragment.toLowerCase()))
        .map(state => state.body)
        .join('\n');

export const componentNames = async (connection: AgentConnection, accessToken: string): Promise<string[]> =>
    (await listBotComponents(connection, accessToken)).map(row => (row.name ?? '').toLowerCase()).filter(Boolean);

export const namesCarry = (names: readonly string[], expected: string): boolean =>
    expected.length > 0 && names.includes(expected.toLowerCase());

export const componentLabels = async (connection: AgentConnection, accessToken: string): Promise<string[]> =>
    (await listBotComponents(connection, accessToken)).flatMap(row => [row.name ?? '', row.schemaname ?? '']).filter(Boolean);

const normalise = (value: string): string => value.toLowerCase().replace(/[^a-z0-9]/g, '');

const identityParts = (label: string): string[] => {
    const afterLastDot = label.slice(label.lastIndexOf('.') + 1);
    const underscore = label.indexOf('_');

    return [label, afterLastDot, underscore >= 0 ? label.slice(underscore + 1) : label].map(normalise);
};

export const labelsCarry = (labels: readonly string[], expected: string): boolean => {
    const wanted = normalise(expected);

    return wanted.length > 0 && labels.some(label => identityParts(label).includes(wanted));
};