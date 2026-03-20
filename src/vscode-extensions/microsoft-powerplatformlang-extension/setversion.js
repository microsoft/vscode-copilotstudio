const nbgv = require('nerdbank-gitversioning');
nbgv.setPackageVersion('.');

// When this is run in the Azure Devops build pipeline, the build number is needed as an
// environment variable so the manifest and signing files can be created with the same name
// as the VSIX itself. The odd syntax below exports the version number into an
// environment variable so it's easy to consume in the build. More details on the syntax can
// be found here:
// https://learn.microsoft.com/en-us/azure/devops/pipelines/process/set-variables-scripts?view=azure-devops&tabs=bash
nbgv.getVersion('.')
    .then((version) => {
        console.log('##vso[task.setvariable variable=EXTENSION_VERSION;]' + version.npmPackageVersion);
        console.log('##vso[task.setvariable variable=EXTENSION_VERSION_OUT;isOutput=true]' + version.npmPackageVersion);
    })
    .catch(err => console.error(err));