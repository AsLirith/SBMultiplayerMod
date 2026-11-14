#!/usr/bin/env bash
# Construit le paquet testeurs : dist/SBMultiplayer-<version>.zip (client en Release, sans SBMultiplayer.json
# pour que chacun garde sa configuration en écrasant les fichiers).
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet build src/SBMultiplayer.Client -c Release -nologo -v quiet
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
out=src/SBMultiplayer.Client/bin/Release/net8.0

rm -rf dist/pkg
mkdir -p dist/pkg/SBMultiplayer
cp "$out"/{SBMultiplayer.Client.dll,0Harmony.dll,LiteNetLib.dll,SBMultiplayer.Protocol.dll} dist/pkg/SBMultiplayer/
cp packaging/INSTALL.txt dist/pkg/

python3 -c "import shutil, sys; shutil.make_archive(sys.argv[1], 'zip', 'dist/pkg')" "dist/SBMultiplayer-$version"
echo "Paquet : dist/SBMultiplayer-$version.zip"

# Serveur dédié (multiplateforme, nécessite le runtime .NET 8 ou plus récent)
rm -rf dist/server
dotnet publish src/SBMultiplayer.Server -c Release -o dist/server/SBMultiplayer.Server -nologo -v quiet
rm -f dist/server/SBMultiplayer.Server/*.pdb
cp docs/hosting.md dist/server/
python3 -c "import shutil, sys; shutil.make_archive(sys.argv[1], 'zip', 'dist/server')" "dist/SBMultiplayer-Server-$version"
echo "Serveur : dist/SBMultiplayer-Server-$version.zip"
