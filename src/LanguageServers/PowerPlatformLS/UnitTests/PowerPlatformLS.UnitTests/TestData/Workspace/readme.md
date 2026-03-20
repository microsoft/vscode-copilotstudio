This directory contains 2 sub-directories:

1. SolutionExport:
    * Solution files, as they are organized when downloading a solution from portal or CLI
2. LocalWorkspace:
    * Expected file structures when editing files in MCS Language Server.
    * The content of the files should miror the content of SolutionExport directory.
    * Content:
      * **agent.yml** : content of `botcomponents\cree9_agent.gpt.default\data` without "kind" property, which will default to "GptComponentMetadata"
      * **settings.yml** : contains the BotDefinition's entity property, which are spread in `bots\cree9_agent\bot.xml` and `bots\cree9_agent\configuration.json`
      * **icon.png** : "iconbase64" property from `bots\cree9_agent\bot.xml` decoded as png
      * **actions** : contains one file foreach directories under `botcomponents` where the data file starts with `kind: TaskDialog`. Each file is a copy of the matching `data` file, with an additional comment header for the name and description coming from the matching `xml` file.
      * **knowledge** : contains 
        * one file foreach directories under `botcomponents` where the data file starts with `kind: KnowledgeSourceConfiguration`. Each file contain the `source` section of the `data` file.
        * a `files` sub-directory containing files from `filedata` under each `botcomponents`. Each file can have a `yml` companion with additional metadata (name, description). Errors if `yml` doesn't have a matching file (i.e. yml knowledge source is not supported: files extension should be changed).
      * **topics**: Same as `actions` but for components with `kind: AdaptiveDialog`