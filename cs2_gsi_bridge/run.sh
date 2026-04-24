#!/bin/sh
set -e

echo "Starting CS2 GSI Bridge on port 3001"
exec dotnet /app/CS2GsiBridge.dll
