# syntax=docker/dockerfile:1
#
# Filee in a browser: the Filee.Web server (src/Filee.Web) with Filee's engines, for linux/amd64.
#
#   docker build -t filee-web .
#   docker run -p 8080:8080 -v filee-data:/data filee-web        → http://localhost:8080
#
# Build arguments (engines beyond the built-in ones; see docs/ENGINES.md):
#   ENGINES           Filee's pinned downloads to put in the image, checked against engines.json's SHA-256:
#                     any of ffmpeg pandoc calibre exiftool upscayl-bin (default: "ffmpeg pandoc"; "" for none)
#   WITH_GHOSTSCRIPT  EPS/PS ↔ PDF, PDF/A, smaller PDFs (apt, ~30 MB; default true)
#   WITH_LIBREOFFICE  DOC, XLS, PPT, RTF, OpenDocument… (apt, ~600 MB; default false)
#   WITH_TESSERACT    text recognition (OCR) in scans (apt; default false)
# rhwp (HWP/HWPX and DOCX/XLSX/PPTX → PDF) and 7-Zip (archives) are always included, as in Filee's installer.

ARG DOTNET_VERSION=10.0

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Restore first, from the project files only, so a code change doesn't download the packages again.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Filee.Core/Filee.Core.csproj src/Filee.Core/
COPY src/Filee.Engines/Filee.Engines.csproj src/Filee.Engines/
COPY src/Filee.Platform.Windows/Filee.Platform.Windows.csproj src/Filee.Platform.Windows/
COPY src/Filee.Platform.Linux/Filee.Platform.Linux.csproj src/Filee.Platform.Linux/
COPY src/Filee.Platform.MacOS/Filee.Platform.MacOS.csproj src/Filee.Platform.MacOS/
COPY src/Filee.Cli/Filee.Cli.csproj src/Filee.Cli/
COPY src/Filee.Web/Filee.Web.csproj src/Filee.Web/
RUN dotnet restore src/Filee.Web -r linux-x64 \
 && dotnet restore src/Filee.Cli -r linux-x64

COPY src/ src/
COPY build/fetch-portable-engines.ps1 build/
COPY THIRD-PARTY-NOTICES.md ./

RUN dotnet publish src/Filee.Web -c Release -r linux-x64 --self-contained false --no-restore -o /app \
 && cp THIRD-PARTY-NOTICES.md /app/

# The bundled engines, next to the program where EngineEnvironment.FindBundled looks first: /app/engines/<id>.
# (7-Zip comes as .tar.xz: tar needs xz.)
RUN apt-get update && apt-get install -y --no-install-recommends xz-utils && rm -rf /var/lib/apt/lists/*
RUN pwsh -NoProfile -File build/fetch-portable-engines.ps1 -Runtime linux-x64 -Destination /app/engines

# Optional engines: Filee's own installer (filee engines install) downloads the pinned versions and checks their
# SHA-256, into a scratch data folder; they are then moved next to the bundled ones.
ARG ENGINES="ffmpeg pandoc"
RUN if [ -n "$ENGINES" ]; then \
      dotnet publish src/Filee.Cli -c Release -r linux-x64 --self-contained false --no-restore -o /cli \
      && for engine in $ENGINES; do \
           FILEE_DATA_DIR=/tmp/filee-engines dotnet /cli/filee-cli.dll engines install "$engine" || exit 1; \
         done \
      && rm -rf /tmp/filee-engines/engines/.downloads \
      && for folder in /tmp/filee-engines/engines/*; do mv "$folder" /app/engines/; done \
      && rm -rf /tmp/filee-engines /cli; \
    fi

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime

ARG WITH_GHOSTSCRIPT=true
ARG WITH_LIBREOFFICE=false
ARG WITH_TESSERACT=false

# Fonts for documents rendered to PDF (Latin, Greek, Cyrillic, CJK…), fontconfig for ImageMagick and Skia, curl for
# the health check. Optional engines from Ubuntu are found on the PATH (docs/ENGINES.md, "looked for in this order").
RUN set -eux; \
    packages="ca-certificates curl fontconfig libfontconfig1 libfreetype6 fonts-noto-core fonts-noto-cjk fonts-dejavu-core"; \
    if [ "$WITH_GHOSTSCRIPT" = "true" ]; then packages="$packages ghostscript"; fi; \
    if [ "$WITH_LIBREOFFICE" = "true" ]; then packages="$packages libreoffice-writer-nogui libreoffice-calc-nogui libreoffice-impress-nogui libreoffice-draw-nogui"; fi; \
    if [ "$WITH_TESSERACT" = "true" ]; then packages="$packages tesseract-ocr"; fi; \
    apt-get update; \
    apt-get install -y --no-install-recommends $packages; \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

# Everything Filee writes goes to /data (one volume): settings and presets, the jobs' uploads and results
# (/data/jobs) and the work folders of running conversions (XDG_CACHE_HOME). HOME is only for programs that keep a
# profile there (LibreOffice, fontconfig); it is on /tmp so the root file system can be read-only.
# The base image already listens on port 8080 (ASPNETCORE_HTTP_PORTS).
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    FILEE_DATA_DIR=/data \
    XDG_CACHE_HOME=/data/cache \
    HOME=/tmp/home

RUN mkdir -p /data && chown app:app /data

USER app
EXPOSE 8080
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS http://127.0.0.1:8080/api/health > /dev/null || exit 1

ENTRYPOINT ["dotnet", "/app/filee-web.dll"]
