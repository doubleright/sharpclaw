# ============================================================
# Stage 1: Build
# ============================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine3.23 AS build

WORKDIR /src

# -- sharc submodule build infrastructure (needed by sharc csproj)
COPY sharc/Directory.Build.props sharc/
COPY sharc/Directory.Packages.props sharc/

# -- sharc source project files (transitive dependency chain)
COPY sharc/src/Sharc.Engine/Sharc.Engine.csproj sharc/src/Sharc.Engine/
COPY sharc/src/Sharc.Crypto/Sharc.Crypto.csproj sharc/src/Sharc.Crypto/
COPY sharc/src/Sharc.Query/Sharc.Query.csproj   sharc/src/Sharc.Query/
COPY sharc/src/Sharc/Sharc.csproj               sharc/src/Sharc/
COPY sharc/src/Sharc.Vector/Sharc.Vector.csproj  sharc/src/Sharc.Vector/

# -- sharpclaw project file
COPY sharpclaw/sharpclaw.csproj sharpclaw/

# Restore (cached unless csproj/props change)
RUN dotnet restore sharpclaw/sharpclaw.csproj

# Copy source code
COPY sharc/src/ sharc/src/
COPY sharpclaw/ sharpclaw/

# Publish framework-dependent (no bundled runtime)
RUN dotnet publish sharpclaw/sharpclaw.csproj \
    -c Release \
    -o /app/publish \
    --no-self-contained \
    --no-restore


# ============================================================
# Stage 2: Runtime
# ============================================================
FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine3.23

# Project uses <FrameworkReference Include="Microsoft.AspNetCore.App" />,
# copy the shared framework from the aspnet image.
COPY --from=mcr.microsoft.com/dotnet/aspnet:10.0-alpine3.23 \
     /usr/share/dotnet/shared/Microsoft.AspNetCore.App \
     /usr/share/dotnet/shared/Microsoft.AspNetCore.App

# ---- Python + Node.js ----
RUN apk add --no-cache \
    python3 \
    py3-pip \
    nodejs \
    npm

# ---- Playwright + system Chromium (Alpine is musl, bundled browsers won't work) ----
RUN apk add --no-cache \
    chromium \
    nss \
    freetype \
    harfbuzz \
    ca-certificates \
    ttf-freefont \
    font-noto-cjk

RUN npm install -g @playwright/cli@latest

# ---- Application ----
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 5000

ENTRYPOINT ["dotnet", "sharpclaw.dll"]
