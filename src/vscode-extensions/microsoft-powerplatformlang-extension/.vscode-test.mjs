import { defineConfig } from '@vscode/test-cli';

export default defineConfig({
	files: 'client/out/tests/**/*.test.js',
	workspaceFolder: '../../LanguageServers/PowerPlatformLS/UnitTests/PowerPlatformLS.UnitTests/TestData/WorkspaceWithSubAgents',
});
