# AccessibilityMap — сборка и запуск на .NET 8 (для деплоя, напр. Render.com)
# Локально собирать/запускать НЕ обязательно — хостинг соберёт образ сам.

# Этап сборки
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore Server/AccessibilityMap.Server.csproj
RUN dotnet publish Server/AccessibilityMap.Server.csproj -c Release -o /app/publish

# Этап выполнения
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AccessibilityMap.Server.dll"]
