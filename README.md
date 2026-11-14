# SBMultiplayer

Mod multijoueur non officiel pour [STUNTBOOST](https://store.steampowered.com/app/2999500/STUNTBOOST/), façon « time attack » : tous les joueurs roulent en même temps sur la même map, se voient sous forme de ghosts (sans collisions) et se disputent le meilleur temps du round.

> Version de test, déjà jouable entre amis. Testée sous Linux (version native du jeu). Windows : à confirmer.

## Fonctionnalités

- **Serveur dédié** (application console, sans le jeu) : rotation de maps, rounds chronométrés, classement en direct, écran de résultats.
- **Ghosts des autres joueurs** en temps réel, fluides (interpolation), avec **leur skin** et **leur pseudo** quand ils sont proches.
- **Map du round chargée automatiquement**, retry immédiat à l'arrivée au lieu de l'écran de fin.
- **Menu en jeu (F8)** : serveur, pseudo, connexion, opacité et affichage des autres joueurs, pseudos, taille du panneau.
- **F7** : rejoindre la map du round. Notifications de connexion, de joueurs, de rounds et de temps.

⚠️ **Avec le mod, le jeu n'enregistre aucun temps**, ni dans les classements officiels (le jeu le bloque lui-même dès qu'un mod est chargé), ni en local.

## Installation (joueurs)

1. Télécharger le zip de la dernière [release](../../releases/latest).
2. Copier le dossier `SBMultiplayer` dans le dossier du jeu (Steam > STUNTBOOST > Gérer > Parcourir les fichiers locaux).
3. Options de lancement Steam :
   - **Linux** : `DOTNET_STARTUP_HOOKS="/chemin/vers/STUNTBOOST/SBMultiplayer/SBMultiplayer.Client.dll" %command%`
   - **Windows** : `cmd /c "set DOTNET_STARTUP_HOOKS=C:\chemin\vers\STUNTBOOST\SBMultiplayer\SBMultiplayer.Client.dll&& %command%"`
4. Lancer le jeu, appuyer sur **F8** et entrer l'adresse du serveur.

Pour désinstaller, retirer l'option de lancement. Le log se trouve dans `SBMultiplayer/SBMultiplayer.log`.

## Héberger un serveur

Voir [docs/hosting.md](docs/hosting.md) (pare-feu, redirection de port, Tailscale…).

```sh
dotnet run --project src/SBMultiplayer.Server -c Release -- --maps o:1,o:2,o:3 --round 300
```

## Compiler

Prérequis : SDK .NET 8 ou plus récent, et le jeu installé.

```sh
dotnet build -p:GameDir=/chemin/vers/steamapps/common/STUNTBOOST   # compile et copie le mod dans le jeu
./packaging/package.sh                                               # zip de distribution dans dist/
```

## Comment ça marche

Le mod est chargé par un [startup hook .NET](https://github.com/dotnet/runtime/blob/main/docs/design/features/host-startup-hook.md) et patche le jeu en mémoire avec [Harmony](https://github.com/pardeike/Harmony), sans modifier aucun fichier du jeu. Le réseau utilise [LiteNetLib](https://github.com/RevenantX/LiteNetLib) (UDP). Notes techniques dans [docs/00-reverse-engineering.md](docs/00-reverse-engineering.md).

## Légal

Code sous licence [MIT](LICENSE). Ce dépôt ne contient aucun code ni asset du jeu, et ne doit jamais en contenir (pas de code décompilé).
STUNTBOOST est la propriété de Byting Games. Ce projet n'est ni affilié au studio ni soutenu par lui.
