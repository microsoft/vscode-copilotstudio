# Changelog

All notable changes to the Copilot Studio extension for Visual Studio Code are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/).

## [Unreleased]

## [1.8.21] - 2026-09-18

### Added

- Surface payload sidecar files of newly added packaged (bundle) skills as **Create** changes in local changes and preview ([#406](https://github.com/microsoft/vscode-copilotstudio/pull/406))
- Handle connected agents in the CLI agent format ([#381](https://github.com/microsoft/vscode-copilotstudio/pull/381))

### Changed

- Restructure the skill folder layout for CLI agents ([#385](https://github.com/microsoft/vscode-copilotstudio/pull/385))
- Run agent sync and deploy fully in memory ([#383](https://github.com/microsoft/vscode-copilotstudio/pull/383))

### Fixed

- Stop serializer-only differences in settings and topics from appearing as local changes ([#412](https://github.com/microsoft/vscode-copilotstudio/pull/412))
- Fix settings diffing for CLI-format agents ([#384](https://github.com/microsoft/vscode-copilotstudio/pull/384))
- Allow the **Apply Change** command to run when a sync is not currently in progress ([#377](https://github.com/microsoft/vscode-copilotstudio/pull/377))

## [1.7.20] - 2026-08-17

### Added

- Add a **Discard changes** button to the Agent Changes view that restores selected local changes to their last-synced baseline ([#356](https://github.com/microsoft/vscode-copilotstudio/pull/356))
- Add global variable IntelliSense with rename, find references, diagnostics, and a create-new-variable quick fix ([#348](https://github.com/microsoft/vscode-copilotstudio/pull/348))
- Work with newly added local AI prompts without pushing them to the cloud first ([#346](https://github.com/microsoft/vscode-copilotstudio/pull/346))

### Changed

- Centralize error handling and PII scrubbing across the language server and the extension, and standardize telemetry dimensions ([#366](https://github.com/microsoft/vscode-copilotstudio/pull/366))
- Redact PII such as agent display names from sync and LSP telemetry while keeping readable VS Code messages ([#355](https://github.com/microsoft/vscode-copilotstudio/pull/355))

### Fixed

- Fix skill-folder handling for CLI agents so cloning works and new bare skill folders can be pushed from local to cloud ([#361](https://github.com/microsoft/vscode-copilotstudio/pull/361))
- Fix child-agent references when using the child display name ([#353](https://github.com/microsoft/vscode-copilotstudio/pull/353))

## [1.6.68] - 2026-07-16

### Added

- Rework multi-account management with an account/environment/agent tree view, and surface disconnected agents with tooltips explaining why (signed out, missing connection, or failed sign-in) ([#344](https://github.com/microsoft/vscode-copilotstudio/pull/344))
- Add IntelliSense warning diagnostics and a quick fix for inline comments ([#334](https://github.com/microsoft/vscode-copilotstudio/pull/334))
- Add reattach/retarget support for agents that have a component collection ([#328](https://github.com/microsoft/vscode-copilotstudio/pull/328))
- Add support for retargeting an agent to a new environment or tenant via Reattach Agent, and show agent identity in the Agent Changes view tooltip ([#312](https://github.com/microsoft/vscode-copilotstudio/pull/312))
- Add connection management (view, bind, and create connections from standard/custom connectors, plus grouped binding) with connection IntelliSense ([#304](https://github.com/microsoft/vscode-copilotstudio/pull/304))
- Add automatic connection creation for reattach/push via the connection creation page ([#286](https://github.com/microsoft/vscode-copilotstudio/pull/286))

### Changed

- Standardize logging and telemetry across the language server and the extension ([#341](https://github.com/microsoft/vscode-copilotstudio/pull/341))
- Simplify `.mcs` metadata handling so Sync/Clone writes it directly ([#339](https://github.com/microsoft/vscode-copilotstudio/pull/339))
- Project cloned child-agent folders from their friendly display name, linked to the cloud agent via a hidden `.agent.json` file ([#314](https://github.com/microsoft/vscode-copilotstudio/pull/314))
- Unify knowledge file handling in the sync engine ([#296](https://github.com/microsoft/vscode-copilotstudio/pull/296))
- Reduce log noise, refactor LSP logging, and standardize the output channel ([#288](https://github.com/microsoft/vscode-copilotstudio/pull/288))

### Fixed

- Stop knowledge files from appearing in the local diff right after cloning a CLI agent ([#335](https://github.com/microsoft/vscode-copilotstudio/pull/335))
- Allow reattaching an agent with no `.mcs` folder, and align the reattach environment list with the Agents tree ([#327](https://github.com/microsoft/vscode-copilotstudio/pull/327))
- Fix child-agent folder naming during sync and reattach so schema-name and display-name folders no longer split or misroute knowledge files ([#325](https://github.com/microsoft/vscode-copilotstudio/pull/325))
- Fix file type properties for `Workflow` components ([#323](https://github.com/microsoft/vscode-copilotstudio/pull/323))
- Exclude `agent.sync.yaml` from YAML diagnostics to stop a spurious semantic-model error ([#321](https://github.com/microsoft/vscode-copilotstudio/pull/321))
- Improve Sync performance and fix workflow error handling ([#320](https://github.com/microsoft/vscode-copilotstudio/pull/320))
- Stop workflow metadata from showing draft mode in local changes when the cloud copy is active ([#319](https://github.com/microsoft/vscode-copilotstudio/pull/319))
- Project packaged skill file attachments so cloned skill bundles land under the correct behaviors folder ([#315](https://github.com/microsoft/vscode-copilotstudio/pull/315))
- Fix AI prompt handling in shared agents ([#299](https://github.com/microsoft/vscode-copilotstudio/pull/299))
- Stop push from requiring a new connection for existing CLI agent connections ([#296](https://github.com/microsoft/vscode-copilotstudio/pull/296))
- Fix push/reattach being blocked for classic agents created from gallery (non-default) templates ([#294](https://github.com/microsoft/vscode-copilotstudio/pull/294))
- Show non-owned agents in the clone list for environment admins ([#290](https://github.com/microsoft/vscode-copilotstudio/pull/290))
- Fix reattach for child agents ([#286](https://github.com/microsoft/vscode-copilotstudio/pull/286))

## [1.5.57] - 2026-06-23

### Fixed

- Fix push/reattach being blocked for classic agents created from non-default gallery templates such as Website Q&A ([#295](https://github.com/microsoft/vscode-copilotstudio/pull/295))
- Show non-owned agents in the clone list for environment admins ([#291](https://github.com/microsoft/vscode-copilotstudio/pull/291))

## [1.5.55] - 2026-06-15

### Added

- Add authoring support for code-first (CLI) Copilot Studio agents across the extension, sync engine, and language server, alongside classic agents ([#272](https://github.com/microsoft/vscode-copilotstudio/pull/272))
- Enable Sync to pack cacheless CLI Copilot workspaces created by local scaffold flows such as `pac copilot init --authoring-mode cli-copilot` ([#276](https://github.com/microsoft/vscode-copilotstudio/pull/276))
- Add Clone/Pull/Push/Reattach support for CLI agents, plus a workflow outline and visualization ([#265](https://github.com/microsoft/vscode-copilotstudio/pull/265))
- Show `SubscriptionBasedTrial` environments in the Agents view ([#243](https://github.com/microsoft/vscode-copilotstudio/pull/243))
- Add Clone/Pull/Push/Reattach support for AI Builder prompts ([#236](https://github.com/microsoft/vscode-copilotstudio/pull/236))

### Changed

- Switch authentication to the new first-party application, resolving several outstanding sign-in issues ([#279](https://github.com/microsoft/vscode-copilotstudio/pull/279))

### Fixed

- Fix a missing `CdsBotId` ([#262](https://github.com/microsoft/vscode-copilotstudio/pull/262))
- Fix file type properties for `Workflow` components ([#261](https://github.com/microsoft/vscode-copilotstudio/pull/261))
- Fix a missing `connectionId` in connection references (`ConnectionNotSet` in Maker mode) ([#256](https://github.com/microsoft/vscode-copilotstudio/pull/256))
- Fix pulling a newly added child agent ([#254](https://github.com/microsoft/vscode-copilotstudio/pull/254))
- Stop repeated sign-in prompts and correctly handle tokens for multiple accounts ([#242](https://github.com/microsoft/vscode-copilotstudio/pull/242))
- Improve formatting of logs in the output window ([#238](https://github.com/microsoft/vscode-copilotstudio/pull/238))
- Improve the telemetry setting description wording ([#232](https://github.com/microsoft/vscode-copilotstudio/pull/232))

## [1.4.37] - 2026-05-14

### Added

- Add a loading indicator and disable sync operations while a sync is in progress ([#225](https://github.com/microsoft/vscode-copilotstudio/pull/225))
- Add Clone/Pull/Push/Reattach support for custom connector components ([#219](https://github.com/microsoft/vscode-copilotstudio/pull/219))
- Add Clone/Sync/KnowledgeFile/Workflow/Connection support for `ComponentCollection` ([#197](https://github.com/microsoft/vscode-copilotstudio/pull/197))

### Fixed

- Fix false `MissingRequiredProperty` diagnostics for `BotEntity` in `settings.mcs.yml` ([#220](https://github.com/microsoft/vscode-copilotstudio/pull/220))
- Fix workflow file variable output type ([#212](https://github.com/microsoft/vscode-copilotstudio/pull/212))
- Fix workflow output variable resolution inside nested actions ([#202](https://github.com/microsoft/vscode-copilotstudio/pull/202))

[Unreleased]: https://github.com/microsoft/vscode-copilotstudio/compare/v1.8.21...HEAD
[1.8.21]: https://github.com/microsoft/vscode-copilotstudio/releases/tag/v1.8.21
[1.7.20]: https://github.com/microsoft/vscode-copilotstudio/releases/tag/v1.7.20
[1.6.68]: https://github.com/microsoft/vscode-copilotstudio/releases/tag/v1.6.68
[1.5.57]: https://github.com/microsoft/vscode-copilotstudio/releases/tag/v1.5.57
[1.5.55]: https://github.com/microsoft/vscode-copilotstudio/releases/tag/v1.5.55
[1.4.37]: https://github.com/microsoft/vscode-copilotstudio/releases/tag/v1.4.37
