import { ExtensionContext, window, TreeDataProvider, EventEmitter, TreeItem, TreeItemCollapsibleState, ThemeIcon, Uri, commands, TreeView } from "vscode";
import { EnvironmentInfo, AgentInfo } from "../types";
import { getIcon } from "../icon";
import { isSignedIn, onAccountChange, switchAccount } from "../clients/account";
import { listAgentsAsync, listSharedAgentsAsync, clearWhoAmICache } from "../clients/dataverseClient";
import { listEnvironmentsBySkuAsync, EnvironmentSku } from "../clients/bapClient";
import { DefaultCoreServicesClusterCategory, TelemetryEventsKeys } from "../constants";
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

interface SkuSectionTreeItem extends CopilotStudioTreeItem {
	kind: TreeItemKind.SkuSection;
	sku: EnvironmentSku;
}

interface EnvironmentTreeItem extends CopilotStudioTreeItem {
	kind: TreeItemKind.Environment;
	environment: EnvironmentInfo;
}

export interface AgentTreeItem extends CopilotStudioTreeItem {
	kind: TreeItemKind.Agent;
	environment: EnvironmentInfo;
	agent: AgentInfo;
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
        treeDataProvider.refresh();
        await resetTreeExpansion(treeView);
    });

    // Register the switch account command (same behavior as button in clone flow)
    const switchAccountCommand = commands.registerCommand('microsoft-copilot-studio.switchAccount', async () => {
        const switched = await switchAccount(DefaultCoreServicesClusterCategory);
        if (switched) {
            // Account changed - tree will refresh automatically via onAccountChange listener
        }
    });

    // Clean up on deactivate
    context.subscriptions.push(accountChangeDisposable, refreshCommand, switchAccountCommand);
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

class AgentTreeDataProvider implements TreeDataProvider<CopilotStudioTreeItem> {
    private _onDidChangeTreeData: EventEmitter<CopilotStudioTreeItem | undefined | void> = new EventEmitter<CopilotStudioTreeItem | undefined | void>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;
    
    // Cache environments by SKU - loaded lazily when user expands each section
    private envsBySku: Map<EnvironmentSku, EnvironmentInfo[]> = new Map();
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

    /** Load environments for a specific SKU - only queries that SKU (Bug3 fix) */
    private async loadSkuEnvironments(sku: EnvironmentSku): Promise<EnvironmentInfo[]> {
        if (this.loadedSkus.has(sku)) {
            return this.envsBySku.get(sku) || [];
        }
        
        try {
            // Only query this specific SKU - defers other SKU queries until expanded
            const envs = await listEnvironmentsBySkuAsync(DefaultCoreServicesClusterCategory, sku, null);
            
            this.envsBySku.set(sku, envs);
            this.loadedSkus.add(sku);
            
            return envs;
		} catch (e: any) {
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
				: `${skuItem.sku} Environments`;
			const item = new TreeItem(label, collapsedState);
			// Use appropriate icons for each SKU type
			const iconMap: Record<EnvironmentSku, string> = {
				'Developer': 'beaker',
				'Default': 'home',
				'Sandbox': 'package',
				'Production': 'globe',
				'Teams': 'organization',
				'Trial': 'clock'
			};
			item.iconPath = new ThemeIcon(iconMap[skuItem.sku]);
			item.contextValue = 'skuSection';
			return item;
		} else if (element.kind === TreeItemKind.Environment) {
			const env = element as EnvironmentTreeItem;
			return new TreeItem(env.environment.displayName, TreeItemCollapsibleState.Collapsed);
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
					const envs = await this.loadSkuEnvironments(skuItem.sku);
					if (envs.length === 0) {
						resolve([{ kind: TreeItemKind.Error, message: `No ${skuItem.sku} environments` } as ErrorTreeItem]);
					} else {
						const envItems: EnvironmentTreeItem[] = envs.map(env => ({
							kind: TreeItemKind.Environment,
							environment: env
						}));
						resolve(envItems);
					}
				} catch (e: any) {
					logger.logError(TelemetryEventsKeys.LoadEnvironmentError, `[TreeView] Failed to load ${skuItem.sku} environments: ${e?.message || e}`);
					resolve([{ kind: TreeItemKind.Error, message: "Failed to load environments" } as ErrorTreeItem]);
				}
			} else if (element.kind === TreeItemKind.Environment) {
				const envItem = element as EnvironmentTreeItem;
				try {
					// Load owned and shared agents in parallel, combine into single list
					const [ownedAgents, sharedAgents] = await Promise.all([
						listAgentsAsync(Uri.parse(envItem.environment.dataverseUrl), null),
						listSharedAgentsAsync(Uri.parse(envItem.environment.dataverseUrl), null)
					]);
					
					const allAgents = [...ownedAgents, ...sharedAgents];
					const agents: CopilotStudioTreeItem[] = allAgents.map((agent) => {
						return { kind: TreeItemKind.Agent, environment: envItem.environment, agent: agent } as AgentTreeItem;
					});
					resolve(agents);
				} catch (e: any) {
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