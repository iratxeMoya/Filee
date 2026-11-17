# Filee in a browser (Docker)

`Filee.Web` runs Filee's conversions on a server and gives them a web page: drop files, pick a format, download the
results. It uses the same engines and conversion routes as the app and the `filee` command, packed in one Docker image.

It isn't the desktop app: there is no donut at the cursor, no watch folders and no preset editor. Files go to the
server, are converted there and are deleted after a while.

## Quick start

```bash
git clone https://github.com/KnifeLemon/Filee.git
cd Filee
docker compose up -d --build
```

Open **http://localhost:27115**.
The first build takes a few minutes (it downloads the .NET SDK and the engines); later builds reuse the cache.

Without Compose:

```bash
docker build -t filee-web .
docker run -d -p 127.0.0.1:27115:8080 -v filee-data:/data --read-only --tmpfs /tmp filee-web
```

The image is `linux/amd64` only for now (Unhwp has no `linux-arm64` library in its package). On Apple silicon,
Docker Desktop runs it under emulation.

## What's in the image

| Engine | How | Converts |
|---|---|---|
| Built-in engines (ImageMagick, PDFium, PDFsharp, DOCX/HWPX writers, …) | always | images, PDF, Office documents, e-books, CAD, fonts, subtitles, data, 3D, geo |
| rhwp, 7-Zip | always (bundled, as in Filee's installer) | HWP/HWPX and DOCX/XLSX/PPTX → PDF, archives |
| FFmpeg, Pandoc | `ENGINES` build argument (default `ffmpeg pandoc`) | video and audio; Markdown and markup ↔ DOCX/ODT/RTF/… |
| Ghostscript | `WITH_GHOSTSCRIPT` (default `true`) | EPS/PS ↔ PDF, PDF/A, smaller PDFs |
| LibreOffice | `WITH_LIBREOFFICE` (default `false`, ~600 MB) | DOC, XLS, PPT, RTF, OpenDocument |
| Tesseract | `WITH_TESSERACT` (default `false`) | text recognition (OCR) |

`ENGINES` takes any of `ffmpeg pandoc calibre exiftool upscayl-bin`: Filee's own installer downloads the versions
pinned in [engines.json](../src/Filee.Engines/Infrastructure/engines.json) and checks their SHA-256 while the image is
built, so the container needs no internet. Ghostscript, LibreOffice and Tesseract come from Ubuntu and are found on
the `PATH`. The Windows-only engines (Windows Imaging Component, WeasyPrint, Whisper) aren't available.

Set the arguments in `docker-compose.yml` (`build.args`) or with `docker build --build-arg`:

```bash
docker build --build-arg ENGINES="ffmpeg pandoc calibre" --build-arg WITH_LIBREOFFICE=true -t filee-web .
```

The page's **Engines** link lists what the running image has. Formats that need a missing engine appear under
"More with optional engines" with the engine they need.

The default image is about 1.3 GB (FFmpeg, Pandoc and the CJK fonts are most of it).

## Settings

Environment variables, read at start:

| Variable | Default | |
|---|---|---|
| `FILEE_WEB_MAX_UPLOAD_MB` | `500` | Largest upload, all files of one conversion together (413 above it) |
| `FILEE_WEB_MAX_FILES` | `50` | Most files in one conversion |
| `FILEE_WEB_MAX_CONCURRENT_JOBS` | `2` | Conversions running at the same time; the others wait |
| `FILEE_WEB_JOB_TTL_MINUTES` | `60` | How long results are kept after a conversion ends |
| `FILEE_WEB_JOB_TIMEOUT_MINUTES` | `30` | A conversion running longer is cancelled |
| `FILEE_WEB_TOKEN` | empty | Access token; when set, every `/api` request needs it (see below) |
| `FILEE_WEB_JOBS_DIR` | `/data/jobs` | Where uploads and results are kept |
| `FILEE_DATA_DIR` | `/data` | Filee's data folder (settings, presets), as for the app and the CLI |

With Compose, `FILEE_PORT` changes the host port (default `27115`; the container listens on 8080).

## Data

Everything the server writes goes to the `/data` volume:

- `/data/jobs/<id>/in` and `out`: each conversion's uploads and results, deleted when it expires, when the page's
  *Remove* is used, and at every start.
- `/data/cache`: the work folders of running conversions.
- `/data/settings.json`, `presets.json`, …: Filee's settings, made with their defaults at the first start. Engine
  priority and the LibreOffice choices in `settings.json` apply as in the app.

The root file system can be read-only (`read_only: true` in Compose); `/tmp` needs a `tmpfs`.

## Security

The server converts whatever is uploaded with complex programs (ImageMagick, Ghostscript, FFmpeg, LibreOffice). Keep
it for people you trust:

- Compose publishes it on `127.0.0.1` only. To reach it from the local network, change the port to
  `"27115:8080"` **and** set `FILEE_WEB_TOKEN`. The page asks for the token once and keeps it in the browser.
  Scripts send it as `Authorization: Bearer <token>`. `/api/health` stays open for health checks.
- For anything beyond a local network, put a reverse proxy with HTTPS and authentication in front, e.g. Caddy:

  ```
  filee.example.com {
      basic_auth { alice <bcrypt hash from `caddy hash-password`> }
      reverse_proxy filee:8080
  }
  ```

  Raise the proxy's request size limit to `FILEE_WEB_MAX_UPLOAD_MB`.
- The container runs as the non-root `app` user. The Compose file also drops every capability, sets
  `no-new-privileges` and limits memory (4 GB), CPUs (2) and processes.
- The server only converts uploads: it never downloads links. Results are served only from the conversion's own folder,
  as downloads (`Content-Disposition: attachment`), with a strict Content Security Policy on the page.

**Licenses.** Filee is MIT. The image also contains FFmpeg (GPL-3.0), Ghostscript (AGPL-3.0), Pandoc (GPL-2.0+) and
the other programs listed in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) (in the image at
`/app/THIRD-PARTY-NOTICES.md`). If you offer the server to other people, their licenses apply to that, and AGPL-3.0
asks you to offer Ghostscript's source to its users. Build without Ghostscript (`WITH_GHOSTSCRIPT=false`) if that is a
concern.

## API

The page uses a small JSON API, which scripts can use too:

| Request | |
|---|---|
| `GET /api/health` | `{status, version, maxFiles, maxUploadMb}` |
| `GET /api/formats` | every format: `id`, `name`, `category`, `extensions`, `writable` |
| `GET /api/targets?ext=heic&ext=png` | what all these convert to now (`targets`) and with optional engines (`withEngines`) |
| `GET /api/engines` | each engine, whether it is available, its version or why not |
| `POST /api/jobs` | multipart form: `files` (one or more), `to` (format id or extension), optional `quality` (1–100); `202` with the job |
| `GET /api/jobs/{id}` | state, progress, and per file its results, error or notice |
| `GET /api/jobs/{id}/results/{n}` | result `n` (a folder comes as a ZIP) |
| `GET /api/jobs/{id}/zip` | every result in one ZIP |
| `POST /api/jobs/{id}/cancel` | cancel it |
| `DELETE /api/jobs/{id}` | cancel it and delete its files |

```bash
id=$(curl -s -F files=@photo.heic -F to=jpg http://localhost:27115/api/jobs | jq -r .id)
curl -s http://localhost:27115/api/jobs/$id | jq .state          # until "completed"
curl -OJ http://localhost:27115/api/jobs/$id/results/0
```

Messages from the server (errors, notices) are in English; the page itself follows the browser's language (English
and Spanish for now).

## Development

Without Docker, with the .NET 10 SDK:

```bash
dotnet run --project src/Filee.Web          # http://localhost:5000; engines from the repository's engines/ folder
dotnet test tests/Filee.Web.Tests
```

`pwsh build/fetch-engines.ps1 -Only rhwp,7zip` (Windows) or `pwsh build/fetch-portable-engines.ps1 -Runtime linux-x64
-Destination engines` fills `engines/` for the bundled engines; other engines are found on the `PATH`.

The code: [src/Filee.Web](../src/Filee.Web) (`WebConversionHost` sets up the pipeline like the CLI's `CliHost`,
`JobStore` keeps the conversions, `Api/FileeApi.cs` is the API, `wwwroot` the page).
[Filee.Web.slnf](../Filee.Web.slnf) holds only the projects behind the web app and their tests
(`dotnet build Filee.Web.slnf`), without the desktop app.

CI: [ci.yml](../.github/workflows/ci.yml) builds `Filee.Web.slnf` on Linux, checks the formatting and runs its tests
(with rhwp, 7-Zip and FFmpeg for the engine tests). [docker.yml](../.github/workflows/docker.yml) builds the image,
starts it and converts a few files through the API on every change that can affect it. Publishing to `ghcr.io/<owner>/filee-web` is opt-in: set the
repository variable `FILEE_WEB_PUBLISH` to `true`; pushes to `main` publish `edge` and `web-v1.2.3` tags publish `1.2.3`.
