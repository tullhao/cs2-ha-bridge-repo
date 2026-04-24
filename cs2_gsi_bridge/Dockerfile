FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY src/CS2GsiBridge.csproj ./
RUN dotnet restore ./CS2GsiBridge.csproj
COPY src/ ./
RUN dotnet publish ./CS2GsiBridge.csproj -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine
WORKDIR /app
COPY --from=build /out ./
COPY run.sh /run.sh
RUN chmod a+x /run.sh
EXPOSE 3001
CMD ["/run.sh"]
