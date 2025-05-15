# Overview of the Copilot Studio Extension

The `Copilot Studio Extension` is a Visual Studio Code extension designed to enhance the development experience for Microsoft Copilot Studio projects. It provides language support and authoring capabilities for Copilot Studio.

## Technology Preview Release

This is a technology preview release.

##  Overview

The Copilot Studio Extension for Visual Studio Code is a new extension, accessible from the Visual Studio Marketplace. It can be searched for and installed just like any other extension. After installation, it allows a user to connect to a Copilot Studio tenant where they can interact with the agents through code, rather than the UI. The extension provides language support and intellisense that supports the development workflow of agents built in Copilot Studio.

The screenshots below provide a high-level view of how this works. Note: These screenshots are all of the desktop IDE, however extensions operate identically across both the desktop IDE and Visual Studio Code for the Web.

## Installation
First, the user searches for Copilot Studio through one of three entry points. The Visual Studio Code IDE, Visual Studio Code for the Web, or directly through the Visual Studio Marketplace. After installation, the extension shows up in both editors (desktop IDE and the web).

<img src=https://raw.githubusercontent.com/microsoft/vscode-copilotStudio/main/images/ConnectToCopilotStudio.jpg width=734 height=413>

## Connect to Copilot Studio
After installation, the user is prompted to sign-in to Copilot Studio and is presented with a list of the agents associated with their account. Each agent can be expanded to see the editable components of the agent, including Knowledge sources, Actions, Topics, and Triggers.

<img src=https://raw.githubusercontent.com/microsoft/vscode-copilotStudio/main/images/ControlPlane.jpg width=734 height=413>

## Editing
Each component can then be edited by double-click to open and directly editing the YAML for each component. Because YAML files are natively supported in Visual Studio Code, the Copilot Studio extension can provide rich intellisense and guided tips.

<img src=https://raw.githubusercontent.com/microsoft/vscode-copilotStudio/main/images/OpenAndEdit.jpg width=734 height=413>

<img src=https://raw.githubusercontent.com/microsoft/vscode-copilotStudio/main/images/OpenAndEdit2.jpg width=734 height=413>

Edits are saved directly to Copilot Studio. Note: This is important, as this is different than having a local instance of the agent, which is then deployed to Copilot Studio. The experience provided by the Extension is “live editing” of a cloud resource.