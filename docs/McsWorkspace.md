This is a placeholder for workspace documentation. In the future, links to this page should be replaced with official MCS documentation. Official documentation should contain more details about the content of each files and instruction to generate templates.

# Workspace

Our extension requires that you are working in a Copilot Studio workspace structured in the following way:
```
agent.mcs.yml
settings.mcs.yml
icon.png (optional)
actions/
knowledge/
|- files/
topics/
```

Where
* **agent.mcs.yml** : agent instructions
* **settings.mcs.yml** : agent entity properties
* **icon.png** : agent icon
* **actions** : task dialog components
* **knowledge** : directory with one file for each knowledge source
* **knowledge/files**: directory with files to convert as additional knowledge source
* **topics**: topics components