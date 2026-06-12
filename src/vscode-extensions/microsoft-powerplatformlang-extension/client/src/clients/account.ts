import { authentication, AuthenticationGetSessionOptions, Uri, Disposable } from "vscode";
import logger from "../services/logger";
import { CoreServicesClusterCategory, TelemetryEventsKeys } from "../constants";

export interface TokenInfo {
    accessToken: string;
    accountId: string;
    tenantId: string;
    accountEmail?: string;
}

export interface AccessTokenResponse {
    response: Response;
    tokenInfo: TokenInfo;
}

let clearSession = false;
const MICROSOFT_PROVIDER_ID = 'microsoft';
const VSCODE_CLIENT_ID = "VSCODE_CLIENT_ID:41c3658f-468d-40c9-92cd-2af217093eaa";

// Coalescing lock for interactive auth - prevents multiple concurrent consent dialogs
let pendingInteractiveAuth: Promise<boolean> | null = null;

export interface PreferredTreeAccount {
    accountId: string;
    accountEmail?: string;
}

let preferredTreeAccount: PreferredTreeAccount | undefined;
let noAccountCancellationNonce = 0;

export function getPreferredTreeAccount(): PreferredTreeAccount | undefined {
    return preferredTreeAccount;
}

const signInCancelled = new Set<string>();

function cancellationKey(scopes: string[], accountId?: string, accountHint?: string): string {
    const identity = (accountId ?? accountHint)?.toLowerCase();
    if (identity) {
        return `${identity}|${scopes.join(' ')}`;
    }

    noAccountCancellationNonce += 1;
    return `anonymous:${noAccountCancellationNonce}|${scopes.join(' ')}`;
}

function isCancellationError(error: unknown): boolean {
    const message = error instanceof Error ? error.message : String(error);
    return /cancel(l)?ed|user did not consent/i.test(message);
}

export function clearSignInCancellation(): void {
    signInCancelled.clear();
}

export function isSignInCancelled(): boolean {
    return signInCancelled.size > 0;
}

export async function hasStoredAccount(accountId?: string, accountHint?: string): Promise<boolean> {
    if (!accountId && !accountHint) {
        const accounts = await authentication.getAccounts(MICROSOFT_PROVIDER_ID);
        return accounts.length > 0;
    }
    const accounts = await authentication.getAccounts(MICROSOFT_PROVIDER_ID);
    return !!findStoredAccount(accounts, accountId, accountHint);
}

export async function listStoredAccounts(): Promise<{ accountId: string; accountEmail?: string }[]> {
    const accounts = await authentication.getAccounts(MICROSOFT_PROVIDER_ID);
    const seen = new Set<string>();
    const result: { accountId: string; accountEmail?: string }[] = [];
    for (const a of accounts) {
        if (seen.has(a.id)) {
            continue;
        }
        seen.add(a.id);
        result.push({ accountId: a.id, accountEmail: a.label });
    }
    return result;
}

function findStoredAccount(
    accounts: readonly import('vscode').AuthenticationSessionAccountInformation[],
    accountId?: string,
    accountHint?: string
): import('vscode').AuthenticationSessionAccountInformation | undefined {
    if (accountId) {
        const byId = accounts.find(a => a.id === accountId);
        if (byId) {
            return byId;
        }
    }
    if (accountHint) {
        const hintLower = accountHint.toLowerCase();
        return accounts.find(a => a.label?.toLowerCase() === hintLower);
    }
    return undefined;
}

const pendingSilentSessions = new Map<string, Promise<import('vscode').AuthenticationSession | undefined>>();

function scopesKey(scopes: string[], accountId?: string): string {
    return `${accountId ?? ''}|${scopes.join(' ')}`;
}

async function getSilentSession(
    scopes: string[],
    account?: import('vscode').AuthenticationSessionAccountInformation
): Promise<import('vscode').AuthenticationSession | undefined> {
    const key = scopesKey(scopes, account?.id);
    const existing = pendingSilentSessions.get(key);
    if (existing) {
        return existing;
    }
    const promise = (async () => {
        try {
            return await authentication.getSession(MICROSOFT_PROVIDER_ID, scopes, {
                createIfNone: false,
                silent: true,
                ...(account ? { account } : {})
            });
        } catch {
            return undefined;
        } finally {
            pendingSilentSessions.delete(key);
        }
    })();
    pendingSilentSessions.set(key, promise);
    return promise;
}

async function ensureInteractiveSession(
    scopes: string[],
    accountId?: string,
    accountHint?: string
): Promise<import('vscode').AuthenticationSession | undefined> {
    const cancelKey = cancellationKey(scopes, accountId, accountHint);
    const trySilent = async () => {
        const accs = await authentication.getAccounts(MICROSOFT_PROVIDER_ID);
        const acc = findStoredAccount(accs, accountId, accountHint);
        return getSilentSession(scopes, acc);
    };

    const runPrompt = async (): Promise<import('vscode').AuthenticationSession | undefined> => {
        try {
            const accounts = await authentication.getAccounts(MICROSOFT_PROVIDER_ID);
            const targetAccount = findStoredAccount(accounts, accountId, accountHint);
            const hintLabel = accountHint || targetAccount?.label;
            const detail = hintLabel ? `This agent was set up with ${hintLabel}. Sign in with that account to continue.` : 'Sign in to continue.';

            let session: import('vscode').AuthenticationSession | undefined;
            if (targetAccount) {
                session = await authentication.getSession(MICROSOFT_PROVIDER_ID, scopes, {
                    createIfNone: true,
                    account: targetAccount
                });
                if (!session) {
                    session = await authentication.getSession(MICROSOFT_PROVIDER_ID, scopes, {
                        forceNewSession: { detail },
                        account: targetAccount
                    });
                }
            } else {
                session = await authentication.getSession(MICROSOFT_PROVIDER_ID, scopes, {
                    forceNewSession: { detail },
                    clearSessionPreference: true
                });
            }

            signInCancelled.delete(cancelKey);
            return session;
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            if (/cancel(l)?ed/i.test(message)) {
                signInCancelled.add(cancelKey);
            } else {
                logger.logError(TelemetryEventsKeys.SignInError, `Interactive sign-in failed: ${message}`);
            }
            return undefined;
        }
    };

    if (signInCancelled.has(cancelKey)) {
        return undefined;
    }

    if (pendingInteractiveAuth) {
        await pendingInteractiveAuth;
        if (signInCancelled.has(cancelKey)) {
            return undefined;
        }
        const silentSession = await trySilent();
        if (silentSession) {
            return silentSession;
        }
        while (pendingInteractiveAuth) {
            await pendingInteractiveAuth;
        }
    }

    let promptedSession: import('vscode').AuthenticationSession | undefined;
    pendingInteractiveAuth = (async () => {
        try {
            promptedSession = await runPrompt();
            return !!promptedSession;
        } finally {
            pendingInteractiveAuth = null;
        }
    })();

    await pendingInteractiveAuth;

    return promptedSession ?? await trySilent();
}

export async function FetchAccessToken(
    resource: Uri,
    requestUri: Uri,
    accountId: string | null,
    cancellationToken: AbortSignal | null,
    autopickAccount: boolean = true,
    accountHint?: string
): Promise<AccessTokenResponse> {
    const accounts = await authentication.getAccounts(MICROSOFT_PROVIDER_ID);
    if (accountId) {
        const account = findStoredAccount(accounts, accountId, accountHint);
        if (account) {
            try {
                const tokenInfo = await getAccessTokenByAccountId(resource, account.id, accountHint);
                const response = await fetch(requestUri.toString(true), {
                    method: 'GET',
                    headers: {
                        'Authorization': `Bearer ${tokenInfo.accessToken}`
                    },
                    signal: cancellationToken
                });
                if (response.ok) {
                    return { response, tokenInfo };
                }
            } catch {
                // Fall through to interactive sign-in below.
            }
        }

        const scope = Uri.from({ scheme: resource.scheme, authority: resource.authority, path: '/.default' }).toString(true);
        const interactive = await ensureInteractiveSession([VSCODE_CLIENT_ID, scope], accountId, accountHint);
        if (interactive) {
            const tokenInfo = sessionToTokenInfo(interactive);
            const response = await fetch(requestUri.toString(true), {
                method: 'GET',
                headers: { 'Authorization': `Bearer ${tokenInfo.accessToken}` },
                signal: cancellationToken
            });
            return { response, tokenInfo };
        }
        throw new Error('Sign-in required for this agent. Please sign in to continue.');
    }

    if (autopickAccount) {
        for (const account of accounts) {
            try {
                const scope = Uri.from({ scheme: resource.scheme, authority: resource.authority, path: '/.default' }).toString(true);
                const silent = await getSilentSession([VSCODE_CLIENT_ID, scope], account);
                if (!silent) {
                    continue;
                }
                const tokenInfo = sessionToTokenInfo(silent);
                const response = await fetch(requestUri.toString(true), {
                    method: 'GET',
                    headers: {
                        'Authorization': `Bearer ${tokenInfo.accessToken}`
                    },
                    signal: cancellationToken
                });
                if (response.ok) {
                    return { response, tokenInfo };
                }
            } catch {
                // Try the next account.
            }
        }
    }

    const scope = Uri.from({ scheme: resource.scheme, authority: resource.authority, path: '/.default' }).toString(true);
    const silentSession = await getSilentSession([VSCODE_CLIENT_ID, scope]);
    if (silentSession) {
        const fallbackTokenInfo = sessionToTokenInfo(silentSession);
        const fallbackResponse = await fetch(requestUri.toString(true), {
            method: 'GET',
            headers: { 'Authorization': `Bearer ${fallbackTokenInfo.accessToken}` },
            signal: cancellationToken
        });
        return { response: fallbackResponse, tokenInfo: fallbackTokenInfo };
    }

    const interactive = await ensureInteractiveSession([VSCODE_CLIENT_ID, scope]);
    if (interactive) {
        const tokenInfo = sessionToTokenInfo(interactive);
        const response = await fetch(requestUri.toString(true), {
            method: 'GET',
            headers: { 'Authorization': `Bearer ${tokenInfo.accessToken}` },
            signal: cancellationToken
        });
        return { response, tokenInfo };
    }

    throw new Error('No signed-in account available for this request. Please sign in.');
}

/**
 * Gets the account ID of VS Code's current/preferred session for this extension.
 * Uses silent getSession to get the session VS Code would use without prompting.
 */
export async function getPreferredAccountId(clusterCategory: CoreServicesClusterCategory): Promise<string | null> {
    const SCOPES = [
        VSCODE_CLIENT_ID,
        Uri.from({
            scheme: 'api',
            authority: getTokenScopeHostName(clusterCategory),
            path: '/.default'
        }).toString(true)
    ];

    try {
        const session = await authentication.getSession(MICROSOFT_PROVIDER_ID, SCOPES, {
            createIfNone: false,
            silent: true
        });
        return session?.account.id ?? null;
    } catch {
        return null;
    }
}

/**
 * Prompts user to switch accounts. Returns true if account was switched, false if cancelled.
 * Uses coalescing to prevent multiple concurrent consent dialogs.
 */
export async function switchAccount(clusterCategory: CoreServicesClusterCategory): Promise<boolean> {
    signInCancelled.clear();
    while (pendingInteractiveAuth) {
        await pendingInteractiveAuth;
    }

    const SCOPES = [
        VSCODE_CLIENT_ID,
        Uri.from({
            scheme: 'api',
            authority: getTokenScopeHostName(clusterCategory),
            path: '/.default'
        }).toString(true)
    ];

    pendingInteractiveAuth = (async () => {
        try {
            const session = await authentication.getSession(MICROSOFT_PROVIDER_ID, SCOPES, {
                clearSessionPreference: true,
                createIfNone: true
            });

            signInCancelled.clear();

            if (session) {
                preferredTreeAccount = {
                    accountId: session.account.id,
                    accountEmail: session.account.label
                };
                try {
                    const { clearWhoAmICache } = await import('./dataverseClient.js');
                    clearWhoAmICache();
                } catch {
                }
            }
            return true; // Successfully switched
        } catch (error) {
            // User cancelled or auth failed
            if (isCancellationError(error)) {
                logger.logInfo(TelemetryEventsKeys.SwitchAccountError, 'Switch account cancelled by user.');
            } else {
                const message = error instanceof Error ? error.message : String(error);
                logger.logError(TelemetryEventsKeys.SwitchAccountError, `Failed to switch account: ${message}`);
            }
            return false; // Cancelled or failed
        } finally {
            pendingInteractiveAuth = null;
        }
    })();

    return pendingInteractiveAuth;
}

/**
 * Switches to a specific account with a Yes/No confirmation modal.
 * Uses forceNewSession to persist the choice in VS Code's auth system.
 * Uses coalescing to prevent multiple concurrent consent dialogs.
 */
export async function switchToAccount(
    accountId: string,
    accountLabel: string,
    clusterCategory: CoreServicesClusterCategory
): Promise<boolean> {
    while (pendingInteractiveAuth) {
        await pendingInteractiveAuth;
    }

    const accounts = await authentication.getAccounts(MICROSOFT_PROVIDER_ID);
    const account = accounts.find(acc => acc.id === accountId);

    if (!account) {
        return false;
    }

    const SCOPES = [
        VSCODE_CLIENT_ID,
        Uri.from({
            scheme: 'api',
            authority: getTokenScopeHostName(clusterCategory),
            path: '/.default'
        }).toString(true)
    ];

    pendingInteractiveAuth = (async () => {
        try {
            const session = await authentication.getSession(MICROSOFT_PROVIDER_ID, SCOPES, {
                forceNewSession: {
                    detail: `Switch to ${accountLabel} for this operation?`
                },
                clearSessionPreference: true,
                account: account
            });
            if (session) {
                try {
                    const { clearWhoAmICache } = await import('./dataverseClient.js');
                    clearWhoAmICache();
                } catch {
                }
            }
            return !!session;
        } catch {
            return false;
        } finally {
            pendingInteractiveAuth = null;
        }
    })();

    return pendingInteractiveAuth;
}

export async function onAccountChange(onChange: () => void): Promise<Disposable> {
    const snapshotAccountIds = async (): Promise<string> => {
        const accounts = await authentication.getAccounts(MICROSOFT_PROVIDER_ID);
        return accounts.map(a => a.id).sort().join('|');
    };

    let previousKey = await snapshotAccountIds();

    // Don't call onChange() immediately - tree view handles initial state via getChildren().
    // This listener only fires on actual session changes.

    const disposable = authentication.onDidChangeSessions(async (e) => {
        if (e.provider.id !== MICROSOFT_PROVIDER_ID) {
            return;
        }

        const currentKey = await snapshotAccountIds();
        if (currentKey !== previousKey) {
            previousKey = currentKey;
            onChange();
        }
    });

    return disposable;
}

export function isSignedIn(): Promise<boolean> {
    return new Promise<boolean>((resolve) => {
        authentication.getAccounts(MICROSOFT_PROVIDER_ID)
            .then(accounts => {
                resolve(accounts.length > 0);
            });
    });
}

export function getCopilotStudioAccessTokenByAccountId(
    clusterCategory: CoreServicesClusterCategory,
    accountId: string | undefined,
    accountHint?: string
): Promise<TokenInfo> {
    const resource = Uri.from({ scheme: 'api', authority: getTokenScopeHostName(clusterCategory) });
    return getAccessTokenByAccountId(resource, accountId, accountHint);
}

export async function getAccessTokenByAccountId(resource: Uri, accountId: string | undefined, accountHint?: string): Promise<TokenInfo> {
    const scope = Uri.from({ scheme: resource.scheme, authority: resource.authority, path: '/.default' }).toString(true);
    const scopes = [VSCODE_CLIENT_ID, scope];

    if (accountId) {
        const accounts = await authentication.getAccounts(MICROSOFT_PROVIDER_ID);
        const account = findStoredAccount(accounts, accountId, accountHint);

        if (account) {
            const session = await getSilentSession(scopes, account);
            if (session) {
                return sessionToTokenInfo(session);
            }
        }

        const interactive = await ensureInteractiveSession(scopes, accountId, accountHint);
        if (interactive) {
            return sessionToTokenInfo(interactive);
        }

        throw new Error('Sign-in required for this agent. Please sign in to continue.');
    }

    return getAccessToken(resource);
}

function sessionToTokenInfo(session: import('vscode').AuthenticationSession): TokenInfo {
    const tenantId = decodeIdToken(session.accessToken)?.tid || '';
    return {
        accessToken: session.accessToken,
        accountId: session.account.id,
        accountEmail: session.account.label,
        tenantId
    };
}

// Prompts the user to sign in to Copilot Studio.
export async function signIn(clusterCategory: CoreServicesClusterCategory) {
    if (await isSignedIn()) {
        return;
    }

    const SCOPES = [
        VSCODE_CLIENT_ID,
        Uri.from({
            scheme: 'api',
            authority: getTokenScopeHostName(clusterCategory),
            path: '/.default'
        }).toString(true)
    ];

    // Prompt user to sign in
    const session = await authentication.getSession(MICROSOFT_PROVIDER_ID, SCOPES, {
        createIfNone: true,
        clearSessionPreference: true
    });

    return !!session;
}

async function getAccessToken(uri: Uri): Promise<TokenInfo> {
    // Wait for any pending interactive auth to complete first
    // This prevents concurrent consent dialogs
    if (pendingInteractiveAuth) {
        await pendingInteractiveAuth;
    }

    const resource = Uri.from({ scheme: uri.scheme, authority: uri.authority, path: '/.default' }).toString(true);
    const scopes = [VSCODE_CLIENT_ID, resource];

    const silentSession = await getSilentSession(scopes);
    if (silentSession) {
        const tenantId = decodeIdToken(silentSession.accessToken)?.tid || '';
        return {
            accessToken: silentSession.accessToken,
            accountId: silentSession.account.id,
            tenantId,
            accountEmail: silentSession.account.label
        };
    }

    const storedAccounts = await authentication.getAccounts(MICROSOFT_PROVIDER_ID);
    if (storedAccounts.length > 0) {
        const preferred = preferredTreeAccount
            ? findStoredAccount(storedAccounts, preferredTreeAccount.accountId, preferredTreeAccount.accountEmail) ?? storedAccounts[0]
            : storedAccounts[0];
        const interactive = await ensureInteractiveSession(scopes, preferred.id, preferred.label);
        if (interactive) {
            const tenantId = decodeIdToken(interactive.accessToken)?.tid || '';
            return {
                accessToken: interactive.accessToken,
                accountId: interactive.account.id,
                tenantId,
                accountEmail: interactive.account.label
            };
        }
    }

    if (!clearSession) {
        throw new Error("No signed-in account available for this request. Please sign in.");
    }

    const options: AuthenticationGetSessionOptions = {
        clearSessionPreference: true,
        createIfNone: true,
    };
    clearSession = false;

    const session = await authentication.getSession(MICROSOFT_PROVIDER_ID, scopes, options);
    if (session) {
        const tenantId = decodeIdToken(session.accessToken)?.tid || '';
        return {
            accessToken: session.accessToken,
            accountId: session.account.id,
            tenantId: tenantId,
            accountEmail: session.account.label
        };
    } else {
        throw new Error("User canceled sign in");
    }
}

function decodeIdToken(idToken: string): { [key: string]: any } | null {
    try {
        const payload = idToken.split('.')[1];
        const decoded = Buffer.from(payload, 'base64').toString('utf-8');
        return JSON.parse(decoded);
    } catch {
        return null;
    }
}

export function resetAccount(): void{
    clearSession = true;
    preferredTreeAccount = undefined;
}

function getTokenScopeHostName(clusterCategory: CoreServicesClusterCategory): string {
    switch (clusterCategory) {
        case CoreServicesClusterCategory.Exp:
        case CoreServicesClusterCategory.Dev:
        case CoreServicesClusterCategory.Test:
        case CoreServicesClusterCategory.Preprod:
            return "a522f059-bb65-47c0-8934-7db6e5286414";
        case CoreServicesClusterCategory.FirstRelease:
        case CoreServicesClusterCategory.Prod:
            return "96ff4394-9197-43aa-b393-6a41652e21f8";
        case CoreServicesClusterCategory.Gov:
            return "9315aedd-209b-43b3-b149-2abff6a95d59";
        case CoreServicesClusterCategory.High:
            return "69c6e40c-465f-4154-987d-da5cba10734e";
        case CoreServicesClusterCategory.DoD:
            return "bd4a9f18-e349-4c74-a6b7-65dd465ea9ab";
        default:
            throw new Error("Not implemented");
    }
}