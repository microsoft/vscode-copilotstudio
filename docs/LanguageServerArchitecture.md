
[[_TOC_]]

# Power Platform Language Server Architecture

This file contains architecture details and guiding principles for the **Power Platform Language Server** and VS Code client extension.
It contains only high level architecture for the components required to implement the [Language Server Protocol (LSP)](https://en.wikipedia.org/wiki/Language_Server_Protocol).

For details on the LSP features that are currently supported, see [MCS Language Server Capabilities](McsLanguageServerSpecs.md).

## Client-Server Architecture

The client-server architecture implements the [Language Server Protocol (LSP)](https://en.wikipedia.org/wiki/Language_Server_Protocol) and follows the [VS Code language server guidance](https://code.visualstudio.com/api/language-extensions/language-server-extension-guide).

The core components are designed to be shareable for creating other language servers and VS Code extensions. This extensibility is highlighted by the greyed-out bubbles in the diagram below, indicating potential future language services and extensions.

Planning for extensibility helps developers focus and scope the impact of their changes.

> **For example**, changes impacting LSP implementation and communication protocol should go in shared libraries, while changes impacting language syntax and IDE features should go in the language implementation library. This way, each can be abstracted with minimal dependency between them.

![LSP-Client-Server](./images/LSP-Client-Server.png)

1. <span style="background-color:darkorange;color:black;">Shared</span> and <span style="background-color:lightblue;color:black;">Impl.LspProcessor</span> implement the [LSP](https://en.wikipedia.org/wiki/Language_Server_Protocol) on client and server respectively, without IDE or language-specific logic.
2. <span style="background-color:darkblue;color:white;">Impl.Language.*</span> contains implementation details of language analyzers, syntax tree builders, and IDE features.
3. <span style="background-color:darkblue;color:white;">Contract.Internal</span> defines interfaces used internally through Dependency Injection. See [Server](#server) for details.

## Repo Files Structure
<BLOCKQUOTE>
LanguageServers <br/>
<span style="background-color:lightblue;color:black;">
|- Contract.Lsp <br/>
|- Impl.LspProcessor <br/>
</span>
<span style="background-color:darkblue;color:white;">
|- Contract.* <br/>
|- Impl.* <br/>
|- Impl.Language.* <br/>
</span>
<span style="background-color:green;color:white;">
|- LanguageServerHost <br/>
</span>
vscode-extensions <br/>
<span style="background-color:darkorange;color:black;">
|- microsoft-powerplatformlang-extension <br/>
|- shared <br/>
</span>
</BLOCKQUOTE>


The [Server](#server-architecture) section covers the functioning and reasoning behind the separation of `Contract` and `Impl` libraries.

## Server Architecture

This section details the language server architecture and guiding principles. Developers should be familiar with these principles and keep them up to date to ensure code cohesion.

Here is an overview of the server components and their relationships (X --> Y means X depends on Y). The color code from the [Client-Server](#client-server-architecture) section is preserved.

![LSP-Server](./images/LSP-Server.png)

1. `Contract` libraries contain data classes and interfaces, most of which are publicly visible.
2. `Impl` libraries define internal components and expose only DI utilities. They don't know about each other's implementations, which is crucial for maintainability and avoiding ["Spaghetti code"](https://en.wikipedia.org/wiki/Spaghetti_code).

Duplication is avoided by having components shared through dependency injection and proper interface definition in `Contract.Internal`. For more benefits of sharing code through internal interfaces, see [Architecture Details](#architecture-details).

### Architecture Details

This section contains the guiding principles for the architecture presented above.
It explains the **reasons for breaking down the server code into several projects rather than a few**.
For more reasoning details, I recommend the book [Software Architecture with C# 12 and .NET 8 - Fourth Edition](https://learning.oreilly.com/library/view/software-architecture-with/9781805127659/) (available for free for FTE).

#### Guiding Principles and Benefits

1. **Loose Coupling and Modularity**
   * **Separation of Concerns**: By having implementation libraries (`Impl.*`) depend on contract libraries (`Contract.*`), you ensure that each library focuses on a specific aspect of the application. This separation makes the codebase more modular and easier to manage.
   * **Interchangeability**: Interfaces in the contract libraries define the expected behavior, allowing different implementations to be swapped without changing the dependent code. **This is particularly useful for testing** and future enhancements.

2. **Scalability and Maintainability**
   - **Independent Development**: Each implementation library can be developed, tested, and deployed independently, reducing the risk of changes affecting other libraries.
   - **Ease of Updates**: Features or bug fixes can be implemented in specific libraries without impacting the entire system, **simplifying updates and maintenance**.

3. **Enhanced Testability**
   - **Mocking and Stubbing**: Interfaces make it easier to create mock implementations for unit testing, leading to more reliable and robust code.
   - **Dependency Injection (DI)**: DI frameworks can inject dependencies at runtime, simplifying component testing.

4. **Flexibility and Extensibility**
   * **Adding New Features**: New features can be added by creating new implementation libraries without modifying existing ones. For example, if `Impl.Language.X` needs a feature from `Impl.Language.Y`, it can access it through an interface defined in `Contract.Internal` and provided via DI. Libraries are treated like black-box services.
   * **Extending Functionality**: As new requirements emerge, additional interfaces and implementations can be added to the contract and implementation libraries, respectively. **This extensibility ensures that the architecture can evolve** with changing needs.

5. **Reduced Interdependencies**
   - **Minimized Coupling**: Avoiding direct dependencies between implementation libraries reduces the risk of circular dependencies and complex dependency chains.
   - **Clear Boundaries**: The architecture enforces clear boundaries, **making it easier to understand and reason about the codebase**.

### Downsides and Challenges

1. **Documentation and Arbitration**: Careful decisions are needed on project splitting and bundling. When bundling projects together, think of whether all planned features should have inter-dependencies. The goal is to establish a foundation for future growth, avoiding tech debt.
2. **Overhead of Abstraction**: Too many layers of abstraction can lead to over-engineering, making the codebase harder to understand and maintain.
3. **Unnecessary Complexity**: If the architecture is too complex for the problem it solves, it can lead to unnecessary complexity without significant benefits.

Other known challenges have relatively simple mitigation strategies.

# Next

To dig deeper into LSP capabilities currently supported and planned for the future, see [MCS Language Server Capabilities](McsLanguageServerSpecs.md).