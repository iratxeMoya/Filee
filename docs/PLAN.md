# Plan: Filee como web app en Docker

Objetivo: ejecutar Filee dentro de un contenedor Docker y usarlo desde el navegador (subir archivos → elegir formato → descargar el resultado).

---

## 1. Análisis del repo

### Qué es Filee hoy

Filee es una **aplicación de escritorio** en .NET 10 con interfaz Avalonia.
No tiene modo web: la experiencia principal (el "donut" que aparece en el cursor al arrastrar archivos) depende de hooks globales de ratón y teclado (SharpHook), ventanas transparentes y del gestor de archivos del sistema.
Eso **no se puede llevar tal cual a un navegador**.

### Estructura relevante

| Proyecto | Qué hace | ¿Sirve para la web? |
|---|---|---|
| `src/Filee.Core` | Modelo de dominio: formatos (`FormatRegistry`), presets, `RoutePlanner` (Dijkstra entre convertidores), `JobQueue`, `ConversionJob`, `UserDataStore`. **Sin Avalonia ni APIs de SO.** | ✅ Núcleo reutilizable |
| `src/Filee.Engines` | Todos los convertidores (`IConverter`), `EngineRegistry`, `EngineInstaller`. Librerías gestionadas + nativos Linux (Magick.NET, SkiaSharp `NativeAssets.Linux.NoDependencies`, PDFium, Unhwp). | ✅ Núcleo reutilizable |
| `src/Filee.Cli` | Comando `filee`. `CliHost.cs` monta el pipeline completo **sin UI** (settings → engines → catálogo → cola). | ✅ Modelo a copiar |
| `src/Filee.App` | App Avalonia (donut, ajustes, bandeja). Ya incluye un servidor Kestrel (`Services/Connect/LocalWebServer.cs`, "Filee Connect") pero es un puente LAN escritorio↔móvil, no un conversor web. | ❌ (solo como referencia de estilo/seguridad) |
| `src/Filee.Platform.*` | Integración con Explorer/Finder/escritorio Linux, papelera. | ❌ No necesario |
| `src/Filee.Setup`, `installer/`, `build/*.ps1` | Instaladores y empaquetado. | Parcial: `build/fetch-portable-engines.ps1` descarga rhwp y 7-Zip para `linux-x64` |

### Piezas clave encontradas

- **`CliHost.Create()`** ([src/Filee.Cli/CliHost.cs](../src/Filee.Cli/CliHost.cs)): crea `UserDataStore`, `EngineRegistry.CreateAll(...)`, `ConverterCatalog` y `JobQueue`. Es exactamente lo que necesita un backend web.
- **`Cli.RunJobAsync`** ([src/Filee.Cli/Cli.cs](../src/Filee.Cli/Cli.cs)): crea un `ConversionJob(files, preset, ...)`, se suscribe a `JobQueue.JobUpdated` para el progreso y espera `Queue.RunAsync(job)`. Los resultados quedan en `FileResult.Outputs`, `State`, `ErrorKey`.
- **`Cli.ResolvePreset` / `UseFolder`**: construyen un `Preset { TargetFormat = ... }` y fijan la carpeta de salida (`Preset.Output`). Reutilizable para "convertir a X y dejar el resultado en esta carpeta".
- **`RoutePlanner.CanConvert(from, to)`** + `FormatRegistry.Detect(path)`: permiten listar los formatos destino posibles para un archivo subido (lo que el donut muestra).
- **`FILEE_DATA_DIR`** (`UserDataStore.DirectoryVariable`): redirige settings **y** la carpeta de motores descargados (`<FILEE_DATA_DIR>/engines`). Ideal para un volumen Docker.
- **Búsqueda de motores** (`EngineEnvironment.FindBundled` + docs/ENGINES.md): 1) copia elegida por el usuario, 2) `engines/<nombre>` junto al ejecutable o en `FILEE_DATA_DIR/engines`, 3) copia del sistema en el `PATH`. → Los motores instalados con `apt` en la imagen se detectan solos.
- **Motores en Linux**:
  - Integrados (sin instalar nada): imágenes, PDF, SVG, DOCX/XLSX/PPTX → HWPX/DOCX, e-books, CAD, fuentes, subtítulos, datos, 3D, geo…
  - Empaquetados: **rhwp** (HWP/HWPX y DOCX/XLSX/PPTX → PDF) y **7-Zip** (archivos) → `fetch-portable-engines.ps1 -Runtime linux-x64`.
  - Descargables con pin SHA-256 (`engines.json`, `filee engines install`): **FFmpeg**, **Pandoc**, Upscayl.
  - Solo desde el sistema en Linux: **LibreOffice**, **Ghostscript**, **Tesseract**, calibre, ExifTool → `apt-get`.
  - Solo Windows (no disponibles): WIC (JPEG XR), WeasyPrint, Whisper, EMF/WMF vía GDI.
- **Dependencias nativas Linux** (de `.github/workflows/portable.yml`): `libicu`, `libssl3`, `libfontconfig1`, `libfreetype6`, `fonts-noto-cjk`… Las de X11 no hacen falta sin UI.
- **Unhwp** no trae nativo para `linux-arm64` (hay que compilarlo con `build/build-native.py`). → Empezar solo con **`linux/amd64`**.

---

## 2. Enfoque elegido

**Nuevo proyecto `src/Filee.Web`** (ASP.NET Core minimal API + frontend estático) que reutiliza `Filee.Core` y `Filee.Engines` igual que el CLI, empaquetado en una imagen Docker Linux.

```
 Navegador ──HTTP──► Filee.Web (Kestrel :8080)
                       ├─ wwwroot/ (HTML+JS: zona de soltar, chips de formato, progreso, descarga)
                       ├─ API /api/*  ──► WebConversionHost (≈ CliHost)
                       │                    └─ JobQueue ─► RoutePlanner ─► IConverter (Filee.Engines)
                       └─ /data (volumen): settings, motores descargados, jobs/<id>/{in,out}
```

Principios:
- **Cambios aditivos**: carpeta nueva + entrada en `Filee.slnx`. No tocar `Filee.App`/`Filee.Cli` para poder seguir haciendo merge de upstream sin conflictos.
- Un único host de conversión (singleton) compartido por todas las peticiones; la `JobQueue` ya limita concurrencia por motor.
- Nada de descargas por URL en la versión web (evita SSRF): solo archivos subidos.

> Alternativa descartada como solución final (ver Anexo A): meter la app de escritorio en el contenedor y verla por noVNC. Rápido de prototipar, pero la UX del donut (gestos globales de arrastre) no funciona bien por VNC.

---

## 3. Plan paso a paso

### Fase 0 — Preparación (½ día)

- [ ] Instalar .NET 10 SDK (`global.json` pide `10.0.100`, `rollForward: latestFeature`) y Docker Desktop (backend WSL2).
- [ ] `dotnet build Filee.slnx` y `dotnet test tests/Filee.Core.Tests tests/Filee.Engines.Tests` en local para confirmar que el núcleo compila.
- [ ] Probar el CLI como "prueba de concepto" del pipeline sin UI:
      `dotnet run --project src/Filee.Cli -- convert foto.png --to jpg`
- [ ] Crear rama `feat/web-docker`.

### Fase 1 — Proyecto `Filee.Web` (backend) (2–3 días)

1. **Crear el proyecto**
   - `src/Filee.Web/Filee.Web.csproj` con `Sdk="Microsoft.NET.Sdk.Web"`, `OutputType=Exe`, `AssemblyName=filee-web`.
   - `ProjectReference` a `Filee.Core` y `Filee.Engines` (no a `Filee.Platform.*` ni a `Filee.App`).
   - Incrustar las traducciones en inglés como hace el CLI:
     `<EmbeddedResource Include="..\Filee.App\Assets\i18n\en.json" LogicalName="Filee.Web.en.json" />`
   - Añadirlo a `Filee.slnx` en la carpeta `/src/`.
   - `Directory.Build.props` ya aporta `net10.0`, nullable, etc. Ojo: `GenerateDocumentationFile=true` → documentar miembros públicos o mantenerlos `internal`.

2. **`WebConversionHost`** (copia adaptada de `CliHost`)
   - Copiar la lógica de `CliHost.Create()` (es `internal`, no se puede referenciar): `UserDataStore` sobre `FILEE_DATA_DIR`, `EngineEnvironment.*` desde settings, `EngineRegistry.CreateAll`, `ConverterCatalog`, `JobQueue` con `FindPdfMerger`, `FindFileCombiner`, `FindTiffMerger`, `FindMediaJoiner`. `moveToTrash: null` (no hay papelera en el servidor).
   - Copiar `EnglishTexts` (un `ILocalizer` que lee el `en.json` incrustado) para traducir `ErrorKey` y nombres de presets.
   - Registrar como **singleton** en DI; `DisposeAsync` al parar la app (`IHostApplicationLifetime`).

3. **Gestor de trabajos (`JobStore`)**
   - Cada job: `/data/jobs/<guid>/in/` (subidas) y `/data/jobs/<guid>/out/` (resultados).
   - Guardar las subidas con nombre saneado (`Path.GetFileName`, sin caracteres de control, longitud máx.) — la extensión es lo que usa `FormatRegistry.Detect`.
   - Construir el preset como `Cli.ResolvePreset` (`new Preset { TargetFormat = format.Id }`) y fijar la salida en `out/` como `Cli.UseFolder`.
   - Lanzar `Queue.RunAsync(job)` en segundo plano; guardar el `ConversionJob` en un `ConcurrentDictionary<Guid, ...>`; escuchar `JobUpdated` para el progreso.
   - `BackgroundService` de limpieza: borrar jobs con más de N minutos (`FILEE_WEB_JOB_TTL_MINUTES`, por defecto 60).

4. **API HTTP**

   | Método y ruta | Descripción |
   |---|---|
   | `GET /api/health` | 200 OK (para el healthcheck de Docker) |
   | `GET /api/formats?ext=heic` | Formatos destino posibles: recorrer `FormatRegistry.Known` y filtrar con `catalog.CreatePlanner().CanConvert(from, to)` |
   | `GET /api/engines` | Estado de motores (`ConverterCatalog.StatusOf`) para mostrar qué falta (p. ej. "Necesita LibreOffice") |
   | `POST /api/jobs` | `multipart/form-data`: `files[]`, `to` (id de formato), opcional `quality`. Devuelve `{ id }` |
   | `GET /api/jobs/{id}` | Estado: por archivo `State`, salidas, error traducido, progreso |
   | `GET /api/jobs/{id}/events` | (opcional) Server-Sent Events con el progreso en vivo |
   | `GET /api/jobs/{id}/files/{name}` | Descarga de un resultado (`Results.File`, comprobando que la ruta resuelta está dentro de `out/`) |
   | `GET /api/jobs/{id}/zip` | Todos los resultados en un ZIP en streaming (`ZipArchive` sobre `Response.Body`, como hace Filee Connect) |
   | `DELETE /api/jobs/{id}` | Cancelar (`job.Cancel()`) y borrar |

5. **Configuración por variables de entorno**
   - `ASPNETCORE_URLS=http://+:8080`
   - `FILEE_DATA_DIR=/data`
   - `FILEE_WEB_MAX_UPLOAD_MB` (p. ej. 500) → `KestrelServerOptions.Limits.MaxRequestBodySize` + `FormOptions.MultipartBodyLengthLimit`
   - `FILEE_WEB_MAX_CONCURRENT_JOBS` (p. ej. 2) → `SemaphoreSlim` delante de `RunAsync`
   - `FILEE_WEB_TOKEN` (opcional) → si está definido, exigir `Authorization: Bearer` o cookie; si no, la app solo debe exponerse en LAN/localhost.

### Fase 2 — Frontend web (1–2 días)

- [ ] `src/Filee.Web/wwwroot/index.html` + `app.js` + `app.css` (vanilla JS, sin build de npm para no complicar el Dockerfile).
- [ ] Flujo:
  1. Zona de soltar (drag & drop + botón "Elegir archivos").
  2. Al añadir archivos → `GET /api/formats?ext=...` (intersección si hay varios tipos) → chips de formato, agrupados como el donut (imágenes, documentos, PDF, vídeo…). Formatos que requieren un motor ausente, deshabilitados con el motivo.
  3. Clic en un formato → `POST /api/jobs` con barra de progreso de subida (`XMLHttpRequest.upload.onprogress`).
  4. Progreso de conversión por SSE o polling cada 1 s.
  5. Lista de resultados con enlace de descarga individual + "Descargar todo (ZIP)"; errores traducidos.
- [ ] Reutilizar la estética de Filee: paleta/logo de `src/Filee.App/Resources/Connect/app.css` y `docs/media`, tema claro/oscuro con `prefers-color-scheme`.
- [ ] `app.UseDefaultFiles(); app.UseStaticFiles();` en `Program.cs`.

### Fase 3 — Dockerización (1–2 días)

1. **`.dockerignore`** en la raíz: `**/bin`, `**/obj`, `.git`, `engines/`, `build/.cache`, `docs/media`, `tests/**/TestResults`.

2. **`Dockerfile`** (multi-stage, en la raíz del repo):

   ```dockerfile
   # ---------- build ----------
   FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
   WORKDIR /src
   COPY . .
   RUN dotnet publish src/Filee.Web -c Release -r linux-x64 --self-contained false -o /app
   # El CLI, solo para instalar motores con versión fijada (FFmpeg, Pandoc)
   RUN dotnet publish src/Filee.Cli -c Release -r linux-x64 --self-contained false -o /cli
   # rhwp + 7-Zip (motores empaquetados) junto al ejecutable: EngineEnvironment.FindBundled los encuentra en ./engines
   RUN pwsh build/fetch-portable-engines.ps1 -Runtime linux-x64 -Destination /app/engines

   # ---------- runtime ----------
   FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
   ARG WITH_LIBREOFFICE=true
   ARG WITH_GHOSTSCRIPT=true
   ARG WITH_TESSERACT=false
   RUN apt-get update && apt-get install -y --no-install-recommends \
         ca-certificates libfontconfig1 libfreetype6 fonts-noto-core fonts-noto-cjk \
       && if [ "$WITH_LIBREOFFICE" = "true" ]; then apt-get install -y --no-install-recommends \
            libreoffice-writer libreoffice-calc libreoffice-impress libreoffice-draw default-jre-headless; fi \
       && if [ "$WITH_GHOSTSCRIPT" = "true" ]; then apt-get install -y --no-install-recommends ghostscript; fi \
       && if [ "$WITH_TESSERACT" = "true" ]; then apt-get install -y --no-install-recommends tesseract-ocr; fi \
       && rm -rf /var/lib/apt/lists/*
   WORKDIR /app
   COPY --from=build /app .
   COPY --from=build /cli /opt/filee-cli
   ENV ASPNETCORE_URLS=http://+:8080 \
       FILEE_DATA_DIR=/data \
       DOTNET_RUNNING_IN_CONTAINER=true
   RUN mkdir -p /data && chown -R app:app /data
   USER app
   EXPOSE 8080
   VOLUME /data
   ENTRYPOINT ["dotnet", "filee-web.dll"]
   ```

   Notas:
   - La imagen `dotnet/sdk` ya incluye `pwsh`, así que el script de motores funciona sin instalar nada.
   - FFmpeg/Pandoc: dos opciones —
     a) en el `ENTRYPOINT`/primer arranque: `dotnet /opt/filee-cli/filee-cli.dll engines install ffmpeg pandoc` (descarga las versiones fijadas con SHA-256 en `engines.json` a `/data/engines`, persiste en el volumen); o
     b) `apt-get install ffmpeg pandoc` (más simple, pero con versiones distintas a las que prueba Filee).
     → Recomendado **a)** con un script `docker/entrypoint.sh` que instala solo si falta.
   - LibreOffice/Ghostscript/Tesseract no tienen descarga Linux en Filee: se detectan en el `PATH` (paso 3 de la búsqueda de motores).
   - El usuario `app` (UID 1654) existe en las imágenes de .NET 8+. Su `HOME` (`/home/app`) es donde `WorkFolders.Root` pone `~/.cache/filee` en Linux: verificar que existe y es escribible.
   - Tamaño aproximado: ~400 MB base, ~1,2 GB con LibreOffice. Por eso los `ARG WITH_*`.

3. **`docker-compose.yml`**:

   ```yaml
   services:
     filee:
       build: .
       image: filee-web:local
       ports:
         - "8080:8080"
       volumes:
         - filee-data:/data
       environment:
         FILEE_WEB_MAX_UPLOAD_MB: "500"
         FILEE_WEB_MAX_CONCURRENT_JOBS: "2"
         # FILEE_WEB_TOKEN: "cambia-esto"
       mem_limit: 4g
       read_only: true
       tmpfs:
         - /tmp
       healthcheck:
         test: ["CMD", "wget", "-qO-", "http://localhost:8080/api/health"]
         interval: 30s
       restart: unless-stopped
   volumes:
     filee-data:
   ```

   (Con `read_only: true` hay que montar también `/home/app/.cache` como `tmpfs` o volumen. La imagen `aspnet` no trae `wget`/`curl`: instalar uno o hacer el healthcheck con un pequeño `--health` en la propia app.)

4. **Arrancar y probar**:
   `docker compose up --build` → abrir **http://localhost:8080**.

### Fase 4 — Seguridad y robustez (1 día)

El contenedor procesa archivos arbitrarios con motores complejos (LibreOffice, Ghostscript, ImageMagick, FFmpeg). Tratarlo como servicio de riesgo:

- [ ] Usuario no root, `read_only`, `cap_drop: [ALL]`, `security_opt: [no-new-privileges:true]`, límites de memoria/CPU.
- [ ] Límite de tamaño de subida y número de archivos por job; tiempo máximo por job (cancelar con `job.Cancel()`).
- [ ] Rutas: nunca construir rutas con texto del cliente sin `Path.GetFileName` + comprobación de que la ruta final está bajo `/data/jobs/<id>/`.
- [ ] Ids de job = GUID aleatorio (no adivinables); los jobs no se listan.
- [ ] Sin funciones de descarga por URL (no reutilizar `WebDownloader`).
- [ ] `FILEE_WEB_TOKEN` si se publica fuera de localhost; para Internet, poner delante un proxy inverso (Caddy/Traefik) con HTTPS y autenticación.
- [ ] Cabeceras: `Content-Disposition: attachment` en descargas, `X-Content-Type-Options: nosniff`, CSP básica.
- [ ] Licencias: Filee es MIT; FFmpeg (GPL) y **Ghostscript (AGPL)** dentro de una imagen que se ofrece como servicio de red implican ofrecer su código fuente si se distribuye/expone públicamente. Revisar `THIRD-PARTY-NOTICES.md` y añadir un enlace "Licencias" en la web.

### Fase 5 — Tests y verificación (1 día)

- [ ] `tests/Filee.Web.Tests` con `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory`), añadido a `Filee.slnx`:
  - `GET /api/health` → 200
  - `GET /api/formats?ext=png` contiene `jpg`, `webp`, `pdf`
  - `POST /api/jobs` con un PNG pequeño → `to=jpg` → estado `Done` → la descarga empieza por `FF D8 FF`
  - Subida por encima del límite → 413
  - `GET /api/jobs/{id}/files/..%2F..%2Fetc%2Fpasswd` → 404/400
- [ ] Prueba manual en el contenedor con un set de muestra:

  | Conversión | Motor | Esperado |
  |---|---|---|
  | HEIC → JPG, PNG → WEBP | Magick (integrado) | ✅ |
  | Varias imágenes → un PDF | PDFsharp | ✅ |
  | PDF → PNG / DOCX / TXT | PDFium, DOCX writer, PdfPig | ✅ |
  | DOCX / XLSX → PDF | HWPX writer + rhwp | ✅ (revisar fuentes) |
  | HWP → PDF | rhwp | ✅ |
  | ZIP → 7Z | 7-Zip | ✅ |
  | MP4 → MP3 | FFmpeg | ✅ si está instalado |
  | DOC / PPT → PDF | LibreOffice | ✅ si `WITH_LIBREOFFICE=true` |
  | EPS → PDF | Ghostscript | ✅ si `WITH_GHOSTSCRIPT=true` |
  | JXR, EMF→PNG | WIC/GDI (solo Windows) | ❌ esperado, debe salir deshabilitado |

- [ ] Verificar fuentes: los PDF generados por rhwp con texto CJK/latino extendido se ven bien (si no, revisar dónde busca fuentes rhwp y montar `fonts-noto-*` o las fuentes de `build/fetch-fonts.ps1`).

### Fase 6 — CI y documentación (½ día)

- [ ] `.github/workflows/docker.yml`: `docker build` en cada PR (y opcionalmente push a GHCR en tags), con `docker/build-push-action`.
- [ ] `docs/DOCKER.md`: cómo construir, variables de entorno, build args, volumen, cómo instalar motores extra, limitaciones respecto a la app de escritorio.
- [ ] Sección breve en el README enlazando a `docs/DOCKER.md`.

---

## 4. Archivos nuevos / modificados

```
Filee.slnx                         (modificado: + Filee.Web, + Filee.Web.Tests)
Dockerfile                         (nuevo)
.dockerignore                      (nuevo)
docker-compose.yml                 (nuevo)
docker/entrypoint.sh               (nuevo: instala FFmpeg/Pandoc la 1ª vez y arranca filee-web)
src/Filee.Web/Filee.Web.csproj     (nuevo)
src/Filee.Web/Program.cs           (nuevo: DI, límites, endpoints, static files)
src/Filee.Web/WebConversionHost.cs (nuevo: adaptado de CliHost)
src/Filee.Web/EnglishTexts.cs      (nuevo: adaptado del CLI)
src/Filee.Web/JobStore.cs          (nuevo: jobs, carpetas, progreso)
src/Filee.Web/JobCleanupService.cs (nuevo: BackgroundService de limpieza)
src/Filee.Web/wwwroot/*            (nuevo: index.html, app.js, app.css, logo)
tests/Filee.Web.Tests/*            (nuevo)
.github/workflows/docker.yml       (nuevo)
docs/DOCKER.md                     (nuevo)
```

## 5. Riesgos y puntos a verificar

| Riesgo | Mitigación |
|---|---|
| Alguna clase de `Filee.Core`/`Engines` usada por el CLI es `internal` | Copiar el código mínimo (como `CliHost`/`EnglishTexts`) o, si es imprescindible, añadir `InternalsVisibleTo Include="filee-web"` (cambio de una línea) |
| Motores que asumen escritorio (p. ej. rutas de `%TEMP%`, `HOME`) | Ejecutar como `app` con `HOME` escribible; revisar `WorkFolders.Root` en Linux |
| Imagen muy grande con LibreOffice | `ARG WITH_LIBREOFFICE`; variantes `:slim` y `:full` |
| `linux-arm64` (Raspberry Pi, Mac M-series nativo) | Fase posterior: compilar Unhwp con `build/build-native.py` y pasar `-p:UnhwpNativeLibrary=...`; mientras, en Mac usar emulación amd64 |
| Conversiones largas (vídeo) bloquean recursos | Semáforo de jobs concurrentes, timeout, límites de CPU/memoria en compose |
| Divergencia con upstream | Todo aditivo; rebasar periódicamente desde `KnifeLemon/Filee` |

## 6. Estimación

| Fase | Esfuerzo |
|---|---|
| 0. Preparación | ½ día |
| 1. Backend `Filee.Web` | 2–3 días |
| 2. Frontend | 1–2 días |
| 3. Docker | 1–2 días |
| 4. Seguridad | 1 día |
| 5. Tests y verificación | 1 día |
| 6. CI y docs | ½ día |
| **Total** | **≈ 7–10 días** |

Un **MVP** (Fases 0, 1 con `POST /api/jobs` + descarga, frontend mínimo y Dockerfile sin LibreOffice) cabe en 2–3 días.

---

## Anexo A — Alternativa rápida: la app de escritorio por noVNC

Si se quiere ver Filee "tal cual" en el navegador sin escribir código:

1. Imagen basada en `ubuntu:24.04` con las dependencias de `portable.yml` (`libx11-6`, `libxrandr2`, `libxkbcommon0`, `libxi6`, `libxtst6`, `fonts-noto-cjk`…), más `xvfb`, un gestor de ventanas ligero (`openbox`), `x11vnc` y `novnc`/`websockify`.
2. `dotnet publish src/Filee.App -r linux-x64` y arrancar con `DISPLAY=:0` y `FILEE_RENDERING=software`.
3. Exponer el puerto 6080 de noVNC → `http://localhost:6080`.

Limitaciones: no se pueden arrastrar archivos del PC al contenedor (habría que montar una carpeta compartida y usar la zona de soltar o *Watch folders*), los gestos globales con el donut son torpes por VNC y el rendimiento es peor. Útil como demo, no como producto. Una variante intermedia sin UI: usar el comando **`filee watch /data/inbox --to pdf`** en un contenedor con una carpeta compartida (SMB/volumen) en vez de navegador.
