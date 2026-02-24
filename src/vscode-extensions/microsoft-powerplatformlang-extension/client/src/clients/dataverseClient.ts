import { Uri } from "vscode";
import { FetchAccessToken, TokenInfo } from "./account";
import { AgentInfo, SolutionInfo } from "../types";
import { solutionList } from "../generated/schema";

const PowerVirtualAgentsSolutionName = "PowerVirtualAgents";
const additionalSolutions: ReadonlyArray<string> = [
  "msdyn_RelevanceSearch",
  PowerVirtualAgentsSolutionName
];

// No longer using FetchXML for solution queries
export async function getAgentAsync(baseEndpoint: Uri, agentId: string, cancellationToken: AbortSignal | null): Promise<{ agent: AgentInfo; accountId: string; accountEmail?: string }> {
  const uri = baseEndpoint.with({ path: `api/data/v9.2/bots(${agentId})`, query: '$select=botid,name,iconbase64&$expand=bot_botcomponentcollection($select=schemaname,botcomponentcollectionid,name)' });
  const { result, tokenInfo } = await getAsync<AgentDetails>(uri, cancellationToken);
  return {
    agent: getAgentInfo(result),
    accountId: tokenInfo.accountId,
    accountEmail: tokenInfo.accountEmail
  };
}

export async function getSolutionVersionsAsync(baseEndpoint: Uri, cancellationToken: AbortSignal | null): Promise<SolutionInfo> {
  const solutions = solutionList.concat(additionalSolutions);
  const filterQuery = `$select=uniquename,version&$filter=${solutions.map(solution => `uniquename eq '${solution}'`).join(' or ')}`;
  const uri = baseEndpoint.with({ path: `api/data/v9.2/solutions`, query: filterQuery });
  const result = await getAsync<ListResponse<SolutionData>>(uri, cancellationToken).then(response => response.result.value);
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

export async function whoAmIAsync(baseEndpoint: Uri, cancellationToken: AbortSignal | null): Promise<string> {
  const cacheKey = baseEndpoint.authority;

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

  const requestPromise = getAsync<WhoAmIResponse>(uri, combinedSignal)
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
export function preWarmWhoAmI(baseEndpoint: Uri): void {
  whoAmIAsync(baseEndpoint, null).catch(() => { /* ignore errors during pre-warm */ });
}

export async function listAgentsAsync(baseEndpoint: Uri, cancellationToken: AbortSignal | null): Promise<AgentInfo[]> {
  const systemUserId = await whoAmIAsync(baseEndpoint, cancellationToken);

  const filter = `ismanaged eq false and _ownerid_value eq ${systemUserId}`;
  const query = `$select=botid,name,iconbase64&$filter=${filter}&$expand=bot_botcomponentcollection($select=schemaname,botcomponentcollectionid,name)`;

  const uri = baseEndpoint.with({
    path: `api/data/v9.2/bots`,
    query: query
  });

  const response = await getAsync<ListResponse<AgentDetails>>(uri, cancellationToken);
  return response.result.value.map(getAgentInfo);
}

/**
 * Lists agents that are shared with the current user (not owned, but user has write access).
 * Uses batched RetrievePrincipalAccess to check access rights in a single HTTP call.
 */
export async function listSharedAgentsAsync(baseEndpoint: Uri, cancellationToken: AbortSignal | null): Promise<AgentInfo[]> {
  const systemUserId = await whoAmIAsync(baseEndpoint, cancellationToken);

  // Get all unmanaged bots the user can see, excluding ones they own
  const filter = `ismanaged eq false and _ownerid_value ne ${systemUserId}`;
  const uri = baseEndpoint.with({
    path: `api/data/v9.2/bots`,
    query: `$select=botid,name,iconbase64&$filter=${filter}&$expand=bot_botcomponentcollection($select=schemaname,botcomponentcollectionid,name)`
  });
  const response = await getAsync<ListResponse<AgentDetails>>(uri, cancellationToken);
  return response.result.value.map(getSharedAgentInfo);
}

/** Maximum number of requests per batch (Microsoft limit is 1000, using 500 for safety margin) */
const BATCH_CHUNK_SIZE = 500;

/**
 * Batches multiple RetrievePrincipalAccess calls into $batch requests.
 * Returns an array of booleans indicating write access for each bot (same order as input).
 * Automatically chunks into multiple batch requests if there are more than BATCH_CHUNK_SIZE bots.
 */
async function batchCheckWriteAccessAsync(
  baseEndpoint: Uri,
  bots: AgentDetails[],
  systemUserId: string,
  cancellationToken: AbortSignal | null
): Promise<boolean[]> {
  if (bots.length === 0) {
    return [];
  }

  // Chunk bots into batches to stay under the 1000 request limit
  const results: boolean[] = new Array(bots.length).fill(false);

  for (let chunkStart = 0; chunkStart < bots.length; chunkStart += BATCH_CHUNK_SIZE) {
    const chunkEnd = Math.min(chunkStart + BATCH_CHUNK_SIZE, bots.length);
    const chunk = bots.slice(chunkStart, chunkEnd);

    try {
      const chunkResults = await executeSingleBatchAsync(
        baseEndpoint,
        chunk,
        systemUserId,
        cancellationToken
      );

      // Copy chunk results to the correct positions in the main results array
      for (let i = 0; i < chunkResults.length; i++) {
        results[chunkStart + i] = chunkResults[i];
      }
    } catch {
      // If this chunk fails, leave those results as false (no write access)
      // Other chunks can still succeed
    }
  }

  return results;
}

/**
 * Executes a single batch request for a chunk of bots.
 * Content-ID values are 0-indexed within this chunk.
 */
async function executeSingleBatchAsync(
  baseEndpoint: Uri,
  bots: AgentDetails[],
  systemUserId: string,
  cancellationToken: AbortSignal | null
): Promise<boolean[]> {
  const batchUri = baseEndpoint.with({ path: `api/data/v9.2/$batch` });
  const boundary = `batch_${Date.now()}_${Math.random().toString(36).substring(2, 8)}`;

  // Build multipart batch request body
  // Each part is a GET request for RetrievePrincipalAccess
  // Content-ID is used to correlate responses (0-indexed within this chunk)
  const parts = bots.map((bot, index) => {
    const accessPath = `/api/data/v9.2/RetrievePrincipalAccess(Target=@target,Principal=@principal)?` +
      `@target={'@odata.id':'bots(${bot.botid})'}&@principal={'@odata.id':'systemusers(${systemUserId})'}`;

    return [
      `--${boundary}`,
      `Content-Type: application/http`,
      `Content-Transfer-Encoding: binary`,
      ``,
      `GET ${accessPath} HTTP/1.1`,
      `Content-ID: ${index}`,
      `Accept: application/json`,
      ``,
    ].join('\r\n');
  });

  const batchBody = parts.join('\r\n') + `\r\n--${boundary}--\r\n`;

  const { result } = await postBatchAsync<string>(
    batchUri,
    boundary,
    batchBody,
    cancellationToken
  );

  // Parse multipart response to extract AccessRights for each bot
  return parseBatchAccessResults(result, bots.length);
}

/**
 * Parses a multipart batch response and extracts WriteAccess for each part.
 * Uses Content-ID from each part to correlate responses with the original request order.
 * Handles out-of-order responses and partial failures gracefully.
 */
export function parseBatchAccessResults(batchResponse: string, expectedCount: number): boolean[] {
  const results: boolean[] = new Array(expectedCount).fill(false);

  // Split response by boundary markers (lines starting with --batch_ or --batchresponse_)
  // Each part contains headers (including Content-ID) followed by the HTTP response
  const boundaryRegex = /--(?:batch|batchresponse)_[^\r\n]+/;
  const parts = batchResponse.split(boundaryRegex).filter(part => part.trim().length > 0);

  for (const part of parts) {
    // Skip the closing boundary marker
    if (part.trim() === '--' || part.trim().length === 0) {
      continue;
    }

    // Extract Content-ID from headers - it appears in format "Content-ID: <number>" or "Content-ID: number"
    const contentIdMatch = part.match(/Content-ID\s*:\s*<?([0-9]+)>?/i);
    if (!contentIdMatch) {
      // Part without Content-ID - might be an error response without correlation
      continue;
    }

    const contentId = parseInt(contentIdMatch[1], 10);
    if (isNaN(contentId) || contentId < 0 || contentId >= expectedCount) {
      // Invalid Content-ID - skip this part
      continue;
    }

    // Check if this part contains a successful response (HTTP 200)
    // The response format is: HTTP/1.1 200 OK followed by headers and JSON body
    const httpStatusMatch = part.match(/HTTP\/1\.1\s+(\d+)/i);
    if (!httpStatusMatch || httpStatusMatch[1] !== '200') {
      // Non-200 response (403, 404, etc.) - leave as false (no write access)
      continue;
    }

    // Extract AccessRights from the JSON response
    const accessRightsMatch = part.match(/"AccessRights"\s*:\s*"([^"]+)"/i);
    if (accessRightsMatch) {
      const accessRights = accessRightsMatch[1];
      results[contentId] = accessRights.includes('WriteAccess');
    }
  }

  return results;
}

/**
 * Makes a POST request to the $batch endpoint with multipart content.
 */
async function postBatchAsync<TResult>(
  batchUri: Uri,
  boundary: string,
  body: string,
  cancellationToken: AbortSignal | null
): Promise<{ result: TResult }> {
  const { accessToken } = await getAccessTokenForUri(batchUri);

  const response = await fetch(batchUri.toString(true), {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${accessToken}`,
      'Content-Type': `multipart/mixed; boundary=${boundary}`,
      'Accept': 'application/json',
      'OData-MaxVersion': '4.0',
      'OData-Version': '4.0',
      // Continue processing remaining requests even if one fails (e.g., 403, 404)
      // This ensures partial failures don't cause all subsequent bots to be marked as no-access
      'Prefer': 'odata.continue-on-error',
    },
    body: body,
    signal: cancellationToken
  });

  if (!response.ok) {
    throw new Error(`Batch request failed with status ${response.status}`);
  }

  // Return raw text for multipart parsing
  const result = await response.text() as TResult;
  return { result };
}

/**
 * Gets an access token for a URI without making a request.
 * Used by postBatchAsync which needs to make its own request.
 */
async function getAccessTokenForUri(uri: Uri): Promise<{ accessToken: string }> {
  const { getAccessTokenByAccountId } = await import('./account.js');
  const tokenInfo = await getAccessTokenByAccountId(uri, undefined);
  return { accessToken: tokenInfo.accessToken };
}

async function getAsync<TResult>(uri: Uri, cancellationToken: AbortSignal | null): Promise<{ result: TResult; tokenInfo: TokenInfo }> {
  const { response, tokenInfo } = await FetchAccessToken(uri, uri, null, cancellationToken);

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
