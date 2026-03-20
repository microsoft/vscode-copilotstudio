import { window } from "vscode";
import { CopilotStudioWorkspace, getAllWorkspaces } from "./localWorkspaces";

export function selectWorkspace() : Promise<CopilotStudioWorkspace | undefined> {
    return new Promise((resolve) => {
        const workspaces = getAllWorkspaces();
        if (workspaces.length === 0) {
            resolve(undefined);
            return;
        } else if (workspaces.length === 1) {
            resolve(workspaces[0]);
            return;
        } else {
            const workspaceItems = workspaces.map(workspace => ({
                label: workspace.displayName,
                description: workspace.description,
                iconPath: workspace.icon,
                type: workspace.type,
                info: workspace.syncInfo,
                data: workspace,
            }));
            window.showQuickPick(workspaceItems, { placeHolder: "Select a workspace" }).then(selected => {
                if (selected) {
                    resolve(selected.data);
                } else {
                    resolve(undefined);
                }
            });
        }
    });
}