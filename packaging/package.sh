#!/usr/bin/env bash
# Builds the distribution zips in dist/:
#  - SBMultiplayer-<version>.zip: the mod (Release build, without SBMultiplayer.json so players keep
#    their settings when overwriting the files)
#  - SBMultiplayer-Server-<version>.zip: the dedicated server
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet build src/SBMultiplayer.Client -c Release -nologo -v quiet
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
out=src/SBMultiplayer.Client/bin/Release/net8.0

rm -rf dist/pkg
mkdir -p dist/pkg/SBMultiplayer
cp "$out"/{SBMultiplayer.Client.dll,0Harmony.dll,LiteNetLib.dll,SBMultiplayer.Protocol.dll,SBMultiplayer.Server.dll} dist/pkg/SBMultiplayer/
cp packaging/verify-replays.sh packaging/verify-replays.bat dist/pkg/SBMultiplayer/
cp packaging/INSTALL.txt dist/pkg/

python3 -c "import shutil, sys; shutil.make_archive(sys.argv[1], 'zip', 'dist/pkg')" "dist/SBMultiplayer-$version"
echo "Client package: dist/SBMultiplayer-$version.zip"

# Dedicated server (cross-platform, requires the .NET 8 runtime or newer)
rm -rf dist/server
dotnet publish src/SBMultiplayer.Server -c Release -o dist/server/SBMultiplayer.Server -nologo -v quiet
rm -f dist/server/SBMultiplayer.Server/*.pdb
cp docs/hosting.md docs/replay-verification.md dist/server/
python3 -c "import shutil, sys; shutil.make_archive(sys.argv[1], 'zip', 'dist/server')" "dist/SBMultiplayer-Server-$version"
echo "Server package: dist/SBMultiplayer-Server-$version.zip"
