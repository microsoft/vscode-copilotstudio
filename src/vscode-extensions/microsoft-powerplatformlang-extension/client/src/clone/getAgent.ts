/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { window, ExtensionContext, Uri, QuickPickItem, QuickPickItemKind, ThemeIcon, commands, ProgressLocation, workspace } from 'vscode';
import { AgentInfo, CloneAgentRequest, ClonedAssets, EnvironmentInfo, IdentifyAgentResponse, CloneAgentResponse } from '../types';
import { getIcon } from '../icon';
import { tryGetAgentIdentifier } from './agentIdentifier';
import { getEnvironmentByIdAsync, listEnvironmentsBySkuAsync, EnvironmentSku } from '../clients/bapClient';
import { getAgentAsync, listAgentsAsync, listSharedAgentsAsync, preWarmWhoAmI } from '../clients/dataverseClient';
import { switchAccount, switchToAccount, isSignedIn, getPreferredAccountId, getPreferredTreeAccount, hasStoredAccount, listStoredAccounts, getAccessTokenByAccountId } from '../clients/account';
import { DefaultCoreServicesClusterCategory, LspMethods, TelemetryEventsKeys } from '../constants';
import { lspClient, buildLspRequestPayload } from '../services/lspClient';
import logger from '../services/logger';
import { writePostOpenInstruction } from '../startup/postOpen';
import { getActiveAgentAccount, getAllProjectAccounts } from '../sync/localWorkspaces';

type EnvironmentPickItem = QuickPickItem & { environment: EnvironmentInfo; sourceAccount?: { accountId?: string; accountEmail?: string } };

/**
 * A multi-step input using window.createQuickPick() and window.createInputBox().
 */
export async function getAgentInfo(agentUrl: string | undefined, context: ExtensionContext): Promise<IdentifyAgentResponse | undefined> {
  return new Promise(async (resolve, reject) => {
    const parseResult = agentUrl ? tryGetAgentIdentifier(agentUrl) : null;
    const clusterCategory = parseResult?.clusterCategory ?? DefaultCoreServicesClusterCategory;
    const quickPick = window.createQuickPick();
    quickPick.title = 'Select agent or environment';
    quickPick.busy = true;
    quickPick.buttons = [
      { iconPath: new ThemeIcon("sign-in"), tooltip: "Switch account" }
    ];

    // Guard to prevent multiple resolutions
    let isResolved = false;
    let isHandlingButtonClick = false; // Track if we're in button handler flow
    let isPickingAgent = false; // Track if we're in the agent picker flow

    const safeResolve = (value: IdentifyAgentResponse | undefined) => {
      if (!isResolved) {
        isResolved = true;
        resolve(value);
      }
    };

    quickPick.onDidTriggerButton(async (button) => {
      if (isResolved) {
        return; // Already handled
      }
      if (button === quickPick.buttons[0]) {
        isHandlingButtonClick = true; // Prevent onDidHide from resolving
        quickPick.dispose();
        // Wait for account switch to complete before reopening the picker
        const switched = await switchAccount(clusterCategory);

        // Check if we're still signed in (cancel during switch may sign us out)
        const stillSignedIn = await isSignedIn();

        if (switched || stillSignedIn) {
          // Either switched successfully OR still signed in with original account
          // Reopen the picker to let user continue
          getAgentInfo(agentUrl, context)
            .then(result => { safeResolve(result); })
            .catch(error => { reject(error); });
        } else {
          // User cancelled AND is now signed out - resolve undefined
          safeResolve(undefined);
        }
      }
    });
    quickPick.onDidChangeSelection(async (selection) => {
      if (isResolved) {
        return; // Already handled
      }
      const first = selection[0] as EnvironmentPickItem & { agentData: IdentifyAgentResponse } & { requiresAccountSwitch?: boolean; targetAccountLabel?: string };
      if (first.agentData) {
        // Check if this clipboard agent requires an account switch
        if (first.requiresAccountSwitch && first.agentData.accountId) {
          isPickingAgent = true; // Prevent onDidHide from resolving during switch
          quickPick.dispose();
          const accountLabel = first.targetAccountLabel || first.agentData.accountId;
          const switched = await switchToAccount(first.agentData.accountId, accountLabel, clusterCategory);
          if (switched) {
            try {
              const dataverseUrl = first.agentData.environmentInfo?.dataverseUrl;
              if (dataverseUrl) {
                await getAccessTokenByAccountId(
                  Uri.parse(dataverseUrl),
                  first.agentData.accountId,
                  first.agentData.accountEmail
                );
              }
            } catch (error) {
              getAgentInfo(agentUrl, context)
                .then(result => { safeResolve(result); })
                .catch(err => { reject(err); });
              return;
            }
            safeResolve(first.agentData);
          } else {
            // User cancelled switch - reopen picker
            getAgentInfo(agentUrl, context)
              .then(result => { safeResolve(result); })
              .catch(error => { reject(error); });
          }
        } else {
          safeResolve(first.agentData);
        }
      } else if (first.environment) {
        isPickingAgent = true; // Mark that we're entering agent picker
        quickPick.dispose();
        const agentInfo = await pickAgent(first.environment, first.sourceAccount);
        if (agentInfo) {
          const result: IdentifyAgentResponse = {
            agentInfo,
            environmentInfo: first.environment,
            accountId: first.sourceAccount?.accountId,
            accountEmail: first.sourceAccount?.accountEmail,
            agentIdentifier: {
              environmentId: first.environment.environmentId,
              agentId: agentInfo.agentId,
              clusterCategory: clusterCategory
            }
          };
          safeResolve(result);
        } else {
          safeResolve(undefined);
        }
      } else {
        safeResolve(undefined);
      }
    });

    // Handle dismiss (Escape key, click outside, etc.)
    quickPick.onDidHide(() => {
      // Don't resolve if we're handling button click (switch account flow) or picking agent
      if (isHandlingButtonClick || isPickingAgent) {
        return;
      }
      safeResolve(undefined);
    });

    quickPick.show();
    let busyCount = 2;

    // Check if signed in first - show message if not
    const signedIn = await isSignedIn();
    if (!signedIn) {
      quickPick.busy = false;
      quickPick.items = [{
        label: "$(sign-in) Not signed in",
        description: "Click the sign-in button above to continue",
        alwaysShow: true
      }];
      return; // Wait for user to click sign-in button
    }

    // Get the preferred/default account to detect cross-account clipboard agents
    const preferredAccountId = await getPreferredAccountId(clusterCategory);

    try {
      if (agentUrl) {
        const parseResult = tryGetAgentIdentifier(agentUrl);
        if (parseResult?.agentId) {
          const storedAccounts = await listStoredAccounts();
          const probeOrder: ({ accountId?: string; accountEmail?: string } | undefined)[] = [];
          const seenIds = new Set<string>();
          if (preferredAccountId) {
            const preferred = storedAccounts.find(a => a.accountId === preferredAccountId);
            if (preferred) {
              probeOrder.push(preferred);
              seenIds.add(preferred.accountId);
            } else {
              probeOrder.push({ accountId: preferredAccountId });
              seenIds.add(preferredAccountId);
            }
          }
          for (const a of storedAccounts) {
            if (!seenIds.has(a.accountId)) {
              probeOrder.push(a);
              seenIds.add(a.accountId);
            }
          }
          if (probeOrder.length === 0) {
            probeOrder.push(undefined);
          }

          let environment: EnvironmentInfo | null = null;
          let agentResult: { agent: AgentInfo; accountId: string; accountEmail?: string } | undefined;
          for (const candidate of probeOrder) {
            try {
              const env = await getEnvironmentByIdAsync(
                parseResult.clusterCategory,
                parseResult.environmentId,
                null,
                candidate?.accountId ?? null,
                candidate?.accountEmail
              );
              if (!env) {
                continue;
              }
              const result = await getAgentAsync(
                Uri.parse(env.dataverseUrl),
                parseResult.agentId,
                null,
                candidate?.accountId,
                candidate?.accountEmail
              );
              if (result.agent) {
                environment = env;
                agentResult = result;
                break;
              }
            } catch (error) {
              continue;
            }
          }

          if (environment && agentResult?.agent) {
            const agent = agentResult.agent;
            const requiresAccountSwitch = preferredAccountId !== null && agentResult.accountId !== preferredAccountId;
            const accountLabel = agentResult.accountEmail || agentResult.accountId;

            const newItem: QuickPickItem & { agentData: IdentifyAgentResponse; requiresAccountSwitch?: boolean; targetAccountLabel?: string } = {
              label: requiresAccountSwitch ? `$(arrow-swap) ${agent.displayName}` : agent.displayName,
              description: requiresAccountSwitch ? `from clipboard (switch to ${accountLabel})` : "from clipboard",
              iconPath: getIcon(agent),
              agentData: {
                agentInfo: agent,
                environmentInfo: environment,
                agentIdentifier: {
                  environmentId: environment.environmentId,
                  agentId: agent.agentId,
                  clusterCategory: parseResult.clusterCategory
                },
                accountId: agentResult.accountId,
                accountEmail: agentResult.accountEmail,
              },
              requiresAccountSwitch,
              targetAccountLabel: accountLabel,
            };
            const separator: QuickPickItem = { kind: QuickPickItemKind.Separator, label: "" };
            quickPick.items = [newItem, separator, ...quickPick.items];
          }
        }
      }
    }
    finally {
      quickPick.busy = --busyCount > 0;
    }

    // Create AbortController to cancel environment loading when user makes a selection
    const envAbortController = new AbortController();

    // Abort environment loading when QuickPick is disposed or selection is made
    quickPick.onDidHide(() => {
      envAbortController.abort();
    });

    // Set up section headers upfront for progressive loading
    const skuSections: EnvironmentSku[] = ['Developer', 'Default', 'Sandbox', 'Production', 'Teams', 'Trial', 'SubscriptionBasedTrial'];
    const skuSeparators: Map<EnvironmentSku, QuickPickItem> = new Map();
    const skuItems: Map<EnvironmentSku, QuickPickItem[]> = new Map();

    // Initialize with headers (loading placeholders)
    for (const sku of skuSections) {
        skuSeparators.set(sku, { kind: QuickPickItemKind.Separator, label: `${sku} (loading...)` });
        skuItems.set(sku, []);
    }

    // Function to rebuild QuickPick items from current state
    const rebuildItems = () => {
        const items: QuickPickItem[] = [...quickPick.items.filter(item =>
            'agentData' in item // Keep any agent items from URL parsing
        )];

        // Add a separator after agent items if any exist
        if (items.length > 0) {
            items.push({ kind: QuickPickItemKind.Separator, label: '' });
        }

        // Add each SKU section
        for (const sku of skuSections) {
            const separator = skuSeparators.get(sku)!;
            const envItems = skuItems.get(sku)!;
            items.push(separator);
            items.push(...envItems);
        }

        quickPick.items = items;
    };

    // Show initial structure with loading placeholders
    rebuildItems();

    let preWarmedWhoAmI = false;
    const preferredTreeAccount = getPreferredTreeAccount();
    const projectAccounts = getAllProjectAccounts();
    let candidateAccounts: ({ accountId?: string; accountEmail?: string } | undefined)[];
    if (preferredTreeAccount) {
      candidateAccounts = [preferredTreeAccount];
    } else if (projectAccounts.length > 0) {
      candidateAccounts = projectAccounts;
    } else {
      const active = getActiveAgentAccount();
      if (active) {
        candidateAccounts = [active];
      } else {
        const stored = await listStoredAccounts();
        candidateAccounts = stored.length > 0 ? stored : [undefined];
      }
    }

    const signInChecks = await Promise.all(
      candidateAccounts.map(async (acct) => {
        if (!acct) {
          return (await hasStoredAccount()) ? acct : null;
        }
        const hasAccount = await hasStoredAccount(acct.accountId, acct.accountEmail);
        return hasAccount ? acct : null;
      })
    );
    const accountsToQuery = signInChecks.filter((acct): acct is { accountId?: string; accountEmail?: string } | undefined => acct !== null);

    for (const sku of skuSections) {
      if (envAbortController.signal.aborted) {
        break;
      }

      try {
        const perAccountResults = await Promise.all(
          accountsToQuery.map(async (acct) => {
            try {
              const environments = await listEnvironmentsBySkuAsync(
                clusterCategory,
                sku,
                envAbortController.signal,
                acct?.accountId ?? null,
                acct?.accountEmail
              );
              return environments.map<EnvironmentPickItem>((environment) => ({
                label: environment.displayName,
                description: environment.environmentId,
                environment,
                sourceAccount: acct
              }));
            } catch (error) {
              logger.logError(
                TelemetryEventsKeys.LoadEnvironmentError,
                `Failed to load ${sku} environments for ${acct?.accountId ?? 'default'}: ${error instanceof Error ? error.message : String(error)}`
              );
              return [] as EnvironmentPickItem[];
            }
          })
        );

        const seen = new Set<string>();
        const merged: EnvironmentPickItem[] = [];
        for (const items of perAccountResults) {
          for (const item of items) {
            const key = item.environment.environmentId;
            if (seen.has(key)) {
              continue;
            }
            seen.add(key);
            merged.push(item);
          }
        }

        if (!preWarmedWhoAmI && merged.length > 0) {
          preWarmedWhoAmI = true;
          preWarmWhoAmI(
            Uri.parse(merged[0].environment.dataverseUrl),
            merged[0].sourceAccount?.accountId,
            merged[0].sourceAccount?.accountEmail
          );
        }

        skuSeparators.set(sku, { kind: QuickPickItemKind.Separator, label: `${sku} (${merged.length})` });
        skuItems.set(sku, merged);
        rebuildItems();
      } catch (error) {
        if (error instanceof Error && error.name === 'AbortError') {
          break;
        }
        skuSeparators.set(sku, { kind: QuickPickItemKind.Separator, label: `${sku} (failed to load)` });
        rebuildItems();
      }
    }

    quickPick.busy = false;
  });
}


async function pickAgent(
  environment: EnvironmentInfo,
  sourceAccount?: { accountId?: string; accountEmail?: string }
): Promise<AgentInfo | undefined> {
  return new Promise(async (resolve) => {
    const input = window.createQuickPick();
    input.busy = true;
    input.items = [];
    input.title = 'Pick agent in environment: ' + environment.displayName;
    input.buttons = [{ iconPath: { light: Uri.file("$(list-unordered)"), dark: Uri.file("$(list-unordered)") }, tooltip: "List agents" }];
    input.placeholder = 'Pick an agent';
    input.canSelectMany = false;

    input.onDidChangeSelection(async (selection) => {
      const selectedItem = selection[0] as QuickPickItem & { agent?: AgentInfo };

      if (selectedItem?.agent) {
        resolve(selectedItem.agent);
      }
    });

    input.onDidHide(() => {
      input.dispose();
      resolve(undefined);
    });
    input.show();

    // Load owned and shared agents in parallel, combine into single list
    const [ownedAgents, sharedAgents] = await Promise.all([
      listAgentsAsync(Uri.parse(environment.dataverseUrl), null, sourceAccount?.accountId, sourceAccount?.accountEmail),
      listSharedAgentsAsync(Uri.parse(environment.dataverseUrl), null, sourceAccount?.accountId, sourceAccount?.accountEmail)
    ]);

    const allAgents = [...ownedAgents, ...sharedAgents];
    const agentItems = allAgents.map((agent) => {
      return { label: agent.displayName + agent.displayComplement, iconPath: getIcon(agent), agent: agent } as QuickPickItem & { agent: AgentInfo };
    });

    input.items = agentItems;
    input.busy = false;
  });
}

export async function cloneAgentToLocalFolder(agent: IdentifyAgentResponse | undefined, context: ExtensionContext): Promise<void> {
  if (!agent || !agent.agentIdentifier || !agent.agentInfo || !agent.environmentInfo) {
    return;
  }

  const { accountId, accountEmail, agentInfo, agentIdentifier, environmentInfo } = agent;

  // Show component picker first (if agent has component collections)
  const assets = await pickAssets(agentInfo);
  if (!assets) {
    // User cancelled the component picker
    logger.logWarning(TelemetryEventsKeys.CloneAgentCancel, "Component selection cancelled. Clone agent canceled.");
    return;
  }

  const folder = await window.showOpenDialog({
    canSelectMany: false,
    canSelectFiles: false,
    canSelectFolders: true
  });

  const rootFolder = folder?.pop()?.fsPath;
  if (!rootFolder) {
    logger.logWarning(TelemetryEventsKeys.CloneAgentCancel, "No folder selected. Clone agent canceled.");
    return;
  }

  try {
    const title = assets.componentcollectionIds.length === 0
      ? "Cloning agent " + agentInfo.displayName + " to " + rootFolder
      : "Cloning agent " + agentInfo.displayName + " with dependencies to " + rootFolder;

    await window.withProgress({ location: ProgressLocation.Notification, cancellable: true, title }, async (progress, cancellationToken) => {
      cancellationToken.onCancellationRequested(() => {
        logger.logInfo(TelemetryEventsKeys.CloneAgentCancel, undefined, {
          agentId: agentInfo.agentId,
          environmentId: environmentInfo.environmentId,
        });
      });

      const cloneRequest: CloneAgentRequest = {
        ...await buildLspRequestPayload(undefined, environmentInfo, {
          accountId,
          accountEmail,
          clusterCategory: agentIdentifier.clusterCategory
        }),
        agentInfo,
        assets,
        rootFolder
      };
      const cloneResp = await lspClient.sendRequest<CloneAgentResponse>(LspMethods.CLONE_AGENT, cloneRequest, cancellationToken);

      logger.logInfo(TelemetryEventsKeys.CloneAgentSuccess, `Agent ${agentInfo.displayName} cloned to <pii>${rootFolder}</pii>`, {
        agentId: agentInfo.agentId,
        environmentId: environmentInfo.environmentId,
      });

      // After cloning the agent, setup a PostOpen instruction to open the agent file in the current window.
      const workspaceUri = Uri.file(rootFolder);
      if (cloneResp?.agentFolderName) {
        try {
          const candidateAgentFile = Uri.joinPath(workspaceUri, cloneResp.agentFolderName, 'agent.mcs.yml');
          await workspace.fs.stat(candidateAgentFile);
          await writePostOpenInstruction(context, workspaceUri, candidateAgentFile);
        } catch {
          logger.logInfo(TelemetryEventsKeys.PostOpenInstruction, undefined, {
            agentId: agentInfo.agentId,
            environmentId: environmentInfo.environmentId,
            phase: 'skipNoAgentFile',
            detail: 'A concrete agent.mcs.yml was not recorded as a postOpen instruction'
          });
        }
      }

      // Dispose the telemetry reporter to ensure all events are sent before VS Code reloads the window.
      await logger.dispose();
      await commands.executeCommand('vscode.openFolder', workspaceUri, { forceReuseWindow: true });
    });
  } catch (error) {
    throw error;
  }
}

type QuickPickItemWithId = QuickPickItem & { id: string };

async function pickAssets(agentInfo: AgentInfo): Promise<ClonedAssets | undefined> {
  if (!agentInfo.componentCollections || agentInfo.componentCollections.length === 0) {
    return { cloneAgent: true, componentcollectionIds: [] };
  }

  return new Promise<ClonedAssets | undefined>((resolve) => {
    // Create a quick pick for the agent and its component collections
    const picker = window.createQuickPick<QuickPickItemWithId>();
    picker.title = 'Select assets to clone';
    picker.canSelectMany = true;
    picker.items = [{ label: agentInfo.displayName, id: "agent", iconPath: getIcon(agentInfo), description: "Agent" } as QuickPickItemWithId].concat(
      agentInfo.componentCollections.map((componentCollection) => {
        return {
          label: componentCollection.displayName,
          description: 'Component collection',
          id: componentCollection.id,
          iconPath: new ThemeIcon("package")
        } as QuickPickItemWithId;
      }));
    picker.selectedItems = picker.items; // Pre-select all items

    let accepted = false;
    picker.onDidAccept(() => {
      accepted = true;
      const typedSelection = picker.selectedItems as QuickPickItemWithId[];
      const isAgentSelected: boolean = typedSelection.some(item => item.id === "agent");
      const componentcollectionIds = typedSelection.filter(item => item.id !== "agent").map(item => item.id);
      picker.dispose();
      resolve({ cloneAgent: isAgentSelected, componentcollectionIds: componentcollectionIds });
    });
    picker.onDidHide(() => {
      picker.dispose();
      if (!accepted) {
        resolve(undefined); // User cancelled
      }
    });
    picker.show();
  });
}
