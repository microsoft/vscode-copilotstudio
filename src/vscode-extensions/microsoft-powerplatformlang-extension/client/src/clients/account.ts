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
const VSCODE_CLIENT_ID = "VSCODE_CLIENT_ID:51f81489-12ee-4a9e-aaae-a2591f45987d";

// Coalescing lock for interactive auth - prevents multiple concurrent consent dialogs
let pendingInteractiveAuth: Promise<boolean> | null = null;

export async function FetchAccessToken(
    resource: Uri,
    requestUri: Uri,
    accountId: string | null,
    cancellationToken: AbortSignal | null,
    autopickAccount: boolean = true
): Promise<AccessTokenResponse> {
    const accounts = await authentication.getAccounts(MICROSOFT_PROVIDER_ID);
    if (accountId)
    {    
        const account = accounts.find(acc => acc.id === accountId);
        if (account) {
            try {
                const tokenInfo = await getAccessTokenByAccountId(resource, account.id);
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
                // If fetching with this account fails, we will try other account
            }
        }
    }

    if (autopickAccount) {
        for (const account of accounts) {
            if (accountId && account.id !== accountId) {
                // Skip the account we already tried
                continue;
            }

            try {
                const tokenInfo = await getAccessTokenByAccountId(resource, account.id);
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
            }
            catch {
                // If fetching with this account fails, we will try the next account
            }        
        }
    }

    // Fallback to default session
    const fallbackTokenInfo = await getAccessToken(resource);
    
    const fallbackResponse = await fetch(requestUri.toString(true), {
        method: 'GET',
        headers: {
            'Authorization': `Bearer ${fallbackTokenInfo.accessToken}`
        },
        signal: cancellationToken
    });

    return { response: fallbackResponse, tokenInfo: fallbackTokenInfo };
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
    // If interactive auth is already in progress, wait for it
    if (pendingInteractiveAuth) {
        return pendingInteractiveAuth;
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
            await authentication.getSession(MICROSOFT_PROVIDER_ID, SCOPES, {
                clearSessionPreference: true,
                createIfNone: true
            });
            return true; // Successfully switched
        } catch (error) {
            // User cancelled or auth failed
            const message = error instanceof Error ? error.message : String(error);
            if (!message.includes('cancelled') && !message.includes('canceled')) {
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
    // If interactive auth is already in progress, wait for it
    if (pendingInteractiveAuth) {
        return pendingInteractiveAuth;
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
    let previousSession = await authentication.getSession(MICROSOFT_PROVIDER_ID, [], { createIfNone: false });

    // Don't call onChange() immediately - tree view handles initial state via getChildren().
    // This listener only fires on actual session changes.

    const disposable = authentication.onDidChangeSessions(async (e) => {
        if (e.provider.id !== MICROSOFT_PROVIDER_ID) {
            return;
        }

        const currentSession = await authentication.getSession(MICROSOFT_PROVIDER_ID, [], { createIfNone: false });

        // Call onChange if the session changed (first sign-in, logout, or switch)
        const currentSessionId = currentSession?.id ?? null;
        const previousSessionId = previousSession?.id ?? null;

        if (currentSessionId !== previousSessionId) {
            previousSession = currentSession;
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

export function getCopilotStudioAccessTokenByAccountId(clusterCategory: CoreServicesClusterCategory, accountId: string | undefined): Promise<TokenInfo> {
    const resource = Uri.from({ scheme: 'api', authority: getTokenScopeHostName(clusterCategory) });
    return getAccessTokenByAccountId(resource, accountId);
}

export async function getAccessTokenByAccountId(resource: Uri, accountId: string | undefined): Promise<TokenInfo> {
    if (accountId) {
        const accounts = await authentication.getAccounts(MICROSOFT_PROVIDER_ID);
        const scope = Uri.from({ scheme: resource.scheme, authority: resource.authority, path: '/.default' }).toString(true);
        const account = accounts.find(acc => acc.id === accountId);

        if (account) {
            const session = await authentication.getSession(
                MICROSOFT_PROVIDER_ID,
                [VSCODE_CLIENT_ID, scope],
                {
                    clearSessionPreference: false,
                    createIfNone: false,
                    account: account
                }
            );

            if (session) {
                const tenantId = decodeIdToken(session.accessToken)?.tid || '';
                return {
                    accessToken: session.accessToken,
                    accountId: session.account.id,
                    accountEmail: session.account.label,
                    tenantId
                };
            }
        }
    }
    
    // If no account is found, return token in current session.
    return getAccessToken(resource);
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
    const options: AuthenticationGetSessionOptions = { 
        clearSessionPreference: clearSession,
        createIfNone: true,
    };

    clearSession = false;

    const session = await authentication.getSession(MICROSOFT_PROVIDER_ID, [VSCODE_CLIENT_ID, resource], options);
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