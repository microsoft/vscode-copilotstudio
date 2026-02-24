
const fs = require('fs');
const { spawn } = require('child_process');

/**
 * Builds and watches the specified language server in watch mode.
 *
 * @param {string} serverName - The name of the language server.
 * @param {string} localLanguageServerPath - The path to the local language server host project.
 * @param {string} outputPath - The output path for the build artifacts.
 * @param {string} target - The runtime target.
 * @throws {Error} If the local language server path does not exist.
 *
 * @example
 * const { watchLanguageServer } = require('shared/scripts/watchLsp');
 * const path = require('path');
 * const localLanguageServerPath = path.join(__dirname, '..', '..', '..', 'LanguageServers', 'PowerPlatformLS', 'LanguageServerHost');
 * const outputPath = path.join(__dirname, "..", "lspOut");
 * watchLanguageServer('PowerPlatform', localLanguageServerPath, outputPath);
 */
function watchLanguageServer(serverName, localLanguageServerPath, outputPath, target) {
    console.log(`Starting to build ${serverName} language server in watch mode, targeting ${target}`);  
    
    if (!fs.existsSync(localLanguageServerPath)) {
        throw new Error("LocalPowerFxLanguageServer not found");
    }
    
    process.chdir(localLanguageServerPath);
    spawn(`dotnet`, ["watch","build", "--runtime", target, "--", `-property:OutDir=${outputPath}`, "-property:Configuration=Debug"], { stdio: 'inherit' });
    console.log(`Done starting build process for ${serverName} language server in watch mode`);
}

module.exports = {
    watchLanguageServer
  };
  