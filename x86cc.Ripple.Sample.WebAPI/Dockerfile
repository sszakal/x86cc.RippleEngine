FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Central Package Management: Directory.Packages.props holds EVERY version, and the ProjectReferences must be
# present too, so all of them have to land before restore — copying this csproj alone fails with NU1015
# ("no version specified") because CPM has nothing to resolve against.
COPY ["Directory.Packages.props", "./"]
COPY ["x86cc.Ripple.Sample.WebAPI/x86cc.Ripple.Sample.WebAPI.csproj", "x86cc.Ripple.Sample.WebAPI/"]
COPY ["x86cc.Ripple.Sample.Domain/x86cc.Ripple.Sample.Domain.csproj", "x86cc.Ripple.Sample.Domain/"]
COPY ["x86cc.RippleEngine.Storage/x86cc.RippleEngine.Storage.csproj", "x86cc.RippleEngine.Storage/"]
COPY ["x86cc.RippleEngine.Core/x86cc.RippleEngine.Core.csproj", "x86cc.RippleEngine.Core/"]
COPY ["x86cc.RippleEngine.MartenDb/x86cc.RippleEngine.MartenDb.csproj", "x86cc.RippleEngine.MartenDb/"]
RUN dotnet restore "x86cc.Ripple.Sample.WebAPI/x86cc.Ripple.Sample.WebAPI.csproj"
COPY . .
WORKDIR "/src/x86cc.Ripple.Sample.WebAPI"
RUN dotnet build "./x86cc.Ripple.Sample.WebAPI.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./x86cc.Ripple.Sample.WebAPI.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "x86cc.Ripple.Sample.WebAPI.dll"]
