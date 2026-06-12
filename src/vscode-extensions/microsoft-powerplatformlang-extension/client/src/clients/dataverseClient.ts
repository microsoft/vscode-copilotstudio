import { Uri } from "vscode";
import { FetchAccessToken, TokenInfo } from "./account";
import { AgentInfo, SolutionInfo } from "../types";
import { solutionList } from "../generated/schema";
import logger from "../services/logger";

const PowerVirtualAgentsSolutionName = "PowerVirtualAgents";
const additionalSolutions: ReadonlyArray<string> = [
  "msdyn_RelevanceSearch",
  PowerVirtualAgentsSolutionName
];

// No longer using FetchXML for solution queries
export async function getAgentAsync(
  baseEndpoint: Uri,
  agentId: string,
  cancellationToken: AbortSignal | null,
  accountId?: string,
  accountHint?: string
): Promise<{ agent: AgentInfo; accountId: string; accountEmail?: string }> {
  const uri = baseEndpoint.with({ path: `api/data/v9.2/bots(${agentId})`, query: '$select=botid,name,iconbase64&$expand=bot_botcomponentcollection($select=schemaname,botcomponentcollectionid,name)' });
  const { result, tokenInfo } = await getAsync<AgentDetails>(uri, cancellationToken, accountId, accountHint);
  return {
    agent: getAgentInfo(result),
    accountId: tokenInfo.accountId,
    accountEmail: tokenInfo.accountEmail
  };
}

export async function getSolutionVersionsAsync(
  baseEndpoint: Uri,
  cancellationToken: AbortSignal | null,
  accountId?: string,
  accountHint?: string
): Promise<SolutionInfo> {
  const solutions = solutionList.concat(additionalSolutions);
  const filterQuery = `$select=uniquename,version&$filter=${solutions.map(solution => `uniquename eq '${solution}'`).join(' or ')}`;
  const uri = baseEndpoint.with({ path: `api/data/v9.2/solutions`, query: filterQuery });
  const result = await getAsync<ListResponse<SolutionData>>(uri, cancellationToken, accountId, accountHint).then(response => response.result.value);
  const solutionVersions: Record<string, string> = {};

  // Basing the default version on the PAC CLI default solution version.
  // Based on default solution version from PAC CLI.
  let copilotStudioSolutionVersion: string = '1.0.0';
  let dvRelevanceSearch: string = '0.0.0';

  for (const solution of result) {
    if (solution.uniquename === PowerVirtualAgentsSolutionName) {
      copilotStudioSolutionVersion = solution.version;
    }
    else if (solution.uniquename === 'msdyn_RelevanceSearch') {
      dvRelevanceSearch = solution.version;
    }
    else {
      solutionVersions[solution.uniquename] = solution.version;
    }
  }

  solutionVersions['msdyn_RelevanceSearch'] = dvRelevanceSearch;

  return {
    solutionVersions,
    copilotStudioSolutionVersion
  };
}

// Cache for WhoAmI results per environment URL to avoid repeated slow calls
const whoAmICache = new Map<string, string>();
const whoAmIPending = new Map<string, Promise<string>>();
// Cache environments that returned 403 (access denied) to avoid retrying
const whoAmIFailed = new Map<string, string>(); // cacheKey -> error reason

const WHOAMI_TIMEOUT_MS = 15000; // 15 second timeout for WhoAmI calls

/** Clears the WhoAmI cache, including failed (403) entries. Call on user-initiated refresh. */
export function clearWhoAmICache(): void {
    whoAmICache.clear();
    whoAmIPending.clear();
    whoAmIFailed.clear();
}

export async function whoAmIAsync(
  baseEndpoint: Uri,
  cancellationToken: AbortSignal | null,
  accountId?: string,
  accountHint?: string
): Promise<string> {
  const cacheKey = `${accountId ?? ''}|${baseEndpoint.authority}`;

  // Return cached result immediately
  const cached = whoAmICache.get(cacheKey);
  if (cached) {
    return cached;
  }

  // Check if this environment previously returned 403 - don't retry
  const failReason = whoAmIFailed.get(cacheKey);
  if (failReason) {
    throw new Error(`WhoAmI previously failed: ${failReason}`);
  }

  // If there's already a pending request for this environment, wait for it
  const pending = whoAmIPending.get(cacheKey);
  if (pending) {
    return pending;
  }

  // Make the request and cache the promise
  const uri = baseEndpoint.with({ path: `api/data/v9.2/WhoAmI` });

  // Create timeout abort controller
  const timeoutController = new AbortController();
  const timeoutId = setTimeout(() => timeoutController.abort(), WHOAMI_TIMEOUT_MS);

  // Combine with caller's cancellation token if provided
  const combinedSignal = cancellationToken
    ? combineAbortSignals(cancellationToken, timeoutController.signal)
    : timeoutController.signal;

  const requestPromise = getAsync<WhoAmIResponse>(uri, combinedSignal, accountId, accountHint)
    .then(({ result }) => {
      clearTimeout(timeoutId);
      const userId = result.UserId;
      whoAmICache.set(cacheKey, userId);
      whoAmIPending.delete(cacheKey);
      return userId;
    })
    .catch((error) => {
      clearTimeout(timeoutId);
      whoAmIPending.delete(cacheKey);

      // Only cache 403/access denied failures - timeouts may be temporary
      const is403 = error?.message?.includes('403') || error?.message?.includes('not a member');
      if (is403) {
        const failureReason = error?.message?.includes('not a member')
          ? 'not a member of organization'
          : 'access denied (403)';
        whoAmIFailed.set(cacheKey, failureReason);
      }

      throw error;
    });

  whoAmIPending.set(cacheKey, requestPromise);
  return requestPromise;
}

/** Combines multiple abort signals into one */
function combineAbortSignals(...signals: AbortSignal[]): AbortSignal {
  const controller = new AbortController();
  for (const signal of signals) {
    if (signal.aborted) {
      controller.abort();
      break;
    }
    signal.addEventListener('abort', () => controller.abort(), { once: true });
  }
  return controller.signal;
}

/** Pre-warm the WhoAmI cache for an environment. Call this early to avoid blocking later. */
export function preWarmWhoAmI(baseEndpoint: Uri, accountId?: string, accountHint?: string): void {
  whoAmIAsync(baseEndpoint, null, accountId, accountHint).catch(() => { /* ignore errors during pre-warm */ });
}

export async function listAgentsAsync(
  baseEndpoint: Uri,
  cancellationToken: AbortSignal | null,
  accountId?: string,
  accountHint?: string
): Promise<AgentInfo[]> {
  logger.trace('Dataverse', `Listing owned agents from: ${baseEndpoint.authority}`);
  const systemUserId = await whoAmIAsync(baseEndpoint, cancellationToken, accountId, accountHint);

  const filter = `ismanaged eq false and _ownerid_value eq ${systemUserId}`;
  const query = `$select=botid,name,iconbase64&$filter=${filter}&$expand=bot_botcomponentcollection($select=schemaname,botcomponentcollectionid,name)`;

  const uri = baseEndpoint.with({
    path: `api/data/v9.2/bots`,
    query: query
  });

  const response = await getAsync<ListResponse<AgentDetails>>(uri, cancellationToken, accountId, accountHint);
  logger.trace('Dataverse', `Found ${response.result.value.length} owned agent(s)`);
  return response.result.value.map(getAgentInfo);
}

/**
 * Lists agents that are visible to the current user but owned by someone else.
 *
 * Returns every non-owned unmanaged bot the Dataverse query returns. Dataverse already
 * security-trims this query to records the caller can read, so no extra per-agent access
 * check is performed here.
 *
 * Note: a previous version gated this list behind a batched RetrievePrincipalAccess
 * "write access" check. That check called an invalid (unbound) function form that always
 * returned HTTP 404, so every non-owned agent was filtered out — environment admins saw
 * only the agents they personally owned. The gate has been removed; cloning only requires
 * read access, which the query above already enforces.
 */
export async function listSharedAgentsAsync(
  baseEndpoint: Uri,
  cancellationToken: AbortSignal | null,
  accountId?: string,
  accountHint?: string
): Promise<AgentInfo[]> {
  logger.trace('Dataverse', `Listing shared agents from: ${baseEndpoint.authority}`);
  const systemUserId = await whoAmIAsync(baseEndpoint, cancellationToken, accountId, accountHint);

  // Get all unmanaged bots the user can see, excluding ones they own
  const filter = `ismanaged eq false and _ownerid_value ne ${systemUserId}`;
  const uri = baseEndpoint.with({
    path: `api/data/v9.2/bots`,
    query: `$select=botid,name,iconbase64&$filter=${filter}&$expand=bot_botcomponentcollection($select=schemaname,botcomponentcollectionid,name)`
  });
  const response = await getAsync<ListResponse<AgentDetails>>(uri, cancellationToken, accountId, accountHint);
  return projectSharedAgents(response.result.value);
}

async function getAsync<TResult>(
  uri: Uri,
  cancellationToken: AbortSignal | null,
  accountId?: string,
  accountHint?: string
): Promise<{ result: TResult; tokenInfo: TokenInfo }> {
  const { response, tokenInfo } = await FetchAccessToken(uri, uri, accountId ?? null, cancellationToken, true, accountHint);

  if (!response.ok) {
    const errorBody = await response.json().catch(() => null);
    throw new Error(`Request failed with status ${response.status}: ${JSON.stringify(errorBody ?? {})}`);
  }

  const result = await response.json() as TResult;
  return { result, tokenInfo };
}

function getAgentInfo(agentDetails: AgentDetails): AgentInfo {
  return {
    agentId: agentDetails.botid,
    displayName: agentDetails.name,
    displayComplement: "",
    iconBase64: agentDetails.iconbase64,
    componentCollections: (agentDetails.bot_botcomponentcollection ?? []).map(componentCollection => ({
      id: componentCollection.botcomponentcollectionid,
      schemaName: componentCollection.schemaname,
      displayName: componentCollection.name
    }))
  };
}

function getSharedAgentInfo(agentDetails: AgentDetails): AgentInfo {
  let details = getAgentInfo(agentDetails);
  details.displayComplement = " (shared)";
  return details;
}

/**
 * Projects the non-owned bots returned by Dataverse into the shared-agent list.
 *
 * This is intentionally a 1:1 mapping with no filtering: every bot the (already
 * security-trimmed) Dataverse query returns is surfaced to the user. It must not drop
 * agents based on any secondary access check — doing so is what hid other users' agents
 * from environment admins.
 */
export function projectSharedAgents(bots: AgentDetails[]): AgentInfo[] {
  return bots.map(getSharedAgentInfo);
}

interface ListResponse<T> {
  value: T[];
}

interface AgentDetails {
  botid: string;
  name: string;
  iconbase64: string;
  bot_botcomponentcollection: ComponentCollectionDetails[];
}

interface ComponentCollectionDetails {
  botcomponentcollectionid: string;
  schemaname: string;
  name: string;
}

interface SolutionData {
  uniquename: string;
  version: string;
}

interface WhoAmIResponse {
  UserId: string;
  BusinessUnitId: string;
  OrganizationId: string;
}
