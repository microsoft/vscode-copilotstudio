// Just a starter script to build the language server
// Expected to change to support multiple os and when publishing the extension

const fs = require('fs');   
const exec = require('child_process').execSync;

const yargs = require('yargs/yargs')
const { hideBin } = require('yargs/helpers');
const args = yargs(hideBin(process.argv)).option("mode", {
    alias: "m",
    describe: "Build mode",
    choices: ["Debug", "Release"],
    default: "Release"
}).parse();

const mode = args.mode;

/**
 * Builds the specified language server.
 *
 * @param {string} serverName - The name of the language server.
 * @param {string} localLanguageServerPath - The path to the local language server host project.
 * @param {string} outputPath - The output path for the build artifacts.
 * @param {string} target - The runtime target.
 * @throws {Error} If the local language server path does not exist.
 *
 * @example
 * const { buildLanguageServer } = require('shared/scripts/buildLsp');
 * const path = require('path');
 * const localLanguageServerPath = path.join(__dirname, '..', '..', '..', 'LanguageServers', 'LanguageServerHost', 'PowerPlatformLS', 'LanguageServerHost.csproj');
 * const outputPath = path.join(__dirname, "..", "lspOut");
 * buildLanguageServer('PowerPlatform', localLanguageServerPath, outputPath);
 */
function buildLanguageServer(serverName, localLanguageServerPath, outputPath, target) {
  console.log(`Building ${serverName} language server at path: ${localLanguageServerPath} in ${mode} mode , targeting ${target} platform`);

  if (!fs.existsSync(localLanguageServerPath)) {
      throw new Error(`Local ${serverName} LanguageServer not found`);
  }

  try {
    exec(`dotnet build ${localLanguageServerPath} -o ${outputPath} -c ${mode} --runtime ${target}`);
  } catch (error) {
    console.error(`Failed to build language server: ${error.message}`);
    throw error;
  }
}

// Export the function
module.exports = {
  buildLanguageServer
};

