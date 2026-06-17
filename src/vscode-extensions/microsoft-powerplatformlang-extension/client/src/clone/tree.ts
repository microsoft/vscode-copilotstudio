import { ExtensionContext, window, TreeDataProvider, EventEmitter, TreeItem, TreeItemCollapsibleState, ThemeIcon, Uri, commands, TreeView } from "vscode";
import { AccountInfo, EnvironmentInfo, AgentInfo } from "../types";
import { getIcon } from "../icon";
import { isSignedIn, onAccountChange, switchAccount, hasStoredAccount, getPreferredTreeAccount } from "../clients/account";
import { listAgentsAsync, listSharedAgentsAsync, clearWhoAmICache } from "../clients/dataverseClient";
import { listEnvironmentsBySkuAsync, EnvironmentSku } from "../clients/bapClient";
import { DefaultCoreServicesClusterCategory, TelemetryEventsKeys } from "../constants";
import { addWorkspaceChangeSubscription, getActiveAgentAccount, getAllProjectAccounts } from "../sync/localWorkspaces";
import logger from '../services/logger';

// Types must be declared before SKU_SECTIONS
export enum TreeItemKind  {
    SignIn = 1,
	Environment = 3,
	Agent = 4,
	Error = 5,
	SkuSection = 6,
}

interface CopilotStudioTreeItem {
	kind: TreeItemKind;
}

export interface SkuSectionTreeItem extends CopilotStudioTreeItem {
	kind: TreeItemKind.SkuSection;
	sku: EnvironmentSku;
}

interface EnvironmentTreeItem extends CopilotStudioTreeItem {
	kind: TreeItemKind.Environment;
	environment: EnvironmentInfo;
	sourceAccount?: AccountInfo;
}

export interface AgentTreeItem extends CopilotStudioTreeItem {
	kind: TreeItemKind.Agent;
	environment: EnvironmentInfo;
	agent: AgentInfo;
    sourceAccount?: AccountInfo;
}

interface ErrorTreeItem extends CopilotStudioTreeItem {
	kind: TreeItemKind.Error;
	environment?: EnvironmentInfo;
	message: string;
}

/** Discriminated union of all tree item types */
export type CopilotStudioTreeItemUnion =
	| { kind: TreeItemKind.SignIn }
	| SkuSectionTreeItem
	| EnvironmentTreeItem
	| AgentTreeItem
	| ErrorTreeItem;

/**
 * Type guard for tree items passed from VS Code command API.
 * Use with discriminated union narrowing: 
 * `if (isCopilotStudioTreeItem(x) && x.kind === TreeItemKind.Agent) { ... }`
 */
export function isCopilotStudioTreeItem(arg: unknown): arg is CopilotStudioTreeItemUnion {
	return (
		typeof arg === 'object' &&
		arg !== null &&
		'kind' in arg &&
		typeof (arg as CopilotStudioTreeItem).kind === 'number' &&
		(arg as CopilotStudioTreeItem).kind >= TreeItemKind.SignIn &&
		(arg as CopilotStudioTreeItem).kind <= TreeItemKind.SkuSection
	);
}

// Static SKU section objects - reused for object identity in reveal()
const SKU_SECTIONS: SkuSectionTreeItem[] = [
    { kind: TreeItemKind.SkuSection, sku: 'Developer' },
    { kind: TreeItemKind.SkuSection, sku: 'Default' },
    { kind: TreeItemKind.SkuSection, sku: 'Sandbox' },
    { kind: TreeItemKind.SkuSection, sku: 'Production' },
    { kind: TreeItemKind.SkuSection, sku: 'Teams' },
    { kind: TreeItemKind.SkuSection, sku: 'Trial' },
    { kind: TreeItemKind.SkuSection, sku: 'SubscriptionBasedTrial' },
];

// Sign-in items (static)
const SIGN_IN_ITEMS: CopilotStudioTreeItem[] = [
    { kind: TreeItemKind.SignIn },
];

export async function configureTreeView(context: ExtensionContext) {
    const treeDataProvider = new AgentTreeDataProvider();
    const treeView = window.createTreeView('remote-agents', {
        treeDataProvider,
        showCollapseAll: true,
    });

	treeView.description = "Remote";

    // LAZY LOADING: Don't fetch environments on sign-in.
    // Just clear the cache and fire change event - getChildren will load on-demand
    // when user actually expands the TreeView. This avoids competing with QuickPick.

	// Listen for account/session changes and invalidate the cache
    const accountChangeDisposable = await onAccountChange(async () => {
        treeDataProvider.invalidateCache();
        // Collapse all, then re-expand Developer
        await resetTreeExpansion(treeView);
    });

    // Register the refresh command (full tree)
    const refreshCommand = commands.registerCommand('microsoft-copilot-studio.refreshAgentTreeView', async () => {
        logger.info('AgentTree', 'Refresh agents requested');
        treeDataProvider.refresh();
        await resetTreeExpansion(treeView);
    });

    // Register the switch account command (same behavior as button in clone flow)
    const switchAccountCommand = commands.registerCommand('microsoft-copilot-studio.switchAccount', async () => {
        logger.info('AgentTree', 'Switch account requested');
        const switched = await switchAccount(DefaultCoreServicesClusterCategory);
        if (switched) {
            logger.info('AgentTree', 'Account switched successfully, reloading tree');
            treeDataProvider.invalidateCache();
            await resetTreeExpansion(treeView);
        } else {
            logger.debug('AgentTree', 'Switch account cancelled or failed');
        }
    });

    let lastActiveAccountKey: string | undefined =
        (getActiveAgentAccount()?.accountEmail || getActiveAgentAccount()?.accountId || '').toLowerCase() || undefined;
    const activeEditorDisposable = window.onDidChangeActiveTextEditor(() => {
        const active = getActiveAgentAccount();
        const key = (active?.accountEmail || active?.accountId || '').toLowerCase() || undefined;
        if (key !== lastActiveAccountKey) {
            lastActiveAccountKey = key;
            treeDataProvider.invalidateCache();
        }
    });

    let lastProjectAccountsKey = projectAccountsKey();
    const workspaceChangeDisposable = addWorkspaceChangeSubscription(() => {
        const next = projectAccountsKey();
        if (next !== lastProjectAccountsKey) {
            lastProjectAccountsKey = next;
            treeDataProvider.invalidateCache();
        }
    });

    // Clean up on deactivate
    context.subscriptions.push(accountChangeDisposable, refreshCommand, switchAccountCommand, activeEditorDisposable, workspaceChangeDisposable);
}

function projectAccountsKey(): string {
    return getAllProjectAccounts()
        .map(a => (a.accountEmail || a.accountId || '').toLowerCase())
        .filter(Boolean)
        .sort()
        .join('|');
}

/** Resets tree expansion: collapse all, then expand Developer */
async function resetTreeExpansion(treeView: TreeView<CopilotStudioTreeItem>) {
    try {
        await commands.executeCommand('workbench.actions.treeView.remote-agents.collapseAll');
        // Re-expand Developer using the static object for identity matching
        const devSection = SKU_SECTIONS[0]; // Developer is first
        await treeView.reveal(devSection, { expand: true, focus: false, select: false });
    } catch {
        // Ignore errors during tree expansion reset
    }
}

export class AgentTreeDataProvider implements TreeDataProvider<CopilotStudioTreeItem> {
    private _onDidChangeTreeData: EventEmitter<CopilotStudioTreeItem | undefined | void> = new EventEmitter<CopilotStudioTreeItem | undefined | void>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;
    
    // Cache environments by SKU - loaded lazily when user expands each section.
    private envsBySku: Map<EnvironmentSku, EnvironmentTreeItem[]> = new Map();
    private loadedSkus: Set<EnvironmentSku> = new Set();
    
    // Populate-once guard: pending fire is debounced to ensure single tree build
    private pendingFire: ReturnType<typeof setTimeout> | null = null;

    /** Fires change event with debounce - ensures populate-once semantics */
    private fireChange(): void {
        // Cancel any pending fire - only the last one wins
        if (this.pendingFire !== null) {
            clearTimeout(this.pendingFire);
        }
        // Defer fire to next tick - coalesces rapid calls into one
        this.pendingFire = setTimeout(() => {
            this.pendingFire = null;
            this._onDidChangeTreeData.fire();
        }, 0);
    }

    /** Called when user clicks refresh button */
    async refresh(): Promise<void> {
        // Clear cache and reload
        this.envsBySku.clear();
        this.loadedSkus.clear();
        clearWhoAmICache(); // Allow retry of previously failed (403) environments
        this.fireChange();
    }

    /** Clears the environment cache on sign-out or account change. */
    invalidateCache(): void {
        this.envsBySku.clear();
        this.loadedSkus.clear();
        this.fireChange();
    }

    /** Load environments for a specific SKU */
    private async loadSkuEnvironments(sku: EnvironmentSku): Promise<EnvironmentTreeItem[]> {
        if (this.loadedSkus.has(sku)) {
            return this.envsBySku.get(sku) || [];
        }

        logger.info('AgentTree', `Loading ${sku} environments`);
        const preferred = getPreferredTreeAccount();
        const projectAccounts = getAllProjectAccounts();

        let candidateAccounts: (AccountInfo | undefined)[];
        if (preferred) {
            candidateAccounts = [{
                accountId: preferred.accountId,
                accountEmail: preferred.accountEmail ?? '',
                tenantId: ''
            } as AccountInfo];
        } else {
            candidateAccounts =
                projectAccounts.length > 0 ? projectAccounts : [getActiveAgentAccount()];
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
        const accountsToQuery = signInChecks.filter((a): a is AccountInfo | undefined => a !== null);

        if (accountsToQuery.length === 0) {
            this.envsBySku.set(sku, []);
            this.loadedSkus.add(sku);
            return [];
        }

        try {
            const perAccountResults = await Promise.all(
                accountsToQuery.map(async (acct) => {
                    try {
                        const envs = await listEnvironmentsBySkuAsync(
                            DefaultCoreServicesClusterCategory,
                            sku,
                            null,
                            acct?.accountId ?? null,
                            acct?.accountEmail
                        );
                        return envs.map<EnvironmentTreeItem>(env => ({
                            kind: TreeItemKind.Environment,
                            environment: env,
                            sourceAccount: acct
                        }));
                    } catch (e: any) {
                        logger.error('AgentTree', `Failed to load ${sku} environments for account ${acct?.accountId ?? 'default'}: ${e?.message || e}`);
                        logger.logError(TelemetryEventsKeys.LoadEnvironmentError, `[TreeView] Failed to load ${sku} environments for ${acct?.accountId ?? 'default'}: ${e?.message || e}`);
                        return [] as EnvironmentTreeItem[];
                    }
                })
            );

            const seen = new Set<string>();
            const merged: EnvironmentTreeItem[] = [];
            for (const list of perAccountResults) {
                for (const item of list) {
                    const key = item.environment.environmentId;
                    if (seen.has(key)) {
                        continue;
                    }
                    seen.add(key);
                    merged.push(item);
                }
            }

            this.envsBySku.set(sku, merged);
            this.loadedSkus.add(sku);
            logger.info('AgentTree', `Loaded ${merged.length} ${sku} environment(s)`);
            return merged;
        } catch (e: any) {
            logger.error('AgentTree', `Failed to load ${sku} environments: ${e?.message || e}`);
            logger.logError(TelemetryEventsKeys.LoadEnvironmentError, `[TreeView] Failed to load ${sku} environments: ${e?.message || e}`);
            this.loadedSkus.add(sku); // Mark as loaded to avoid retry loops
            return [];
        }
    }

    // Keep old fetchData for compatibility but it's not used anymore
    async fetchData(): Promise<void> {
        await this.loadSkuEnvironments('Developer');
    }

    getTreeItem(element: CopilotStudioTreeItem) : TreeItem {
        if (element.kind === TreeItemKind.SignIn) {
            const item = new TreeItem("Sign In", TreeItemCollapsibleState.None);
            item.iconPath = new ThemeIcon("sign-in");
            item.command = { command: "microsoft-copilot-studio.signIn", title: "Sign In" };
            return item;
        } else if (element.kind === TreeItemKind.SkuSection) {
			const skuItem = element as SkuSectionTreeItem;
			// Developer expanded by default, others collapsed
			const collapsedState = skuItem.sku === 'Developer' 
				? TreeItemCollapsibleState.Expanded 
				: TreeItemCollapsibleState.Collapsed;
			// Default is singular (there's only ever 1), others are plural
			const label = skuItem.sku === 'Default' 
				? 'Default Environment'
				: skuItem.sku === 'SubscriptionBasedTrial'
				? 'Trial (Subscription-Based) Environments'
				: `${skuItem.sku} Environments`;
			const item = new TreeItem(label, collapsedState);
			// Use appropriate icons for each SKU type
			const iconMap: Record<EnvironmentSku, string> = {
				'Developer': 'beaker',
				'Default': 'home',
				'Sandbox': 'package',
				'Production': 'globe',
				'Teams': 'organization',
				'Trial': 'clock',
				'SubscriptionBasedTrial': 'clock'
			};
			item.iconPath = new ThemeIcon(iconMap[skuItem.sku]);
			item.contextValue = 'skuSection';
			return item;
		} else if (element.kind === TreeItemKind.Environment) {
			const env = element as EnvironmentTreeItem;
			const item = new TreeItem(env.environment.displayName, TreeItemCollapsibleState.Collapsed);
			return item;
		} else if (element.kind === TreeItemKind.Agent) {
			const agent = element as AgentTreeItem;
			const item = new TreeItem(agent.agent.displayName, TreeItemCollapsibleState.None);
			item.iconPath = getIcon(agent.agent);
			item.contextValue = 'agentItem';
			return item;
		} else if (element.kind === TreeItemKind.Error) {
			const errorItem = element as ErrorTreeItem;
			const item = new TreeItem(errorItem.message, TreeItemCollapsibleState.None);
			return item;
		} else {throw new Error("Unknown tree item kind: " + element.kind);}
	}

    getParent(element: CopilotStudioTreeItem): CopilotStudioTreeItem | undefined {
        // SKU sections and sign-in items are root level - no parent
        if (element.kind === TreeItemKind.SkuSection || 
            element.kind === TreeItemKind.SignIn) {
            return undefined;
        }
        // Environment items have SKU section as parent
        if (element.kind === TreeItemKind.Environment) {
            const envItem = element as EnvironmentTreeItem;
            const sku = envItem.environment.environmentSku;
            return SKU_SECTIONS.find(s => s.sku === sku);
        }
        // Agents have Environment as parent - not supported for now
        return undefined;
    }

    getChildren(element?: CopilotStudioTreeItem): Thenable<CopilotStudioTreeItem[]> {
		return new Promise(async (resolve) => {
			if (element === undefined) {
				// Root level: show SKU sections or sign-in options
				if (await isSignedIn()) {
					// Use static SKU_SECTIONS for object identity with reveal()
					resolve(SKU_SECTIONS);
				} else {
					// Use static SIGN_IN_ITEMS for object identity
					resolve(SIGN_IN_ITEMS);
				}
			} else if (element.kind === TreeItemKind.SkuSection) {
				// SKU section expanded: load environments for this SKU
				const skuItem = element as SkuSectionTreeItem;
				try {
					const envItems = await this.loadSkuEnvironments(skuItem.sku);
					if (envItems.length === 0) {
						resolve([{ kind: TreeItemKind.Error, message: `No ${skuItem.sku} environments` } as ErrorTreeItem]);
					} else {
						resolve(envItems);
					}
				} catch (e: any) {
					logger.error('AgentTree', `Failed to load ${skuItem.sku} environments: ${e?.message || e}`);
					logger.logError(TelemetryEventsKeys.LoadEnvironmentError, `[TreeView] Failed to load ${skuItem.sku} environments: ${e?.message || e}`);
					resolve([{ kind: TreeItemKind.Error, message: "Failed to load environments" } as ErrorTreeItem]);
				}
			} else if (element.kind === TreeItemKind.Environment) {
				const envItem = element as EnvironmentTreeItem;
                try {
                    // Load owned and shared agents in parallel, combine into single list
					const storeAccount = envItem.sourceAccount ?? getActiveAgentAccount();
					logger.info('AgentTree', `Loading agents for environment: ${envItem.environment.displayName}`);
					const [ownedAgents, sharedAgents] = await Promise.all([
                        listAgentsAsync(Uri.parse(envItem.environment.dataverseUrl), null, storeAccount?.accountId, storeAccount?.accountEmail),
                        listSharedAgentsAsync(Uri.parse(envItem.environment.dataverseUrl), null, storeAccount?.accountId, storeAccount?.accountEmail)
					]);
					
					const allAgents = [...ownedAgents, ...sharedAgents];
					logger.info('AgentTree', `Loaded ${allAgents.length} agent(s) for environment: ${envItem.environment.displayName}`);
					const agents: CopilotStudioTreeItem[] = allAgents.map((agent) => {
                        return { kind: TreeItemKind.Agent, environment: envItem.environment, agent: agent, sourceAccount: storeAccount } as AgentTreeItem;
					});
					resolve(agents);
				} catch (e: any) {
					logger.error('AgentTree', `Failed to load agents for environment: ${envItem.environment.displayName}: ${e?.message || e}`);
					logger.logError(TelemetryEventsKeys.LoadEnvironmentError, `[TreeView] Failed to load agents for ${envItem.environment.displayName}: ${e?.message || e}`);
					const errorMessage = e?.message?.includes('403') || e?.message?.includes('not a member')
						? "Access denied - not a member of this organization"
						: e?.message?.includes('timeout') || e?.message?.includes('abort')
						? "Request timed out"
						: "Failed to load agents";
					resolve([{ kind: TreeItemKind.Error, message: errorMessage, environment: envItem.environment } as ErrorTreeItem]);
				}
			}
		});
	}	
}