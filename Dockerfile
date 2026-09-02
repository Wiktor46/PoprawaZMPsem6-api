FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Kopiowanie i przywracanie zależności projektu
COPY ["LibraryApi/LibraryApi.csproj", "LibraryApi/"]
RUN dotnet restore "LibraryApi/LibraryApi.csproj"

# Kopiowanie reszty plików i publikacja
COPY . .
WORKDIR "/src/LibraryApi"
RUN dotnet publish "LibraryApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Konfiguracja portu dla Render.com
ENV ASPNETCORE_HTTP_PORTS=8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

ENTRYPOINT ["dotnet", "LibraryApi.dll"]
