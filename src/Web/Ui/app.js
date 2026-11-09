(() => {
  'use strict';

  const api = '/api/plugin/season-posters';
  const reasons = {
    SeasonMatch: 'Matched a TMDB season',
    MovieMatch: 'Matched a TMDB movie',
    ShowPosterOnMainSeason: 'Uses the TMDB show poster',
    SharedWithEarlierPart: 'Shares a TMDB season or movie with an earlier part',
    MixedSeasons: 'Episodes span several TMDB seasons',
    MultipleMovies: 'Links to several TMDB movies',
    MovieLinkedToSeason: 'Movie linked to a TMDB season, not a TMDB movie',
    SpecialsOnly: 'Only linked to TMDB specials',
    NoTmdbLink: 'No TMDB link',
    NoTmdbPosters: 'No TMDB posters available yet',
    Pinned: 'Your choice',
    SetOutsidePlugin: 'Set outside this plugin',
  };
  const kinds = { TmdbSeason: 'TMDB season', TmdbMovie: 'TMDB movie', TmdbShow: 'TMDB show', AniDb: 'AniDB' };
  const splitPolicies = { '': 'Default', AniDbPoster: 'AniDB poster', NextTmdbPoster: 'Next TMDB poster', LeaveForManualChoice: 'Leave for me' };
  const showModes = { '': 'Default', JellyfinUpload: 'Own show poster', TmdbShowPosterOnMainSeason: 'TMDB show poster on main season', SameAsMainSeason: 'Same as main season' };

  const el = {
    summary: document.getElementById('summary'),
    shows: document.getElementById('shows'),
    flagged: document.getElementById('flagged-only'),
    search: document.getElementById('search'),
    reason: document.getElementById('reason'),
    scan: document.getElementById('scan'),
    apply: document.getElementById('apply'),
    logos: document.getElementById('logos'),
  };
  const pageSize = 25;
  let plan = null;
  let visibleCount = pageSize;

  function apiKey() {
    try {
      return JSON.parse(localStorage.getItem('apiSession') ?? '{}').apikey ?? '';
    } catch {
      return '';
    }
  }

  async function call(path, options = {}) {
    const headers = { apikey: apiKey(), ...(options.headers ?? {}) };
    if (options.json !== undefined) headers['Content-Type'] = 'application/json';
    const response = await fetch(api + path, {
      method: options.method ?? 'GET',
      headers,
      body: options.json !== undefined ? JSON.stringify(options.json) : options.body,
    });
    if (response.status === 401 || response.status === 403) {
      throw new Error('Open this page from Shoko while signed in as an administrator.');
    }
    if (!response.ok) throw new Error((await response.text()) || `Request failed (${response.status}).`);
    return response.status === 204 ? null : response.json();
  }

  function make(tag, props = {}, ...children) {
    const node = Object.assign(document.createElement(tag), props);
    node.append(...children.filter((child) => child !== null && child !== undefined));
    return node;
  }

  function image(imageId, className, remoteUrl) {
    return make('img', { src: remoteUrl ?? `/api/v3/Image/${imageId}`, alt: '', loading: 'lazy', className: className ?? '' });
  }

  function findRemoteUrl(series) {
    return series.alternatives.find((poster) => poster.imageId === series.imageId)?.remotePreviewUrl;
  }

  function describe(poster) {
    const size = poster.width && poster.height ? `${poster.width}×${poster.height}` : 'size unknown';
    const parts = [kinds[poster.kind] ?? poster.kind, size];
    if (poster.language) parts.push(poster.language);
    if (!poster.isAvailable) parts.push('downloads when chosen');
    return parts.join(' · ');
  }

  async function run(work, message) {
    const buttons = document.querySelectorAll('button, select');
    buttons.forEach((node) => { node.disabled = true; });
    el.summary.textContent = message;
    el.summary.classList.remove('error');
    try {
      await work();
    } catch (error) {
      el.summary.textContent = error.message;
      el.summary.classList.add('error');
      buttons.forEach((node) => { node.disabled = false; });
      return false;
    }
    buttons.forEach((node) => { node.disabled = false; });
    return true;
  }

  async function reload() {
    plan = await call('/plan');
    render();
  }

  function act(work, message) {
    return run(async () => {
      await work();
      await reload();
    }, message);
  }

  // The page runs in Shoko's sandboxed iframe, which blocks confirm() and alert(), so results are shown in the
  // summary line instead.
  function describeApply(result) {
    const parts = [`${result.changed} posters set`];
    if (result.failed) parts.push(`${result.failed} failed`);
    if (result.jellyfinUploaded || result.jellyfinFailed) {
      parts.push(`${result.jellyfinUploaded} show posters uploaded to Jellyfin${result.jellyfinFailed ? ` (${result.jellyfinFailed} failed)` : ''}`);
    }
    if (result.showPostersFailed) parts.push(`${result.showPostersFailed} show posters could not be downloaded`);
    return `Applied: ${parts.join(', ')}.`;
  }

  function apply(showKeys, message) {
    return run(async () => {
      const result = await call('/apply', { method: 'POST', json: showKeys ? { showKeys } : {} });
      await reload();
      el.summary.textContent = `${describeApply(result)} ${el.summary.textContent}`;
      el.summary.classList.toggle('error', result.failed > 0 || result.jellyfinFailed > 0);
    }, message);
  }

  // A second click within a few seconds confirms, since the sandbox blocks confirm().
  function confirmClick(button, prompt, onConfirm) {
    const label = button.textContent;
    let timer = null;
    button.addEventListener('click', () => {
      if (timer === null) {
        button.textContent = prompt();
        button.classList.add('confirming');
        timer = setTimeout(() => {
          timer = null;
          button.textContent = label;
          button.classList.remove('confirming');
        }, 5000);
        return;
      }
      clearTimeout(timer);
      timer = null;
      button.textContent = label;
      button.classList.remove('confirming');
      onConfirm();
    });
  }

  function select(labelText, options, value, onChange) {
    const control = make('select', { value });
    for (const [optionValue, label] of Object.entries(options)) {
      control.append(make('option', { value: optionValue, textContent: label, selected: optionValue === value }));
    }
    control.addEventListener('change', () => onChange(control.value));
    return make('label', {}, labelText, control);
  }

  /** Builds its posters only when opened. A library has thousands, and loading them all stalls the page. */
  function choicePicker(label, posters, selectedId, choose) {
    const choices = make('div', { className: 'choices' });
    const details = make('details', {}, make('summary', { textContent: label }), choices);
    details.addEventListener('toggle', () => {
      if (!details.open || choices.childElementCount > 0) return;
      for (const poster of posters) {
        const choice = make(
          'button',
          { type: 'button', className: `choice${poster.imageId === selectedId ? ' selected' : ''}`, title: describe(poster) },
          image(poster.imageId, '', poster.remotePreviewUrl),
          make('span', { textContent: describe(poster) }),
        );
        choice.addEventListener('click', () => act(() => choose(poster.imageId), 'Saving…'));
        choices.append(choice);
      }
    });
    return details;
  }

  function renderSeries(series) {
    const poster = series.imageId
      ? image(series.imageId, 'current', findRemoteUrl(series))
      : make('div', { className: 'current empty', textContent: 'No poster' });

    const badges = make('div', { className: 'badges' });
    if (series.needsReview) badges.append(make('span', { className: 'badge review', textContent: 'Needs review' }));
    if (series.isMovieType) badges.append(make('span', { className: 'badge', textContent: 'Movie' }));
    if (series.kind) badges.append(make('span', { className: 'badge', textContent: kinds[series.kind] ?? series.kind }));
    if (series.action === 'SetPreferred') badges.append(make('span', { className: 'badge', textContent: 'Change pending' }));

    const actions = make('div', { className: 'series-actions' });
    if (series.needsReview && series.imageId) {
      const keep = make('button', { type: 'button', textContent: 'Keep this poster' });
      keep.addEventListener('click', () => act(
        () => call(`/series/${series.seriesId}/pin`, { method: 'PUT', json: { imageId: series.imageId } }),
        'Saving…',
      ));
      actions.append(keep);
    }
    if (series.reason === 'Pinned' || series.reason === 'SetOutsidePlugin') {
      const auto = make('button', { type: 'button', textContent: 'Return to automatic' });
      auto.addEventListener('click', () => act(() => call(`/series/${series.seriesId}/pin`, { method: 'DELETE' }), 'Saving…'));
      actions.append(auto);
    }
    const uploadInput = make('input', { type: 'file', accept: 'image/*', hidden: true });
    uploadInput.addEventListener('change', () => {
      const file = uploadInput.files?.[0];
      if (!file) return;
      const body = new FormData();
      body.append('file', file);
      act(() => call(`/series/${series.seriesId}/upload`, { method: 'POST', body }), 'Uploading…');
    });
    const upload = make('button', { type: 'button', textContent: 'Upload your own' });
    upload.addEventListener('click', () => uploadInput.click());
    actions.append(upload, uploadInput);

    const details = choicePicker(
      `Choose another poster (${series.alternatives.length})`,
      series.alternatives,
      series.imageId,
      (imageId) => call(`/series/${series.seriesId}/pin`, { method: 'PUT', json: { imageId } }),
    );

    return make(
      'article',
      { className: 'series' },
      poster,
      make(
        'div',
        {},
        make('h3', { textContent: series.title }),
        make('p', { textContent: series.reason === 'NoTmdbPosters' ? `${series.match}. ${reasons.NoTmdbPosters}.` : `${series.match}.` }),
        badges,
        actions,
        details,
      ),
    );
  }

  function renderShowPoster(show) {
    if (show.showMode !== 'JellyfinUpload' || show.showPosterAlternatives.length === 0) return null;
    return choicePicker(
      'Choose the show poster',
      show.showPosterAlternatives,
      show.jellyfinPosterImageId,
      (imageId) => saveShow(show, { pinnedShowPosterImageId: imageId }),
    );
  }

  function saveShow(show, changes) {
    return call(`/shows/${encodeURIComponent(show.key)}`, { method: 'PUT', json: changes });
  }

  function renderShow(show) {
    const options = make(
      'div',
      { className: 'show-options' },
      select('Split seasons', splitPolicies, show.splitPolicy, (value) => act(() => saveShow(show, { splitPolicy: value }), 'Saving…')),
      select('Show poster', showModes, show.showMode, (value) => act(() => saveShow(show, { showMode: value }), 'Saving…')),
    );
    const applyButton = make('button', { type: 'button', textContent: 'Apply this show' });
    applyButton.addEventListener('click', () => apply([show.key], 'Applying…'));

    return make(
      'section',
      { className: 'show' },
      make('div', { className: 'show-head' }, make('h2', { textContent: show.title }), applyButton),
      options,
      renderShowPoster(show),
      ...show.series.map(renderSeries),
    );
  }

  function updateReasonFilter(allSeries) {
    const counts = new Map();
    for (const series of allSeries) counts.set(series.reason, (counts.get(series.reason) ?? 0) + 1);
    const selected = el.reason.value;
    el.reason.replaceChildren(
      make('option', { value: '', textContent: 'Any reason' }),
      ...[...counts].sort((a, b) => b[1] - a[1]).map(([reason, count]) => make('option', {
        value: reason,
        textContent: `${reasons[reason] ?? reason} (${count})`,
        selected: reason === selected,
      })),
    );
  }

  function render() {
    const query = el.search.value.trim().toLowerCase();
    const flaggedOnly = el.flagged.checked;
    const reason = el.reason.value;
    const matches = (show) => !query
      || show.title.toLowerCase().includes(query)
      || show.series.some((series) => series.title.toLowerCase().includes(query));
    const hasReason = (show) => !reason || show.series.some((series) => series.reason === reason);
    const shows = plan.shows.filter((show) => (!flaggedOnly || show.needsReview) && hasReason(show) && matches(show));
    const allSeries = plan.shows.flatMap((show) => show.series);
    const flagged = allSeries.filter((series) => series.needsReview).length;
    const pending = allSeries.filter((series) => series.action === 'SetPreferred').length;
    updateReasonFilter(allSeries);
    el.summary.textContent = `${plan.shows.length} shows, ${flagged} need review, ${pending} changes pending. Showing ${Math.min(shows.length, visibleCount)} of ${shows.length}.`;
    el.summary.classList.remove('error');
    el.shows.replaceChildren(...shows.slice(0, visibleCount).map(renderShow));
    if (shows.length > visibleCount) {
      const more = make('button', { type: 'button', className: 'more', textContent: `Show ${Math.min(pageSize, shows.length - visibleCount)} more` });
      more.addEventListener('click', () => {
        visibleCount += pageSize;
        render();
      });
      el.shows.append(more);
    }
  }

  const filterChanged = () => {
    visibleCount = pageSize;
    if (plan) render();
  };
  el.flagged.addEventListener('change', filterChanged);
  el.reason.addEventListener('change', filterChanged);
  el.search.addEventListener('input', filterChanged);
  el.scan.addEventListener('click', () => run(async () => {
    plan = await call('/scan', { method: 'POST' });
    render();
  }, 'Scanning…'));
  el.logos.addEventListener('click', () => run(async () => {
    const result = await call('/logos/apply', { method: 'POST' });
    el.summary.textContent = `Logos: ${result.changed} set, ${result.kept} kept, ${result.failed} failed.`;
  }, 'Applying logos…'));
  confirmClick(
    el.apply,
    () => {
      const pending = plan ? plan.shows.flatMap((show) => show.series).filter((series) => series.action === 'SetPreferred').length : 0;
      return `Click again to apply ${pending} changes`;
    },
    () => apply(null, 'Applying every pending change… this can take several minutes.'),
  );

  run(reload, 'Loading…');
})();
