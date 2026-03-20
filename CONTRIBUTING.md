# Contributing to the Copilot Studio Extension for VS Code

Thank you for your interest in the Copilot Studio extension! This project is
the source code for the
[Copilot Studio extension](https://marketplace.visualstudio.com/items?itemName=ms-CopilotStudio.mscopilotStudio)
in the Visual Studio Code Marketplace.

## Current Build State

> **Source code is complete; build requires internal dependencies.**
>
> This repository contains the full source code that produces the shipping
> extension. However, the build currently requires NuGet packages from an
> internal feed that is not accessible to external contributors.

| Area | Status |
|------|--------|
| **Source code** | Complete — matches the published extension |
| **Build** | Requires internal `Microsoft.Agents.*` NuGet packages. **External builds are not supported yet.** |
| **Unit tests** | 530 unit tests + 13 LSP journal tests pass internally |
| **VSIX packaging** | Builds internally for 6 platform targets (win32-x64, win32-arm64, linux-x64, linux-arm64, darwin-x64, darwin-arm64) |

### Roadmap to external buildability

The internal NuGet dependency comes from the cloud sync module (PullAgent),
which uses Microsoft's internal Dataverse authoring SDK. We are replacing this
with delegated use of [PAC (Power Platform CLI)](https://learn.microsoft.com/power-platform/developer/cli/introduction),
which will eliminate the internal package requirements entirely.

Once that migration is complete:
- `nuget.config` will reference only nuget.org
- `dotnet build` and `dotnet test` will work for external contributors
- This file will be updated to reflect the fully open contribution model

In the meantime, the source code is available for review, issue filing with
source context, and understanding the extension's architecture.

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

## Reporting Issues

Please use [GitHub Issues](https://github.com/microsoft/vscode-copilotstudio/issues).
We provide issue templates for bug reports and feature requests.

For bugs, we recommend using the extension's built-in issue reporter:

1. Open the Command Palette (`Ctrl+Shift+P` / `Cmd+Shift+P`)
2. Run **Help: Copilot Studio: Report Issue**

This automatically includes diagnostic information like your session ID, which
helps us investigate more efficiently.

## Submitting Changes

> **Note:** External pull requests that require building the project cannot be
> validated until the internal NuGet dependencies are removed (see roadmap
> above). We welcome contributions and will review them as capacity allows —
> documentation fixes, TypeScript extension changes, and issue reports are
> particularly easy to accept during this period.

1. Fork the repository and create a feature branch from `main`.
2. Make your changes, following the existing code style.
3. Ensure your changes do not introduce new warnings or errors (where possible
   — see Build State above).
4. Submit a pull request with a clear description of the change and its motivation.

### Pull Request Guidelines

- Keep PRs focused — one logical change per PR.
- Include a summary of *what* changed and *why*.
- Reference any related issues (e.g., `Fixes #123`).
- For bug fixes, describe how to reproduce the original problem.

## Development Prerequisites

To build this project, you currently need access to an internal NuGet feed.
We are working to remove this requirement (see roadmap above).

The toolchain requirements are:

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- [Node.js 22](https://nodejs.org/) LTS or later
- [Visual Studio Code](https://code.visualstudio.com/)

## Internal Development (Microsoft Employees)

If you have access to the internal NuGet feed that provides the
`Microsoft.Agents.*` packages, you can build and test the full project today.

### 1. Configure the internal NuGet source

The repo's `nuget.config` lists only `nuget.org`. Because it does **not** use
`<clear />`, NuGet automatically merges it with your user-level configuration.
Add the internal feed to your user-level config once:

```sh
dotnet nuget add source <feed-url> --name CCI-Dependency \
    --configfile ~/.nuget/NuGet/NuGet.Config
```

> The feed URL is available from your team's internal onboarding documentation
> or the ObjectModel monorepo's `nuget.config`.

After this, `dotnet restore` resolves all six `Microsoft.Agents.*` packages
automatically — three from nuget.org and three from the internal feed.

### 2. Build and test the language server

```sh
dotnet build src/LanguageServers/PowerPlatformLS/PowerPlatformLS.sln
dotnet test  src/LanguageServers/PowerPlatformLS/PowerPlatformLS.sln
```

### 3. Build the VS Code extension

```sh
cd src/vscode-extensions
npm install
npm run build
```

### Working toward external buildability

Four `Microsoft.Agents.*` packages are currently only available on the
internal feed: `Platform.Content`, `Platform.Content.Internal`,
`ObjectModel.Dataverse`, and `ObjectModel.NodeGenerators`. We are eliminating
these by migrating PullAgent to delegate through
[PAC CLI](https://learn.microsoft.com/power-platform/developer/cli/introduction).
When that work completes, `nuget.config` will reference only `nuget.org` and
external contributors will be able to build, test, and submit verified PRs.

Contributions that advance this migration are especially valuable.

## Code of Conduct

This project has adopted the
[Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) for details.

## License

This project is licensed under the [MIT License](LICENSE).
