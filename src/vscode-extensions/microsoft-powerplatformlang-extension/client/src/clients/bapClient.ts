import { Uri } from "vscode";
import { FetchAccessToken, TokenInfo } from "./account";
import { EnvironmentInfo } from "../types";
import { CoreServicesClusterCategory, DefaultCoreServicesClusterCategory, TelemetryEventsKeys } from "../constants";
import logger from "../services/logger";

export type EnvironmentSku = 'Developer' | 'Default' | 'Sandbox' | 'Production' | 'Teams' | 'Trial';

// ============ SHARED SKU QUERY DEFINITIONS ============
// These are used by both progressive loading (QuickPick) and per-SKU loading (TreeView)

/** OData queries for each SKU category - each SKU is queried directly */
const SKU_QUERIES: Record<EnvironmentSku, string> = {
    'Developer': "$filter=properties/environmentSku eq 'Developer'&$expand=properties.permissions",
    'Default': "$filter=properties/environmentSku eq 'Default'&$expand=properties.permissions",
    'Sandbox': "$filter=properties/environmentSku eq 'Sandbox'&$expand=properties.permissions",
    'Production': "$filter=properties/environmentSku eq 'Production'&$expand=properties.permissions",
    'Teams': "$filter=properties/environmentSku eq 'Teams'&$expand=properties.permissions",
    'Trial': "$filter=properties/environmentSku eq 'Trial'&$expand=properties.permissions",
};

/** Order for progressive loading - Developer first as it's fastest and most commonly used */
const SKU_LOAD_ORDER: EnvironmentSku[] = ['Developer', 'Default', 'Sandbox', 'Production', 'Teams', 'Trial'];

/**
 * Client-side SKU filter. The OData $filter can be unreliable, so we filter again after the response.
 * This ensures each environment appears in exactly one category.
 */
function filterBySku(envs: EnvironmentDetails[], sku: EnvironmentSku): EnvironmentDetails[] {
    return envs.filter(env => env.properties?.environmentSku === sku);
}

// ============ END SHARED SKU DEFINITIONS ============

export interface ProgressiveEnvironmentCallbacks {
    onSkuLoaded: (sku: EnvironmentSku, environments: EnvironmentInfo[]) => void;
    onAllComplete: () => void;
    onError?: (sku: EnvironmentSku, error: unknown) => void;
}

/**
 * Loads environments progressively by SKU, firing sequential requests and calling back as each completes.
 * This provides faster time-to-first-result by showing Developer/Default envs (~2s) while Sandbox loads (~5s).
 * Pass an AbortSignal to cancel remaining requests when user makes a selection.
 */
export function listEnvironmentsProgressiveAsync(
    clusterCategory: CoreServicesClusterCategory | null,
    cancellationToken: AbortSignal | null,
    accountId: string | null,
    callbacks: ProgressiveEnvironmentCallbacks
): void {
    // Run sequentially to avoid queueing contention with agent calls
    (async () => {
        for (const sku of SKU_LOAD_ORDER) {
            const query = SKU_QUERIES[sku];
            
            // Check for cancellation before starting each request
            if (cancellationToken?.aborted) {
                break;
            }
            
            try {
                const response = await getAsync<EnvironmentResponse>(
                    clusterCategory,
                    'environments',
                    query,
                    cancellationToken,
                    accountId,
                    false
                );
                
                // Client-side filter (OData filter is unreliable)
                const skuFilteredEnvs = filterBySku(response.result.value, sku);
                
                const envs = skuFilteredEnvs
                    .filter(hasEditPermission)
                    .sort(sortWithinSku)
                    .map(toEnvironmentInfo)
                    .filter((env): env is EnvironmentInfo => env !== null);
                callbacks.onSkuLoaded(sku, envs);
            } catch (error) {
                // Don't report abort errors
                if (error instanceof Error && error.name === 'AbortError') {
                    break;
                }
                callbacks.onError?.(sku, error);
            }
        }
        callbacks.onAllComplete();
    })();
}

function sortWithinSku(a: EnvironmentDetails, b: EnvironmentDetails): number {
    // Within same SKU: hasAdmin first, then alphabetical
    const hasAdminA = !!a.properties?.permissions?.UpdateEnvironment;
    const hasAdminB = !!b.properties?.permissions?.UpdateEnvironment;
    if (hasAdminA !== hasAdminB) {
        return hasAdminA ? -1 : 1;
    }
    const nameA = a.properties?.displayName || '';
    const nameB = b.properties?.displayName || '';
    return nameA.localeCompare(nameB);
}

/**
 * Lists environments for a specific SKU type only.
 * Used by TreeView for lazy loading - only queries the SKU being expanded.
 */
export async function listEnvironmentsBySkuAsync(
    clusterCategory: CoreServicesClusterCategory | null,
    sku: EnvironmentSku,
    cancellationToken: AbortSignal | null
): Promise<EnvironmentInfo[]> {
    const query = SKU_QUERIES[sku];
    
    const response = await getAsync<EnvironmentResponse>(
        clusterCategory,
        'environments',
        query,
        cancellationToken,
        null,
        false
    );

    // Client-side filter (OData filter is unreliable)
    const skuFilteredEnvs = filterBySku(response.result.value, sku);

    var permissionFilteredEnvs = skuFilteredEnvs
        .filter(hasEditPermission)
        .sort(sortWithinSku)
        .map(toEnvironmentInfo)
        .filter((env): env is EnvironmentInfo => env !== null);

    if (permissionFilteredEnvs.length === 0)
    {
        const skuEnvsCount = skuFilteredEnvs.length;
        logger.logInfo(TelemetryEventsKeys.LoadEnvironmentSuccess, `0/${skuEnvsCount} ${sku} environments are editable.`);
    }
    
    return permissionFilteredEnvs;
}

export async function listEnvironmentsAsync(clusterCategory: CoreServicesClusterCategory | null, cancellationToken: AbortSignal | null, accountId: string | null): Promise<EnvironmentInfo[]> {
    const response = await getAsync<EnvironmentResponse>(
        clusterCategory,
        'environments',
        "$filter=properties/environmentSku ne 'Platform'&$expand=properties.permissions",
        cancellationToken,
        accountId,
        false
    );

    const candidateEnvs = response.result.value.filter(env => hasEditPermission(env));

    // Sort environments: Developer first, then Default, Sandbox, Production, Teams
    // Within each SKU: hasAdmin first, then alphabetical
    const skuPriority: Record<string, number> = {
        'Developer': 0,
        'Default': 1,
        'Sandbox': 2,
        'Production': 3,
        'Teams': 4
    };

    const sortedEnvs = candidateEnvs.sort((a, b) => {
        const skuA = a.properties?.environmentSku || 'Unknown';
        const skuB = b.properties?.environmentSku || 'Unknown';
        const priorityA = skuPriority[skuA] ?? 5;
        const priorityB = skuPriority[skuB] ?? 5;

        // First sort by SKU priority
        if (priorityA !== priorityB) {
            return priorityA - priorityB;
        }

        // Within same SKU: hasAdmin first
        const hasAdminA = !!a.properties?.permissions?.UpdateEnvironment;
        const hasAdminB = !!b.properties?.permissions?.UpdateEnvironment;
        if (hasAdminA !== hasAdminB) {
            return hasAdminA ? -1 : 1;
        }

        // Finally alphabetical
        const nameA = a.properties?.displayName || '';
        const nameB = b.properties?.displayName || '';
        return nameA.localeCompare(nameB);
    });

    return sortedEnvs
        .map(toEnvironmentInfo)
        .filter(envInfo => envInfo !== null) as EnvironmentInfo[];
}

function hasEditPermission(env: EnvironmentDetails): boolean {
    // Check if the user has Environment Maker or Admin permissions
    // The presence of these permission objects indicates the user has them
    const permissions = env.properties?.permissions;
    if (!permissions) {
        return false;
    }
    // Environment Admin or ability to create apps indicates edit capability
    return !!(permissions.UpdateEnvironment || permissions.CreatePowerApp);
}  

export async function getEnvironmentByIdAsync(clusterCategory: CoreServicesClusterCategory | null, environmentId: string, cancellationToken: AbortSignal| null): Promise<EnvironmentInfo | null> {
    const environmentDetails = await getAsync<EnvironmentDetails>(
        clusterCategory,
        `environments/${environmentId}`,
        '',
        cancellationToken,
        null,
        true
    );

    return toEnvironmentInfo(environmentDetails.result);
}

async function getAsync<TResult>(
    clusterCategory: CoreServicesClusterCategory | null,
    relativePath: string,
    additionalQueryString: string | null,
    cancellationToken: AbortSignal | null,
    accountId: string | null,
    autopickAccount: boolean = true
): Promise<{ result: TResult; tokenInfo: TokenInfo }> {
    let query = 'api-version=2024-05-01';
    if (additionalQueryString) {
        query += `&${additionalQueryString}`;
    }

    const uri = Uri.from({
        scheme: 'https',
        authority: getHostName(clusterCategory ?? DefaultCoreServicesClusterCategory),
        path: '/providers/Microsoft.BusinessAppPlatform/' + relativePath,
        query: query
    });

    const resource = Uri.from({
        scheme: 'https',
        authority: getTokenScopeHostName(clusterCategory ?? DefaultCoreServicesClusterCategory)
    });

    const { response, tokenInfo } = await FetchAccessToken(resource, uri, accountId, cancellationToken, autopickAccount);
    
    if (!response.ok) {
        const errorBody = await response.json().catch(() => null);
        throw new Error(`Request failed with status ${response.status}: ${JSON.stringify(errorBody ?? {})}`);
    }

    const result = await response.json() as TResult;
    return { result, tokenInfo };
}

function getHostName(clusterCategory: CoreServicesClusterCategory): string {
    switch (clusterCategory) {
        case CoreServicesClusterCategory.Exp:
        case CoreServicesClusterCategory.Dev:
        case CoreServicesClusterCategory.Test:
            return "test.api.bap.microsoft.com";
        case CoreServicesClusterCategory.Preprod:
            return "preprod.api.bap.microsoft.com";
        case CoreServicesClusterCategory.FirstRelease:
        case DefaultCoreServicesClusterCategory:
            return "api.bap.microsoft.com";
        case CoreServicesClusterCategory.Gov:
            return "gov.api.bap.microsoft.us";
        case CoreServicesClusterCategory.High:
            return "high.api.bap.microsoft.us";
        case CoreServicesClusterCategory.DoD:
            return "api.bap.appsplatform.us";
        case CoreServicesClusterCategory.Mooncake:
            return "api.bap.partner.microsoftonline.cn";
        default:
            throw new Error("Not implemented");
    }
}

function getTokenScopeHostName(clusterCategory: CoreServicesClusterCategory): string {
    switch (clusterCategory) {
        case CoreServicesClusterCategory.Exp:
        case CoreServicesClusterCategory.Dev:
        case CoreServicesClusterCategory.Test:
        case CoreServicesClusterCategory.Preprod:
        case CoreServicesClusterCategory.FirstRelease:
        case DefaultCoreServicesClusterCategory:
            return "service.powerapps.com";
        case CoreServicesClusterCategory.Gov:
            return "gov.service.powerapps.us";
        case CoreServicesClusterCategory.High:
            return "high.service.powerapps.us";
        case CoreServicesClusterCategory.DoD:
            return "service.apps.appsplatform.us";
        default:
            throw new Error("Not implemented");
    }
}

function toEnvironmentInfo(details: EnvironmentDetails): EnvironmentInfo | null {
    if (!details.properties?.linkedEnvironmentMetadata?.instanceUrl) {
        return null;
    }

    return {
        environmentId: details.name,
        displayName: details.properties.displayName,
        dataverseUrl: details.properties.linkedEnvironmentMetadata?.instanceUrl,
        agentManagementUrl: details.properties.runtimeEndpoints['microsoft.PowerVirtualAgents'],
        environmentSku: details.properties.environmentSku
    };
}

interface EnvironmentResponse {
    value: EnvironmentDetails[];
}

interface EnvironmentDetails {
    name: string;
    properties: Properties;
}

interface Properties {
    displayName: string;
    runtimeEndpoints: Record<string, string>;
    linkedEnvironmentMetadata: LinkedEnvironmentMetadata;
    permissions?: EnvironmentPermissions;
    environmentSku?: string;
    environmentType?: string;
    isDefault?: boolean;
    createdTime?: string;
    createdBy?: { type?: string; id?: string; displayName?: string };
}

interface EnvironmentPermissions {
    UpdateEnvironment?: { displayName: string };
    CreatePowerApp?: { displayName: string };
    [key: string]: { displayName: string } | undefined;
}

interface LinkedEnvironmentMetadata {
    instanceUrl: string;
    type?: string;
}