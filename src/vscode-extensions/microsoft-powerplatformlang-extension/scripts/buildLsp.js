
const { buildLanguageServer } = require('../../shared/scripts/buildLsp');
const path = require('path');
const localLanguageServerPath = path.join(__dirname, '..', '..', '..', 'LanguageServers', 'PowerPlatformLS', 'LanguageServerHost', 'LanguageServerHost.csproj');
const outputPath = path.join(__dirname, "..", "lspOut");
const rawArgs = process.argv.slice(2);

let target = 'win-x64';

for (let i = 0; i < rawArgs.length; i++) {
    const arg = rawArgs[i];

    // --target value
    if (arg === '--target' && rawArgs[i + 1]) {
        target = rawArgs[++i];
    }
}

buildLanguageServer('PowerPlatform', localLanguageServerPath, outputPath, target);
