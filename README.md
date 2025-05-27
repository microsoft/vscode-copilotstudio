# Copilot Studio extension for Visual Studio Code

The Copilot Studio extension for Visual Studio Code is designed to enhance the development experience of Microsoft Copilot Studio agents. It provides language support, Intellisense, and authoring capabilities for Copilot Studio agent components.

After installation, the extension prompts you to sign in to Copilot Studio. It can then show you a list of the agents associated with your environment. Clone an agent to see its editable components, including knowledge sources, actions, topics, and triggers.

## Technology preview release

This is a technology preview release. Preview releases are only available for Windows/x64 versions of Visual Studio Code.

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

To edit any component, open the corresponding file and make the desired changes. Since Visual Studio Code natively supports YAML files, the Copilot Studio extension can provide rich Intellisense and guided tips.

![Screenshot of an agent topic open for editing with the Copilot Studio extension in Visual Studio code](https://raw.githubusercontent.com/microsoft/vscode-copilotStudio/main/images/edit-topic.png)

## Sync your changes

The Copilot Studio extension uses the same source control features as Visual Studio Code. **Fetch changes**, **Pull changes**, and **Push changes** icons are available in both the **Explorer** panel and the **Source Control** panel of Visual Studio Code.

- To preview any remote changes from Copilot Studio, use **Fetch changes**.
- To get all remote changes from Copilot Studio, use **Pull changes**.
- To push your local changes from Visual Studio Code to Copilot Studio, use **Push changes**.

When you push changes they are saved directly to Copilot Studio. This is different than having a local instance of the agent, which you would then deploy to Copilot Studio. The extension provides a _live editing_ experience of a cloud resource.
