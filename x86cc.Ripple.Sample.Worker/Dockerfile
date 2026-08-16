FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

# The Angular dashboard, built in its own Node stage. The .NET SDK image has no Node, so the Dashboard csproj's
# BuildAngularDashboard target detects the missing npm and SKIPS with a warning — meaning a container build can
# only ever embed a bundle that came in from the build context. Building it here makes the image self-contained
# and lets the generated output stay out of git.
FROM node:20 AS spa
WORKDIR /spa
# package*.json first so `npm ci` is cached independently of source edits.
COPY ["x86cc.RippleEngine.Dashboard/spa/package.json", "x86cc.RippleEngine.Dashboard/spa/package-lock.json", "./"]
RUN npm ci
COPY x86cc.RippleEngine.Dashboard/spa/ ./
# angular.json points outputPath at ../dist, so the build lands one level up (the Dashboard project's dist/).
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Central Package Management: Directory.Packages.props holds EVERY version, and the ProjectReferences must be
# present too, so all of them have to land before restore — copying the Worker csproj alone fails with NU1015
# ("no version specified") because CPM has nothing to resolve against.
COPY ["Directory.Packages.props", "./"]
COPY ["x86cc.Ripple.Sample.Worker/x86cc.Ripple.Sample.Worker.csproj", "x86cc.Ripple.Sample.Worker/"]
COPY ["x86cc.Ripple.Sample.Domain/x86cc.Ripple.Sample.Domain.csproj", "x86cc.Ripple.Sample.Domain/"]
COPY ["x86cc.RippleEngine.Hosting/x86cc.RippleEngine.Hosting.csproj", "x86cc.RippleEngine.Hosting/"]
COPY ["x86cc.RippleEngine.Dashboard/x86cc.RippleEngine.Dashboard.csproj", "x86cc.RippleEngine.Dashboard/"]
COPY ["x86cc.RippleEngine.Engine/x86cc.RippleEngine.Engine.csproj", "x86cc.RippleEngine.Engine/"]
COPY ["x86cc.RippleEngine.Storage/x86cc.RippleEngine.Storage.csproj", "x86cc.RippleEngine.Storage/"]
COPY ["x86cc.RippleEngine.Core/x86cc.RippleEngine.Core.csproj", "x86cc.RippleEngine.Core/"]
COPY ["x86cc.RippleEngine.MartenDb/x86cc.RippleEngine.MartenDb.csproj", "x86cc.RippleEngine.MartenDb/"]
RUN dotnet restore "x86cc.Ripple.Sample.Worker/x86cc.Ripple.Sample.Worker.csproj"
COPY . .
# The built bundle lands where the Dashboard csproj embeds it from.
COPY --from=spa /dist/ x86cc.RippleEngine.Dashboard/dist/
WORKDIR "/src/x86cc.Ripple.Sample.Worker"
# BuildSpa=false: the SPA is already built above, so don't re-probe for npm (it isn't in this image anyway).
RUN dotnet build "./x86cc.Ripple.Sample.Worker.csproj" -c $BUILD_CONFIGURATION -o /app/build -p:BuildSpa=false

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./x86cc.Ripple.Sample.Worker.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false -p:BuildSpa=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "x86cc.Ripple.Sample.Worker.dll"]
