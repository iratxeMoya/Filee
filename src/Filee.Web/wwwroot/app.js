// Filee in the browser: pick files, see what they convert to (/api/targets), start a job (/api/jobs), follow its
// progress and download the results. No framework and no inline code (the server's CSP allows only this file).
'use strict';

(() => {
  // ---------- Texts ----------
  const TEXTS = {
    en: {
      tagline: 'Drag. Drop. Converted.',
      engines: 'Engines',
      drop: 'Drop files here',
      choose: 'or click to choose them',
      limit: 'Up to {files} files, {mb} MB together',
      clear: 'Clear',
      files: n => (n === 1 ? '1 file' : `${n} files`),
      remove: 'Remove',
      unknown: n => (n === 1 ? '1 file is in a format Filee doesn\'t know and will be skipped.' : `${n} files are in formats Filee doesn't know and will be skipped.`),
      convertTo: 'Convert to',
      filter: 'Filter formats',
      loading: 'Looking for formats…',
      noFormats: 'These files have no format in common to convert to.',
      withEngines: 'More with optional engines',
      needs: 'Needs {engines}',
      quality: 'Quality',
      pick: 'Pick a format',
      convert: 'Convert to {format}',
      uploading: 'Uploading… {pct}%',
      queued: 'Waiting for a free slot…',
      running: 'Converting… {pct}%',
      completed: 'Done',
      completedWithErrors: 'Done, with errors',
      failed: 'Failed',
      cancelled: 'Cancelled',
      download: 'Download',
      downloadAll: 'Download all (ZIP)',
      cancel: 'Cancel',
      close: 'Remove',
      tooMany: 'At most {files} files at once.',
      tooLarge: 'The files are larger than {mb} MB together.',
      network: 'The server could not be reached.',
      privacy: 'Files are converted on this server and deleted after a while.',
      dropAnywhere: 'Drop to add files',
      tokenTitle: 'Access token',
      tokenText: 'This server needs an access token.',
      save: 'Save',
      missingFonts: 'Missing fonts: {fonts}',
      available: 'Ready',
      categories: {
        Image: 'Images', Pdf: 'PDF', Document: 'Documents', Spreadsheet: 'Spreadsheets', Presentation: 'Presentations',
        Hwp: 'HWP', Text: 'Text', Video: 'Video', Audio: 'Audio', Ebook: 'E-books', Archive: 'Archives', Vector: 'Vector',
        Cad: 'CAD', Font: 'Fonts', Subtitle: 'Subtitles', Data: 'Data', Model3D: '3D models', Geo: 'Geospatial',
      },
    },
    es: {
      tagline: 'Arrastra. Suelta. Convertido.',
      engines: 'Motores',
      drop: 'Suelta los archivos aquí',
      choose: 'o haz clic para elegirlos',
      limit: 'Hasta {files} archivos, {mb} MB en total',
      clear: 'Vaciar',
      files: n => (n === 1 ? '1 archivo' : `${n} archivos`),
      remove: 'Quitar',
      unknown: n => (n === 1 ? '1 archivo tiene un formato que Filee no conoce y se omitirá.' : `${n} archivos tienen formatos que Filee no conoce y se omitirán.`),
      convertTo: 'Convertir a',
      filter: 'Filtrar formatos',
      loading: 'Buscando formatos…',
      noFormats: 'Estos archivos no tienen ningún formato de destino en común.',
      withEngines: 'Más con motores opcionales',
      needs: 'Necesita {engines}',
      quality: 'Calidad',
      pick: 'Elige un formato',
      convert: 'Convertir a {format}',
      uploading: 'Subiendo… {pct}%',
      queued: 'Esperando turno…',
      running: 'Convirtiendo… {pct}%',
      completed: 'Hecho',
      completedWithErrors: 'Hecho, con errores',
      failed: 'Error',
      cancelled: 'Cancelado',
      download: 'Descargar',
      downloadAll: 'Descargar todo (ZIP)',
      cancel: 'Cancelar',
      close: 'Quitar',
      tooMany: 'Como máximo {files} archivos a la vez.',
      tooLarge: 'Los archivos ocupan más de {mb} MB en total.',
      network: 'No se pudo contactar con el servidor.',
      privacy: 'Los archivos se convierten en este servidor y se borran pasado un tiempo.',
      dropAnywhere: 'Suelta para añadir archivos',
      tokenTitle: 'Token de acceso',
      tokenText: 'Este servidor necesita un token de acceso.',
      save: 'Guardar',
      missingFonts: 'Faltan fuentes: {fonts}',
      available: 'Listo',
      categories: {
        Image: 'Imágenes', Pdf: 'PDF', Document: 'Documentos', Spreadsheet: 'Hojas de cálculo', Presentation: 'Presentaciones',
        Hwp: 'HWP', Text: 'Texto', Video: 'Vídeo', Audio: 'Audio', Ebook: 'Libros electrónicos', Archive: 'Archivos comprimidos',
        Vector: 'Vectorial', Cad: 'CAD', Font: 'Fuentes', Subtitle: 'Subtítulos', Data: 'Datos', Model3D: 'Modelos 3D', Geo: 'Geoespacial',
      },
    },
  };
  const lang = (navigator.languages || [navigator.language || 'en']).map(l => l.slice(0, 2)).find(l => TEXTS[l]) || 'en';
  const T = TEXTS[lang];
  const t = (key, values = {}) => String(T[key] ?? TEXTS.en[key] ?? key).replace(/\{(\w+)\}/g, (_, k) => values[k] ?? '');

  const CATEGORIES = ['Image', 'Pdf', 'Document', 'Spreadsheet', 'Presentation', 'Hwp', 'Text', 'Video', 'Audio', 'Ebook',
    'Archive', 'Vector', 'Cad', 'Font', 'Subtitle', 'Data', 'Model3D', 'Geo'];
  const LOSSY = new Set(['jpg', 'webp', 'avif', 'heic', 'jxl', 'jp2']);
  const JOBS_KEY = 'filee.jobs';
  const TOKEN_KEY = 'filee.token';

  // ---------- Elements ----------
  const $ = id => document.getElementById(id);
  const el = (tag, props = {}, ...children) => {
    const node = document.createElement(tag);
    for (const [key, value] of Object.entries(props)) {
      if (value === undefined || value === null || value === false) continue;
      if (key === 'class') node.className = value;
      else if (key === 'text') node.textContent = value;
      else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
      else node.setAttribute(key, value === true ? '' : value);
    }
    for (const child of children.flat()) if (child) node.append(child);
    return node;
  };

  document.documentElement.lang = lang;
  for (const node of document.querySelectorAll('[data-i18n]')) node.textContent = t(node.dataset.i18n);
  for (const node of document.querySelectorAll('[data-i18n-placeholder]')) node.placeholder = t(node.dataset.i18nPlaceholder);

  // ---------- State ----------
  const state = {
    files: [],
    targets: null,
    selected: null,
    limits: { maxFiles: 50, maxUploadMb: 500 },
    jobs: new Map(),
    targetsRequest: 0,
  };

  // ---------- API ----------
  let token = read(TOKEN_KEY);
  let tokenPrompt = null;

  function read(key) { try { return localStorage.getItem(key); } catch { return null; } }
  function write(key, value) { try { value === null ? localStorage.removeItem(key) : localStorage.setItem(key, value); } catch { /* private mode */ } }

  function useToken(value) {
    token = value;
    write(TOKEN_KEY, value);
    // Download links can't send a header: the server also reads the token from this cookie.
    document.cookie = `filee_web_token=${encodeURIComponent(value)}; path=/; SameSite=Strict`;
  }
  if (token) useToken(token);

  function askToken() {
    if (tokenPrompt) return tokenPrompt;
    const dialog = $('token-dialog');
    tokenPrompt = new Promise(resolve => {
      dialog.addEventListener('close', () => {
        const value = $('token-input').value.trim();
        if (value) useToken(value);
        tokenPrompt = null;
        resolve(Boolean(value));
      }, { once: true });
      dialog.showModal();
    });
    return tokenPrompt;
  }

  async function api(path, options = {}, retry = true) {
    const headers = new Headers(options.headers || {});
    if (token) headers.set('Authorization', `Bearer ${token}`);
    const response = await fetch(path, { ...options, headers });
    if (response.status === 401 && retry && await askToken()) return api(path, options, false);
    return response;
  }

  async function json(path, options) {
    const response = await api(path, options);
    if (!response.ok) throw new Error((await response.json().catch(() => null))?.error || `${response.status}`);
    return response.status === 204 ? null : response.json();
  }

  // ---------- Selected files ----------
  function extensionOf(name) {
    const parts = name.toLowerCase().split('.');
    if (parts.length < 2) return '';
    if (parts.length > 2 && parts[parts.length - 2] === 'tar') return `tar.${parts[parts.length - 1]}`;
    return parts[parts.length - 1];
  }

  function formatSize(bytes) {
    if (bytes == null) return '';
    const units = ['B', 'KB', 'MB', 'GB'];
    let value = bytes;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
    return `${value.toFixed(value < 10 && unit > 0 ? 1 : 0)} ${units[unit]}`;
  }

  function addFiles(list) {
    const known = new Set(state.files.map(f => `${f.name}|${f.size}|${f.lastModified}`));
    for (const file of list) {
      const key = `${file.name}|${file.size}|${file.lastModified}`;
      if (file.size === 0 || known.has(key)) continue;
      known.add(key);
      state.files.push(file);
    }
    if (state.files.length > state.limits.maxFiles) {
      state.files.length = state.limits.maxFiles;
      alert(t('tooMany', { files: state.limits.maxFiles }));
    }
    renderSelection();
    refreshTargets();
  }

  function renderSelection() {
    const has = state.files.length > 0;
    $('selection').classList.toggle('hidden', !has);
    $('formats-card').classList.toggle('hidden', !has);
    $('selection-count').textContent = T.files(state.files.length);
    const unknown = new Set(state.targets?.unknown || []);
    $('file-list').replaceChildren(...state.files.map((file, index) => {
      const ext = extensionOf(file.name);
      return el('li', {},
        el('span', { class: `badge${unknown.has(ext) ? ' unknown' : ''}`, text: ext || '?' }),
        el('span', { class: 'name', text: file.name, title: file.name }),
        el('span', { class: 'size', text: formatSize(file.size) }),
        el('button', {
          type: 'button', class: 'icon-button', 'aria-label': `${t('remove')} ${file.name}`, text: '×',
          onclick: () => { state.files.splice(index, 1); renderSelection(); refreshTargets(); },
        }));
    }));
    const unknownCount = state.files.filter(f => unknown.has(extensionOf(f.name)) || !extensionOf(f.name)).length;
    $('unknown-note').classList.toggle('hidden', unknownCount === 0);
    $('unknown-note').textContent = unknownCount ? T.unknown(unknownCount) : '';
  }

  // ---------- Formats ----------
  async function refreshTargets() {
    const request = ++state.targetsRequest;
    const extensions = [...new Set(state.files.map(f => extensionOf(f.name)).filter(Boolean))];
    if (extensions.length === 0) {
      state.targets = state.files.length ? { sources: [], unknown: [], targets: [], withEngines: [] } : null;
      renderFormats();
      return;
    }
    $('formats-loading').classList.remove('hidden');
    $('format-groups').replaceChildren();
    try {
      const query = extensions.map(e => `ext=${encodeURIComponent(e)}`).join('&');
      const targets = await json(`/api/targets?${query}`);
      if (request !== state.targetsRequest) return;
      state.targets = targets;
    } catch (error) {
      if (request !== state.targetsRequest) return;
      state.targets = { sources: [], unknown: [], targets: [], withEngines: [], error: error.message };
    }
    $('formats-loading').classList.add('hidden');
    renderSelection();
    renderFormats();
  }

  function groups(targets, filter, makeChip) {
    const text = filter.trim().toLowerCase();
    const visible = targets.filter(f => !text || f.name.toLowerCase().includes(text) || f.id.includes(text));
    return CATEGORIES
      .map(category => [category, visible.filter(f => f.category === category)])
      .filter(([, list]) => list.length > 0)
      .map(([category, list]) => el('div', { class: 'group' },
        el('h3', { text: T.categories[category] || category }),
        el('div', { class: 'chips' }, list.map(makeChip))));
  }

  function renderFormats() {
    const targets = state.targets;
    const ready = targets?.targets || [];
    if (state.selected && !ready.some(f => f.id === state.selected)) state.selected = null;
    const filter = $('format-filter').value;

    $('format-groups').replaceChildren(...groups(ready, filter, format => el('button', {
      type: 'button',
      class: 'chip',
      'aria-pressed': String(format.id === state.selected),
      text: format.name,
      onclick: () => { state.selected = format.id === state.selected ? null : format.id; renderFormats(); },
    })));

    const later = targets?.withEngines || [];
    $('more-formats').classList.toggle('hidden', later.length === 0);
    $('engine-groups').replaceChildren(...groups(later, filter, format => el('button', {
      type: 'button', class: 'chip', disabled: true, text: format.name,
      title: t('needs', { engines: format.needsEngines.join(', ') }),
    })));

    const loaded = Boolean(targets) && $('formats-loading').classList.contains('hidden');
    $('no-formats').classList.toggle('hidden', !(loaded && state.files.length && ready.length === 0));
    if (targets?.error) $('no-formats').textContent = targets.error;
    else $('no-formats').textContent = t('noFormats');

    const chosen = ready.find(f => f.id === state.selected);
    $('quality-row').classList.toggle('hidden', !chosen || !LOSSY.has(chosen.id));
    const button = $('convert');
    button.disabled = !chosen;
    button.textContent = chosen ? t('convert', { format: chosen.name }) : t('pick');
  }

  // ---------- Jobs ----------
  function savedJobs() {
    try { return JSON.parse(read(JOBS_KEY) || '[]'); } catch { return []; }
  }

  function remember(id) { write(JOBS_KEY, JSON.stringify([id, ...savedJobs().filter(j => j !== id)].slice(0, 20))); }
  function forget(id) { write(JOBS_KEY, JSON.stringify(savedJobs().filter(j => j !== id))); }

  function startJob() {
    const chosen = state.targets?.targets.find(f => f.id === state.selected);
    if (!chosen) return;
    const known = new Set(state.targets.unknown);
    const files = state.files.filter(f => extensionOf(f.name) && !known.has(extensionOf(f.name)));
    const total = files.reduce((sum, f) => sum + f.size, 0);
    if (total > state.limits.maxUploadMb * 1024 * 1024) {
      alert(t('tooLarge', { mb: state.limits.maxUploadMb }));
      return;
    }

    const form = new FormData();
    for (const file of files) form.append('files', file, file.name);
    form.append('to', chosen.id);
    if (!$('quality-row').classList.contains('hidden')) form.append('quality', $('quality').value);

    const card = createCard();
    card.setUploading(0, chosen.name);
    $('jobs').prepend(card.root);

    state.files = [];
    state.selected = null;
    state.targets = null;
    renderSelection();
    renderFormats();

    upload(form, card, true);
  }

  function upload(form, card, retry) {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', '/api/jobs');
    if (token) xhr.setRequestHeader('Authorization', `Bearer ${token}`);
    xhr.responseType = 'json';
    xhr.upload.onprogress = e => { if (e.lengthComputable) card.setUploading(Math.round((e.loaded / e.total) * 100)); };
    xhr.onerror = () => card.setError(t('network'));
    xhr.onload = async () => {
      if (xhr.status === 401 && retry && await askToken()) { upload(form, card, false); return; }
      if (xhr.status !== 202) { card.setError(xhr.response?.error || `HTTP ${xhr.status}`); return; }
      track(xhr.response, card);
    };
    xhr.send(form);
  }

  function track(job, card = createCard()) {
    if (!card.root.isConnected) $('jobs').prepend(card.root);
    state.jobs.set(job.id, card);
    remember(job.id);
    card.update(job);
    poll(job.id);
  }

  async function poll(id) {
    const card = state.jobs.get(id);
    if (!card) return;
    try {
      const response = await api(`/api/jobs/${id}`);
      if (response.status === 404) { card.root.remove(); state.jobs.delete(id); forget(id); return; }
      if (!response.ok) throw new Error(`${response.status}`);
      const job = await response.json();
      if (!state.jobs.has(id)) return;
      card.update(job);
      if (!job.finished) setTimeout(() => poll(id), 700);
    } catch {
      setTimeout(() => poll(id), 3000);
    }
  }

  function createCard() {
    const title = el('strong');
    const status = el('span', { class: 'job-state' });
    const buttons = el('div', { class: 'job-buttons' });
    const bar = el('div', { class: 'bar' }, el('span'));
    const list = el('ul', { class: 'results' });
    const root = el('article', { class: 'card job' },
      el('div', { class: 'job-head' }, el('div', { class: 'job-title' }, title, status), buttons), bar, list);
    let id = null;

    const setBar = (fraction, done, indeterminate) => {
      bar.classList.toggle('done', Boolean(done));
      bar.classList.toggle('indeterminate', Boolean(indeterminate));
      bar.firstChild.style.width = `${Math.round(Math.max(0, Math.min(1, fraction)) * 100)}%`;
    };

    return {
      root,
      setUploading(pct, name) {
        if (name) title.textContent = `→ ${name}`;
        status.className = 'job-state';
        status.textContent = t('uploading', { pct });
        setBar(pct / 100 * 0.5);
      },
      setError(message) {
        status.className = 'job-state bad';
        status.textContent = message;
        setBar(0);
        buttons.replaceChildren(el('button', { type: 'button', class: 'secondary', text: t('close'), onclick: () => root.remove() }));
      },
      update(job) {
        id = job.id;
        title.textContent = `→ ${job.targetName}`;
        const pct = Math.round(job.progress * 100);
        status.className = `job-state${job.state === 'completed' ? ' ok' : ['failed', 'cancelled', 'completedWithErrors'].includes(job.state) ? ' bad' : ''}`;
        status.textContent = job.finished ? t(job.state) : job.state === 'queued' ? t('queued') : t('running', { pct });
        setBar(job.finished ? 1 : 0.5 + job.progress * 0.5, job.finished && job.state === 'completed', !job.finished && job.state === 'queued');

        const actions = [];
        if (job.finished && job.resultCount > 1) {
          actions.push(el('a', { class: 'secondary', href: `/api/jobs/${id}/zip`, download: '', text: t('downloadAll') }));
        }
        if (!job.finished) {
          actions.push(el('button', { type: 'button', class: 'secondary', text: t('cancel'), onclick: () => json(`/api/jobs/${id}/cancel`, { method: 'POST' }).catch(() => {}) }));
        } else {
          actions.push(el('button', {
            type: 'button', class: 'secondary', text: t('close'),
            onclick: () => { json(`/api/jobs/${id}`, { method: 'DELETE' }).catch(() => {}); state.jobs.delete(id); forget(id); root.remove(); },
          }));
        }
        buttons.replaceChildren(...actions);

        list.replaceChildren(...job.files.map(file => {
          const icon = { done: '✓', failed: '✕', cancelled: '–', skipped: '–', running: '…', pending: '·' }[file.state] || '·';
          const item = el('li', {},
            el('div', { class: 'result-line' },
              el('span', { class: `status ${file.state}`, text: icon, 'aria-hidden': 'true' }),
              el('span', { class: 'name', text: file.name, title: file.name })));
          if (file.results.length) {
            item.append(el('div', { class: 'downloads' }, file.results.map(result => el('a', {
              class: 'secondary', href: `/api/jobs/${id}/results/${result.index}`, download: result.name,
              text: `${t('download')} ${result.name}${result.size != null ? ` · ${formatSize(result.size)}` : ''}`,
            }))));
          }
          if (file.error) {
            const needs = file.needsEngines?.length ? ` ${t('needs', { engines: file.needsEngines.join(', ') })}.` : '';
            item.append(el('p', { class: 'message error', text: file.error + needs }));
          }
          if (file.notice) item.append(el('p', { class: 'message', text: file.notice }));
          if (file.missingFonts?.length) item.append(el('p', { class: 'message', text: t('missingFonts', { fonts: file.missingFonts.join(', ') }) }));
          return item;
        }));
      },
    };
  }

  // ---------- Engines ----------
  $('engines-toggle').addEventListener('click', async () => {
    const card = $('engines-card');
    card.classList.toggle('hidden');
    if (card.classList.contains('hidden') || card.dataset.loaded) return;
    try {
      const engines = await json('/api/engines');
      card.dataset.loaded = '1';
      engines.sort((a, b) => Number(b.available) - Number(a.available));
      $('engines-list').replaceChildren(...engines.map(engine => el('li', {},
        el('span', { class: `dot${engine.available ? ' on' : ''}`, 'aria-hidden': 'true' }),
        el('span', {},
          el('span', { text: engine.name }),
          el('small', { text: ` ${engine.available ? (engine.version || t('available')) : (engine.reason || '')}` })))));
    } catch (error) {
      $('engines-list').replaceChildren(el('li', { text: error.message }));
    }
  });

  // ---------- Events ----------
  const input = $('file-input');
  input.addEventListener('change', () => { addFiles(input.files); input.value = ''; });
  $('clear').addEventListener('click', () => { state.files = []; state.selected = null; state.targets = null; renderSelection(); renderFormats(); });
  $('format-filter').addEventListener('input', renderFormats);
  $('quality').addEventListener('input', () => { $('quality-value').textContent = $('quality').value; });
  $('convert').addEventListener('click', startJob);
  $('token-form').addEventListener('submit', () => { /* method=dialog closes it */ });

  // Dropping anywhere on the page adds the files.
  let dragDepth = 0;
  const hasFiles = e => [...(e.dataTransfer?.types || [])].includes('Files');
  window.addEventListener('dragenter', e => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    if (++dragDepth === 1) { $('drag-overlay').classList.remove('hidden'); $('dropzone').classList.add('active'); }
  });
  window.addEventListener('dragover', e => { if (hasFiles(e)) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; } });
  window.addEventListener('dragleave', e => {
    if (!hasFiles(e)) return;
    if (--dragDepth <= 0) { dragDepth = 0; $('drag-overlay').classList.add('hidden'); $('dropzone').classList.remove('active'); }
  });
  window.addEventListener('drop', e => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    dragDepth = 0;
    $('drag-overlay').classList.add('hidden');
    $('dropzone').classList.remove('active');
    addFiles(e.dataTransfer.files);
  });

  // ---------- Start ----------
  (async () => {
    try {
      const health = await (await fetch('/api/health')).json();
      if (health.maxFiles) state.limits = { maxFiles: health.maxFiles, maxUploadMb: health.maxUploadMb };
    } catch { /* keep the defaults */ }
    $('limit-note').textContent = t('limit', { files: state.limits.maxFiles, mb: state.limits.maxUploadMb });
    renderFormats();
    for (const id of savedJobs().reverse()) track({ id, state: 'queued', finished: false, progress: 0, targetName: '…', files: [], resultCount: 0 });
  })();
})();
