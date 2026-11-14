# Héberger un serveur

## Lancer

```sh
dotnet build src/SBMultiplayer.Server -c Release
dotnet src/SBMultiplayer.Server/bin/Release/net8.0/SBMultiplayer.Server.dll [--port 24800] [--max-players 32] [--echo]
```

Commandes console : `list`, `quit`. L'option `--echo` renvoie à chaque joueur sa propre planche avec 2 s de retard, pour tester seul.

## Réseau

1. **Pare-feu** (Linux avec ufw) : `sudo ufw allow 24800/udp`
2. **Box** : rediriger le port **UDP** 24800 vers l'IP locale du serveur.
3. Les joueurs mettent `"Server": "IP_PUBLIQUE:24800"` dans `SBMultiplayer/SBMultiplayer.json`.

### ⚠️ PC connecté en Ethernet ET en Wi-Fi

Les réponses du serveur partent par l'interface de la route par défaut (souvent l'Ethernet). Si la redirection de la box pointe vers l'IP **Wi-Fi**, les joueurs atteignent le serveur mais ne reçoivent jamais ses réponses. Dans le log du serveur, on voit alors des `Connexion de …` suivis de `Déconnexion de  (#n) : Reconnect` toutes les ~5 s, sans pseudo.

Vérifier l'interface utilisée avec `ip route get <IP du joueur>`, et faire pointer la redirection vers cette adresse `src`. Autre solution : couper le Wi-Fi.

### Sans redirection de port

En cas de box en CGNAT, ou de redirection impossible : installer [Tailscale](https://tailscale.com/) (ou ZeroTier) sur le serveur et chez les joueurs, puis utiliser l'IP Tailscale du serveur.

## Testé

2026-11-14 : 3 joueurs (Linux et internet), 6 maps officielles. Fluide, avec seulement les 100 ms de retard d'interpolation prévus.
