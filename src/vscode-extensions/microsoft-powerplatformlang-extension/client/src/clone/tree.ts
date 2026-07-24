import { ExtensionContext, window, TreeDataProvider, EventEmitter, TreeItem, TreeItemCollapsibleState, ThemeIcon, ThemeColor, Uri, commands } from "vscode";
import { AccountInfo, EnvironmentInfo, AgentInfo } from "../types";
import { getIcon } from "../icon";
import { isSignedIn, onAccountChange, onAuthStateChanged, switchAccount, getPreferredTreeAccount, listStoredAccounts, getAccessTokenByAccountId, getAccountHealth, clearAuthAccountState, AuthError } from "../clients/account";
import { listAgentsAsync, listSharedAgentsAsync, clearWhoAmICache } from "../clients/dataverseClient";
import { listEnvironmentsBySkuAsync, EnvironmentSku, getTokenScopeHostName, isAccountTokenUsable } from "../clients/bapClient";
import { DefaultCoreServicesClusterCategory, TelemetryEventsKeys } from "../constants";
import { addWorkspaceChangeSubscription, getActiveAgentAccount, getAllProjectAccounts } from "../sync/localWorkspaces";
import logger from '../services/logger';

// Types must be declared before the account/SKU nodes
export enum TreeItemKind  {
    SignIn = 1,
	Environment = 3,
	Agent = 4,
	Error = 5,
	SkuSection = 6,
	AccountProblem = 7,
	RetrySignIn = 8,
	Account = 10,
	AddAccount = 11,
}

const TREE_ITEM_KIND_VALUES = new Set<number>(
	Object.values(TreeItemKind).filter((value): value is number => typeof value === 'number')
);

interface CopilotStudioTreeItem {
	kind: TreeItemKind;
}

interface AccountNodeTreeItem extends CopilotStudioTreeItem {
	kind: TreeItemKind.Account;
	account: AccountInfo;
	linked: boolean;
	current: boolean;
	expanded: boolean;
}

export interface SkuSectionTreeItem extends CopilotStudioTreeItem {
	kind: TreeItemKind.SkuSection;
	sku: EnvironmentSku;
	account: AccountInfo;
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

interface AccountProblemTreeItem extends CopilotStudioTreeItem {
	kind: TreeItemKind.AccountProblem;
	account: AccountInfo;
	message: string;
}

interface RetrySignInTreeItem extends CopilotStudioTreeItem {
	kind: TreeItemKind.RetrySignIn;
	account: AccountInfo;
}

interface ErrorTreeItem extends CopilotStudioTreeItem {
	kind: TreeItemKind.Error;
	environment?: EnvironmentInfo;
	message: string;
}

/** Discriminated union of all tree item types */
export type CopilotStudioTreeItemUnion =
	| { kind: TreeItemKind.SignIn }
	| { kind: TreeItemKind.AddAccount }
	| AccountNodeTreeItem
	| AccountProblemTreeItem
	| RetrySignInTreeItem
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
		TREE_ITEM_KIND_VALUES.has((arg as CopilotStudioTreeItem).kind)
	);
}

const SKU_LIST: EnvironmentSku[] = ['Developer', 'Default', 'Sandbox', 'Production', 'Teams', 'Trial', 'SubscriptionBasedTrial'];

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

    treeView.description = undefined;

    const accountChangeDisposable = await onAccountChange(async () => {
        treeDataProvider.invalidateCache();
        void treeDataProvider.probeAccounts();
    });

    const authStateDisposable = onAuthStateChanged(() => {
        treeDataProvider.invalidateCache();
        void treeDataProvider.probeAccounts();
    });

    const refreshCommand = commands.registerCommand('microsoft-copilot-studio.refreshAgentTreeView', async () => {
        logger.logInfo(TelemetryEventsKeys.RefreshAgentsClick, undefined, { message: 'Agents refresh initiated' });
        const startTime = Date.now();
        try {
            treeDataProvider.refresh();
            void treeDataProvider.probeAccounts();
            const durationMs = Date.now() - startTime;
            logger.logInfo(TelemetryEventsKeys.RefreshAgentsSuccess, undefined, { message: `Agents refreshed`, durationMs });
        } catch (error) {
            const durationMs = Date.now() - startTime;
            logger.logError(TelemetryEventsKeys.RefreshAgentsError, undefined, { message: `Agents refresh failed`, durationMs, error });
        }
    });

    const retrySignInCommand = commands.registerCommand('microsoft-copilot-studio.treeRetrySignIn', async (account?: AccountInfo) => {
        if (account) {
            await treeDataProvider.signInAccount(account);
        } else {
            await treeDataProvider.signInSelectedAccount();
        }
    });

    const addAccountCommand = commands.registerCommand('microsoft-copilot-studio.addTreeAccount', async () => {
        await switchAccount(DefaultCoreServicesClusterCategory);
        treeDataProvider.invalidateCache();
        void treeDataProvider.probeAccounts();
    });

    let lastActiveAccountKey = activeAccountKey();
    const activeEditorDisposable = window.onDidChangeActiveTextEditor(() => {
        const key = activeAccountKey();
        if (key !== lastActiveAccountKey) {
            lastActiveAccountKey = key;
            treeDataProvider.refreshNodes();
        }
    });

    let lastProjectAccountsKey = projectAccountsKey();
    const workspaceChangeDisposable = addWorkspaceChangeSubscription(() => {
        const next = projectAccountsKey();
        if (next !== lastProjectAccountsKey) {
            lastProjectAccountsKey = next;
            treeDataProvider.invalidateCache();
        }
        void treeDataProvider.probeAccounts();
    });

    void treeDataProvider.probeAccounts();

    context.subscriptions.push(accountChangeDisposable, authStateDisposable, refreshCommand, retrySignInCommand, addAccountCommand, activeEditorDisposable, workspaceChangeDisposable);
}

function activeAccountKey(): string | undefined {
    const active = getActiveAgentAccount();
    return (active?.accountEmail || active?.accountId || '').toLowerCase() || undefined;
}

function projectAccountsKey(): string {
    return getAllProjectAccounts()
        .map(a => (a.accountEmail || a.accountId || '').toLowerCase())
        .filter(Boolean)
        .sort()
        .join('|');
}

export interface AgentTreeDeps {
    isSignedIn: () => Promise<boolean>;
    listAccounts: () => Promise<{ accountId: string; accountEmail?: string }[]>;
}

export class AgentTreeDataProvider implements TreeDataProvider<CopilotStudioTreeItem> {
    private _onDidChangeTreeData: EventEmitter<CopilotStudioTreeItem | undefined | void> = new EventEmitter<CopilotStudioTreeItem | undefined | void>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;
    
    private envsByAccountSku: Map<string, EnvironmentTreeItem[]> = new Map();
    private loadedAccountSkus: Set<string> = new Set();
    private accountUsable: Map<string, boolean> = new Map();

    private readonly checkSignedIn: () => Promise<boolean>;
    private readonly listAccounts: () => Promise<{ accountId: string; accountEmail?: string }[]>;

    constructor(deps?: Partial<AgentTreeDeps>) {
        this.checkSignedIn = deps?.isSignedIn ?? isSignedIn;
        this.listAccounts = deps?.listAccounts ?? listStoredAccounts;
    }
    
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
        this.envsByAccountSku.clear();
        this.loadedAccountSkus.clear();
        clearWhoAmICache();
        this.fireChange();
    }

    /** Clears the environment cache on sign-out or account change. */
    invalidateCache(): void {
        this.envsByAccountSku.clear();
        this.loadedAccountSkus.clear();
        this.fireChange();
    }

    refreshNodes(): void {
        this.fireChange();
    }

    private accountKey(account: AccountInfo): string {
        return (account.accountEmail || account.accountId || '').toLowerCase();
    }

    async getAccounts(): Promise<AccountInfo[]> {
        return (await this.listAccounts()).map<AccountInfo>(a => ({ accountId: a.accountId, accountEmail: a.accountEmail ?? '', tenantId: '' }));
    }

    async resolveSelectedAccount(): Promise<AccountInfo | undefined> {
        const active = getActiveAgentAccount();
        if (active) {
            return active;
        }
        const preferred = getPreferredTreeAccount();
        if (preferred) {
            return { accountId: preferred.accountId, accountEmail: preferred.accountEmail ?? '', tenantId: '' };
        }
        const stored = await this.listAccounts();
        if (stored.length > 0) {
            return { accountId: stored[0].accountId, accountEmail: stored[0].accountEmail ?? '', tenantId: '' };
        }
        return undefined;
    }

    private async isAccountUsable(account: AccountInfo): Promise<boolean> {
        const usable = await isAccountTokenUsable(account.accountId, account.accountEmail);
        this.accountUsable.set(this.accountKey(account), usable);
        return usable;
    }

    isAccountUsableCached(account: AccountInfo): boolean | undefined {
        return this.accountUsable.get(this.accountKey(account));
    }

    async probeAccounts(): Promise<void> {
        const accounts = await this.getAccounts();
        let changed = false;
        for (const account of accounts) {
            const key = this.accountKey(account);
            const before = this.accountUsable.get(key);
            const usable = await isAccountTokenUsable(account.accountId, account.accountEmail);
            if (before !== usable) {
                this.accountUsable.set(key, usable);
                changed = true;
            }
        }
        if (changed) {
            this.fireChange();
        }
    }

    async signInAccount(account: AccountInfo): Promise<void> {
        clearAuthAccountState(account.accountId, account.accountEmail);
        try {
            const resource = Uri.from({ scheme: 'https', authority: getTokenScopeHostName(DefaultCoreServicesClusterCategory) });
            await getAccessTokenByAccountId(resource, account.accountId, account.accountEmail, true);
        } catch (error) {
            logger.logError(TelemetryEventsKeys.SignInError, undefined, { message: `Tree sign-in failed for <pii>${account.accountEmail ?? account.accountId}</pii>`, error });
        }
        this.invalidateCache();
        void this.probeAccounts();
    }

    async signInSelectedAccount(): Promise<void> {
        const account = await this.resolveSelectedAccount();
        if (account) {
            await this.signInAccount(account);
        }
    }

    private async loadSkuEnvironments(sku: EnvironmentSku, account: AccountInfo): Promise<EnvironmentTreeItem[]> {
        const cacheKey = `${this.accountKey(account)}:${sku}`;
        if (this.loadedAccountSkus.has(cacheKey)) {
            const cached = this.envsByAccountSku.get(cacheKey) || [];
            logger.logTrace('AgentTree', `Using ${cached.length} cached ${sku} environment(s)`);
            return cached;
        }
        logger.logTrace('AgentTree', `<pii>${account.accountEmail ?? account.accountId}</pii> > ${sku}: Loading environments`);
        const startTime = Date.now();
        try {
            const envs = await listEnvironmentsBySkuAsync(DefaultCoreServicesClusterCategory, sku, null, account.accountId ?? null, account.accountEmail, true);
            const items = envs.map<EnvironmentTreeItem>(env => ({ kind: TreeItemKind.Environment, environment: env, sourceAccount: account }));
            this.envsByAccountSku.set(cacheKey, items);
            this.loadedAccountSkus.add(cacheKey);
            const durationMs = Date.now() - startTime;
            logger.logInfo(TelemetryEventsKeys.LoadEnvironmentSuccess, undefined, { message: `<pii>${account.accountEmail ?? account.accountId}</pii> > ${sku}: Loaded ${items.length} environment(s)`, sku, environmentCount: items.length, durationMs });
            return items;
        } catch (error) {
            if (!(error instanceof AuthError)) {
                const durationMs = Date.now() - startTime;
                logger.logError(TelemetryEventsKeys.LoadEnvironmentError, `<pii>${account.accountEmail ?? account.accountId}</pii> > ${sku}: Failed to load environments`, { sku, durationMs, error });
            }
            this.loadedAccountSkus.add(cacheKey);
            return [];
        }
    }

    getTreeItem(element: CopilotStudioTreeItem) : TreeItem {
        if (element.kind === TreeItemKind.SignIn) {
            const item = new TreeItem("Sign In", TreeItemCollapsibleState.None);
            item.iconPath = new ThemeIcon("sign-in");
            item.command = { command: "microsoft-copilot-studio.signIn", title: "Sign In" };
            return item;
        } else if (element.kind === TreeItemKind.AccountProblem) {
            const problem = element as AccountProblemTreeItem;
            const item = new TreeItem(problem.message, TreeItemCollapsibleState.None);
            item.iconPath = new ThemeIcon('warning', new ThemeColor('list.warningForeground'));
            return item;
        } else if (element.kind === TreeItemKind.RetrySignIn) {
            const retry = element as RetrySignInTreeItem;
            const item = new TreeItem('Try signing in again', TreeItemCollapsibleState.None);
            item.iconPath = new ThemeIcon('sign-in');
            item.command = { command: 'microsoft-copilot-studio.treeRetrySignIn', title: 'Try signing in again', arguments: [retry.account] };
            return item;
        } else if (element.kind === TreeItemKind.Account) {
            const node = element as AccountNodeTreeItem;
            const account = node.account;
            const label = account.accountEmail || account.accountId || '';
            const health = getAccountHealth(account.accountId, account.accountEmail);
            const unusable = health === 'terminal' || this.isAccountUsableCached(account) === false;
            const bits: string[] = [];
            if (node.current) {
                bits.push('current');
            } else if (node.linked) {
                bits.push('in this workspace');
            }
            if (unusable) {
                bits.push("can't sign in");
            } else if (health === 'signedOut') {
                bits.push('signed out');
            }
            const item = new TreeItem(node.current ? { label, highlights: [[0, label.length]] as [number, number][] } : label, node.expanded ? TreeItemCollapsibleState.Expanded : TreeItemCollapsibleState.Collapsed);
            item.description = bits.join(' \u00b7 ');
            item.iconPath = health === 'terminal'
                ? new ThemeIcon('error', new ThemeColor('list.errorForeground'))
                : (unusable || health === 'signedOut')
                    ? new ThemeIcon('warning', new ThemeColor('list.warningForeground'))
                    : new ThemeIcon('account');
            item.contextValue = 'treeAccount';
            return item;
        } else if (element.kind === TreeItemKind.AddAccount) {
            const item = new TreeItem('Add or switch account\u2026', TreeItemCollapsibleState.None);
            item.iconPath = new ThemeIcon('add');
            item.command = { command: 'microsoft-copilot-studio.addTreeAccount', title: 'Add or switch account' };
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

    getParent(): CopilotStudioTreeItem | undefined {
        return undefined;
    }

    getChildren(element?: CopilotStudioTreeItem): Thenable<CopilotStudioTreeItem[]> {
		return new Promise(async (resolve) => {
			if (element === undefined) {
				if (!(await this.checkSignedIn())) {
					resolve(SIGN_IN_ITEMS);
					return;
				}
				const accounts = await this.getAccounts();
				if (accounts.length === 0) {
					resolve(SIGN_IN_ITEMS);
					return;
				}
				const linkedKeys = new Set(getAllProjectAccounts().map(a => (a.accountEmail || a.accountId || '').toLowerCase()).filter(Boolean));
				const active = getActiveAgentAccount();
				const activeKey = (active?.accountEmail || active?.accountId || '').toLowerCase();
				const decorated = accounts.map(account => {
					const key = this.accountKey(account);
					return { account, linked: linkedKeys.has(key), current: !!activeKey && key === activeKey };
				});
				decorated.sort((a, b) => {
					if (a.linked !== b.linked) {
						return a.linked ? -1 : 1;
					}
					return (a.account.accountEmail || a.account.accountId || '').localeCompare(b.account.accountEmail || b.account.accountId || '');
				});
				const nodes: CopilotStudioTreeItem[] = decorated.map(d => ({
					kind: TreeItemKind.Account,
					account: d.account,
					linked: d.linked,
					current: d.current,
					expanded: d.current
				} as AccountNodeTreeItem));
				nodes.push({ kind: TreeItemKind.AddAccount });
				resolve(nodes);
			} else if (element.kind === TreeItemKind.Account) {
				const account = (element as AccountNodeTreeItem).account;
				if (await this.isAccountUsable(account)) {
					resolve(SKU_LIST.map(sku => ({ kind: TreeItemKind.SkuSection, sku, account } as SkuSectionTreeItem)));
				} else {
					const label = account.accountEmail || account.accountId;
					resolve([
						{ kind: TreeItemKind.AccountProblem, account, message: `Can't sign in to ${label}.` } as AccountProblemTreeItem,
						{ kind: TreeItemKind.RetrySignIn, account } as RetrySignInTreeItem
					]);
				}
			} else if (element.kind === TreeItemKind.SkuSection) {
				const skuItem = element as SkuSectionTreeItem;
				const envItems = await this.loadSkuEnvironments(skuItem.sku, skuItem.account);
				if (envItems.length === 0) {
					resolve([{ kind: TreeItemKind.Error, message: `No ${skuItem.sku} environments` } as ErrorTreeItem]);
				} else {
					resolve(envItems);
				}
			} else if (element.kind === TreeItemKind.Environment) {
				const envItem = element as EnvironmentTreeItem;
				const sku = envItem.environment.environmentSku ?? 'Unknown';
				const envName = envItem.environment.displayName;
				const startTime = Date.now();
                try {
					const storeAccount = envItem.sourceAccount ?? await this.resolveSelectedAccount();
                    const accountName = storeAccount?.accountEmail ?? storeAccount?.accountId ?? 'unknown';
					logger.logTrace('AgentTree', `<pii>${accountName}</pii> > ${sku} > ${envName}[${envItem.environment.environmentId}]: Loading agents`);
					const [ownedAgents, sharedAgents] = await Promise.all([
                        listAgentsAsync(Uri.parse(envItem.environment.dataverseUrl), null, storeAccount?.accountId, storeAccount?.accountEmail, true),
                        listSharedAgentsAsync(Uri.parse(envItem.environment.dataverseUrl), null, storeAccount?.accountId, storeAccount?.accountEmail, true)
					]);
					
					const allAgents = [...ownedAgents, ...sharedAgents];
					const agents: CopilotStudioTreeItem[] = allAgents.map((agent) => {
                        return { kind: TreeItemKind.Agent, environment: envItem.environment, agent: agent, sourceAccount: storeAccount } as AgentTreeItem;
					});
                    const durationMs = Date.now() - startTime;
                    logger.logInfo(TelemetryEventsKeys.LoadAgentsSuccess, undefined, { message: `<pii>${accountName}</pii> > ${sku} > ${envName}[${envItem.environment.environmentId}]: Loaded ${allAgents.length} agent(s)`, sku, environmentId: envItem.environment.environmentId, agentCount: allAgents.length, durationMs });
					resolve(agents);
				} catch (error: any) {
                    const durationMs = Date.now() - startTime;
					logger.logError(TelemetryEventsKeys.LoadAgentsError, `<pii>${envItem.sourceAccount?.accountEmail ?? envItem.sourceAccount?.accountId ?? 'unknown'}</pii> > ${sku} > ${envName}[${envItem.environment.environmentId}]: Failed to load agents`, { sku, environmentId: envItem.environment.environmentId, durationMs, error });
					const errorMessage = error?.message?.includes('403') || error?.message?.includes('not a member')
						? "Access denied - not a member of this organization"
						: error?.message?.includes('timeout') || error?.message?.includes('abort')
						? "Request timed out"
						: "Failed to load agents";
					resolve([{ kind: TreeItemKind.Error, message: errorMessage, environment: envItem.environment } as ErrorTreeItem]);
				}
			}
		});
	}	
}