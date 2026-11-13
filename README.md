# SBMultiplayer

Mod multijoueur non officiel pour [STUNTBOOST](https://store.steampowered.com/app/2999500/STUNTBOOST/), façon « time attack » : les joueurs roulent en même temps sur la même map et se voient sous forme de ghosts, sans collisions.

> 🚧 En développement : rien de jouable pour l'instant.

## Principe

- Injection via un [startup hook .NET](https://github.com/dotnet/runtime/blob/main/docs/design/features/host-startup-hook.md) + [Harmony](https://github.com/pardeike/Harmony), sans modifier les fichiers du jeu.
- Prévu : un serveur dédié (tournois), un listen server et du P2P Steam.
- Le jeu bloque automatiquement l'envoi des runs aux classements officiels quand un mod est chargé. **Avec le mod, le jeu n'enregistre pas non plus les temps en local.**

## Build

Prérequis : SDK .NET 8 ou plus récent, et le jeu installé.

```sh
dotnet build -p:GameDir=/chemin/vers/steamapps/common/STUNTBOOST
```

Le mod est copié dans `<GameDir>/SBMultiplayer/`.

## Installation (Linux natif)

Options de lancement Steam :

```
DOTNET_STARTUP_HOOKS=<GameDir>/SBMultiplayer/SBMultiplayer.Client.dll %command%
```

Pour désinstaller, retirer l'option de lancement. Le log se trouve dans `<GameDir>/SBMultiplayer/SBMultiplayer.log`.

## Légal

Ce dépôt ne contient aucun code ni asset du jeu. Il ne faut jamais y ajouter de code décompilé.
STUNTBOOST est la propriété de Byting Games. Ce projet n'est pas affilié au studio.
