# 服务端打包器与后端共用上游 TomCat_Engine，提交在此固定；浏览器播放器锁在
# engine.lock.json（e9c2a428），两者 Managed API v5 与 TCPAK 格式一致，可互相加载。
ARG ENGINE_REPOSITORY=https://github.com/chnnasn/TomCat_Engine.git
ARG ENGINE_COMMIT=e9c2a42818504f4f5496b74b30286b85ccae57de

# ---------------------------------------------------------------------------
# 0. 引擎源码：检出并初始化构建 Web Player 所需的最小子模块，供后续阶段共用。
# ---------------------------------------------------------------------------
FROM alpine/git:latest AS source
ARG ENGINE_REPOSITORY
ARG ENGINE_COMMIT

WORKDIR /engine
RUN git clone --filter=blob:none --no-checkout "${ENGINE_REPOSITORY}" . \
    && git checkout --detach "${ENGINE_COMMIT}" \
    && git submodule update --init \
         TomCat/vendor/Box2D TomCat/vendor/glm TomCat/vendor/spdlog TomCat/vendor/ImGuizmo

# ---------------------------------------------------------------------------
# 1. 打包器：构建独立的 Emscripten Web Player（tomcat_player）。
#    它持有引擎真正的 TCPAK writer 与 tc_web_player_cook 入口，所以 Linux
#    容器可以自行打包，不再需要 Windows 桌面 CLI 或额外的 cook 主机。
#    只构建 tomcat_player 目标：不需要 .NET SDK，也不需要 wasm-tools 工作负载。
# ---------------------------------------------------------------------------
FROM emscripten/emsdk:4.0.15 AS player

RUN apt-get update \
    && apt-get install -y --no-install-recommends ninja-build cmake \
    && rm -rf /var/lib/apt/lists/*

COPY --from=source /engine /engine

# emcmake 会重置子进程 PATH，显式传入 Ninja 路径，避免依赖 PATH 查找。
SHELL ["/bin/bash", "-c"]
WORKDIR /engine
RUN . /emsdk/emsdk_env.sh \
    && emcmake cmake -S Web -B build/web -G Ninja \
         -DCMAKE_MAKE_PROGRAM="$(command -v ninja)" \
         -DCMAKE_BUILD_TYPE=Release \
    && cmake --build build/web --target tomcat_player --parallel "$(nproc)" \
    && test -f build/web/tomcat_player.wasm \
    && test -f build/web/tomcat_player.data

# ---------------------------------------------------------------------------
# 2. 托管工具链：用户 C# 脚本的编译输入。
#    TomCat.Managed 是脚本 API 面，TomCat.ScriptGenerator 是源生成器（Roslyn 分析器），
#    它负责把脚本清单嵌进 Assembly-CSharp.dll。两者与打包器来自同一提交。
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS managed

COPY --from=source /engine /engine
WORKDIR /engine
RUN dotnet build Managed/TomCat.Managed/TomCat.Managed.csproj --configuration Release --nologo \
    && dotnet build Managed/TomCat.ScriptGenerator/TomCat.ScriptGenerator.csproj --configuration Release --nologo \
    && mkdir -p /managed \
    && cp Managed/TomCat.Managed/bin/Release/net10.0/TomCat.Managed.dll /managed/ \
    && cp Managed/TomCat.ScriptGenerator/bin/Release/net10.0/TomCat.ScriptGenerator.dll /managed/ \
    && test -f /managed/TomCat.Managed.dll \
    && test -f /managed/TomCat.ScriptGenerator.dll

# ---------------------------------------------------------------------------
# 3. 后端发布
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY NuGet.Config ./
COPY TomCat.Api/TomCat.Api.csproj TomCat.Api/
RUN dotnet restore TomCat.Api/TomCat.Api.csproj --configfile NuGet.Config

COPY TomCat.Api/ TomCat.Api/
RUN dotnet publish TomCat.Api/TomCat.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

# Node 只用于驱动 Web Player 打包，不承载 API 逻辑。
FROM node:22-bookworm-slim AS node

# ---------------------------------------------------------------------------
# 4. 运行时：ASP.NET Core + Node + Web Player + .NET SDK
#    这里刻意用 SDK 镜像而不是 aspnet 镜像：带 C# 脚本的项目需要在容器内运行
#    dotnet build（上游桌面的 CompileManaged 只在 Windows 下可用），MSBuild 与
#    Roslyn 都由 SDK 提供。代价是镜像更大；不需要 C# 打包时可换回 aspnet 镜像。
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime
WORKDIR /app
ARG ENGINE_COMMIT

# worker 只用 Node 内置模块，因此只需要可执行文件本身，不需要 npm 依赖树。
COPY --from=node /usr/local/bin/node /usr/local/bin/node

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_PACKAGES=/tmp/tomcat-nuget \
    PORT=8080 \
    Storage__Directory=/data \
    AllowedHosts=* \
    Cook__CliPath=/usr/local/bin/node \
    Cook__CliArgs="/app/cook/worker.mjs \"{project}\" \"{output}\"" \
    TOMCAT_PLAYER_DIR=/app/player \
    TOMCAT_MANAGED_DIR=/app/managed \
    TOMCAT_COOK_ENGINE_COMMIT=${ENGINE_COMMIT}

COPY --from=build /app/publish .

# Emscripten MODULARIZE 产物是 CommonJS，require() 需要同目录的 package.json 标记。
COPY --from=player /engine/build/web/tomcat_player.js   /app/player/tomcat_player.js
COPY --from=player /engine/build/web/tomcat_player.wasm /app/player/tomcat_player.wasm
COPY --from=player /engine/build/web/tomcat_player.data /app/player/tomcat_player.data
COPY --from=source /engine/Editor/TomCatInut/Packages/fonts/ /app/player/Packages/fonts/
RUN printf '{"type":"commonjs"}\n' > /app/player/package.json

# C# 脚本编译所需的托管程序集（与打包器同一提交）。
COPY --from=managed /managed/ /app/managed/

COPY cook/ /app/cook/

EXPOSE 8080
ENTRYPOINT ["dotnet", "TomCat.Api.dll"]
