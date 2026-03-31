FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Kopiert alles in den Container
COPY . ./
RUN dotnet restore
RUN dotnet publish -c Release -o out

# Erstellt die Start-Umgebung
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/out .

# HIER IST DIE ÄNDERUNG:
# Railway sucht jetzt nach der Datei 'New folder.dll'
ENTRYPOINT ["dotnet", "New folder.dll"]