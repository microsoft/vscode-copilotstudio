# Copilot Studio Extension for Visual Studio Code

The Copilot Studio extension for Visual Studio Code is designed to enhance the development experience of Microsoft Copilot Studio agents. It provides language support, IntelliSense code completion and suggestions, and authoring capabilities for Copilot Studio agent components.

After installation, the extension prompts you to sign in to Copilot Studio. It can then show you a list of the agents associated with your environment. Clone an agent to see its editable components, including knowledge sources, actions, topics, and triggers.


## Connect to Copilot Studio for the first time

1. Select the Copilot Studio icon in the primary side bar of Visual Studio Code. The extension asks for your permission to sign in.

1. Select **Allow**, and sign in with the appropriate credentials for your Copilot Studio environment.

## Clone an agent

1. (Optional) Open the desired agent in Copilot Studio and copy its URL from your browser's address bar.

1. In the **Copilot Studio** panel of Visual Studio Code, select **Clone agent**.

1. Select your agent (marked with "from clipboard" if you already copied the URL); otherwise, select the desired environment and then select the desired agent. The extension prompts you to select a folder to hold your agent's files (similar to a local repository).

   ![Screenshot of the agent/environment picker of the Copilot Studio extension in Visual Studio code](https://raw.githubusercontent.com/microsoft/vscode-copilotStudio/main/images/select-agent-from-clipboard.png)

1. Select the desired folder.

## Edit your agent

To edit any component, open the corresponding file and make the desired changes. Since Visual Studio Code natively supports YAML files, the Copilot Studio extension supports IntelliSense code completion and can provide guided tips.

![Screenshot of an agent topic open for editing with the Copilot Studio extension in Visual Studio code](https://raw.githubusercontent.com/microsoft/vscode-copilotStudio/main/images/edit-topic.png)

## Sync your changes

The Copilot Studio extension keeps your local workspace in sync with your agent. Use the sync commands in the **Agent Changes** view to manage changes between your local files and your Copilot Studio agent.

- **Preview changes** — See what's different between your local workspace and the agent in Copilot Studio, without modifying any files.
- **Get changes** — Download the latest changes from your Copilot Studio agent into your local workspace.
- **Apply changes** — Upload your local edits to your Copilot Studio agent.

When you apply changes, they are saved directly to your agent's environment. This is a _live editing_ experience—your agent is updated immediately.

## Repository Structure

```
src/
  LanguageServers/                        # .NET language server (C#)
    CLaSP/                                # Common Language Server Protocol framework
    PowerPlatformLS/                      # Language server solution (11 projects)
  vscode-extensions/                      # VS Code extension (TypeScript)
    microsoft-powerplatformlang-extension/  # Extension entry point
    shared/                               # Shared TypeScript modules
docs/                                     # Architecture documentation
assets/                                   # Shared assets (BotSchema.json)
build/                                    # Build infrastructure
```

For architecture details, see the [docs/](docs/) folder.

## Development

See [CONTRIBUTING.md](CONTRIBUTING.md) for build prerequisites, current build
state, and contribution guidelines.

### End-to-end tests against a real environment

Drives the real extension in a real VS Code window, over the LSP client and server,
against a real environment. **It creates and deletes cloud agents, so point it at a
development environment only.**

Setup is one gitignored config file at the extension root:

```bash
cd src/vscode-extensions/microsoft-powerplatformlang-extension
cp lspe2e.config.example.json lspe2e.config.json   # use copy on Windows
```

Fill in `environmentId` for your development environment — the Power Platform admin center
shows it. The suite resolves the environment's Dataverse URL through BAP. Running any command
below without the config prints the exact path and the template.

Every agent scenario runs in two agent shapes — `classic` and `cli`. The `workflow` shape is a
standalone workflow project.

| Command | Scenarios | Runs | Time | Covers |
|---|---|---|---|---|
| `npm run test:ui` | 28 | 52 | ~20 min | everything below |
| `npm run test:ui:smoke` | 4 | 8 | ~3 min | clone, push, pull — the fastest useful signal |
| `npm run test:ui:vscode` | 20 | 40 | ~14 min | the extension on its own |
| `npm run test:ui:workflow` | 4 | 4 | ~3 min | a standalone cloud flow with no agent |
| `npm run test:ui:interop` | 4 | 8 | ~3 min | `pac` / `msagent` compatibility |
| `npm run test:ui:signin` | — | — | ~1 min | one-time: seed the cached sign-in |
| `npm run test:ui:clean [prefix]` | — | — | ~1 min | delete leftover agents and cloud flows by name prefix |

`--group` takes `smoke`, `vscode`, `workflow`, `interop`, `all`, or a comma-separated list.
`--no-clean` leaves the agents behind for inspection.

`msagent` must be installed and signed in by running msagent auth login, because deleting the agents a run
created goes through `msagent agent delete`.

Result for each run — full transcript, machine-readable report, per-failure logs and the
cloned workspaces — are left in `%TEMP%\mcs-lsp-e2e\<run>\`, and the path is printed when the
run finishes.

#### What it tests against

Every run uploads three fixtures to the environment — one per authoring shape, plus a standalone
cloud flow — and works against those. They live in
[client/src/tests/ui/fixtures](src/vscode-extensions/microsoft-powerplatformlang-extension/client/src/tests/ui/fixtures):

| Fixture | Shape | Contents |
|---|---|---|
| [`classic-agent`](src/vscode-extensions/microsoft-powerplatformlang-extension/client/src/tests/ui/fixtures/classic-agent) | `classic` — `agent.mcs.yml` | topics, actions, knowledge files, AI prompts, a workflow, a custom connector, a sub-agent and connection references |
| [`cli-agent`](src/vscode-extensions/microsoft-powerplatformlang-extension/client/src/tests/ui/fixtures/cli-agent) | `cli` — `agent.sync.yaml` | behaviors (skills with `SKILL.md` and scripts), knowledge capabilities, a workflow, connection references under `infrastructure/` |
| [`standalone-workflow`](src/vscode-extensions/microsoft-powerplatformlang-extension/client/src/tests/ui/fixtures/standalone-workflow) | `workflow` — `workflow.sync.yaml` | one cloud flow and no agent: a definition, its metadata sidecar and the connection references it binds under `infrastructure/` |
| [`new-components`](src/vscode-extensions/microsoft-powerplatformlang-extension/client/src/tests/ui/fixtures/new-components) | both agents | components added mid-run by B-07, including a **bare skill folder** with no `.mcs.yml` — generating that metadata is what the scenario tests |

## Reporting Issues

To help us resolve problems more efficiently, please use the custom issue reporting command built into this extension. It automatically includes helpful diagnostic information like your session ID.

✅ Recommended: use the custom **Help: Copilot Studio: Report Issue** command

1. Open the Command Palette (Ctrl+Shift+P or Cmd+Shift+P on macOS)
2. Type and select:
**Help: Copilot Studio: Report Issue**
<img width="964" height="165" alt="image" src="https://github.com/user-attachments/assets/f9028ed9-cdec-4fe8-882b-c9ea74326d31" />

3. A form will appear with a pre-filled Session ID, fill out the form and submit. This ensures we receive context to investigate your issue quickly.
<img width="781" height="682" alt="image" src="https://github.com/user-attachments/assets/ba1696b4-6b0d-4bec-b5e3-b5da66e92a28" />



⚠️ Avoid using the built-in **Help: Report Issue...**

## Code of Conduct

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) for details.

## License

[MIT](LICENSE)
