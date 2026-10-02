
const { watchLanguageServer } = require('../../shared/scripts/watchLsp');
const { getHostRuntimeIdentifier } = require('../../shared/scripts/hostRuntime');
const path = require('path');
const localLanguageServerPath = path.join(__dirname, '..', '..', '..', 'LanguageServers', 'PowerPlatformLS', 'LanguageServerHost');
const outputPath = path.join(__dirname, "..", "lspOut");
const rawArgs = process.argv.slice(2);

let target = getHostRuntimeIdentifier();

for (let i = 0; i < rawArgs.length; i++) {
    const arg = rawArgs[i];

    // --target value
    if (arg === '--target' && rawArgs[i + 1]) {
        target = rawArgs[++i];
    }
}

watchLanguageServer('PowerPlatform', localLanguageServerPath, outputPath, target);
