const nbgv = require('nerdbank-gitversioning')
nbgv.setPackageVersion('.')
nbgv.getVersion('.').then( (version)=> {
    console.log('##vso[task.setvariable variable=CUSTOM_VERSION;]' + version.npmPackageVersion)
})
