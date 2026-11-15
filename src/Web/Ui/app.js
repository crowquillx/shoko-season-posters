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
    LooksLikeAnotherPoster: 'Every TMDB poster looks the same as one used elsewhere in the show',
    Pinned: 'Your choice',
    SetOutsidePlugin: 'Set outside this plugin',
  };
  // Filters that are not decision reasons, listed after them in the reason menu.
  const extraFilters = {
    'lookalike': 'Looks like another poster in its show',
    'show-review': 'Show poster needs review',
  };
  const kinds = { TmdbSeason: 'TMDB season', TmdbMovie: 'TMDB movie', TmdbShow: 'TMDB show', AniDb: 'AniDB', Other: 'Other' };
  const splitPolicies = { '': 'Default', AniDbPoster: 'AniDB poster', NextTmdbPoster: 'Next TMDB poster', LeaveForManualChoice: 'Leave for me' };
  const showModes = { '': 'Default', JellyfinUpload: 'Own show poster', TmdbShowPosterOnMainSeason: 'TMDB show poster on main season', SameAsMainSeason: 'Same as main season' };
  const showTexts = { '': 'Default', FollowLanguages: 'Follow languages', TextlessFirst: 'Textless first' };

  const el = {
    summary: document.getElementById('summary'),
    shows: document.getElementById('shows'),
    flagged: document.getElementById('flagged-only'),
    search: document.getElementById('search'),
    reason: document.getElementById('reason'),
    scan: document.getElementById('scan'),
    apply: document.getElementById('apply'),
    logos: document.getElementById('logos'),
    settingsToggle: document.getElementById('settings-toggle'),
    settings: document.getElementById('settings'),
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
    node.append(...children.filter((child) => child !== null && child !== undefined && child !== false));
    return node;
  }

  function imageUrl(imageId, remoteUrl, large) {
    if (!remoteUrl) return `/api/v3/Image/${imageId}`;
    return large ? remoteUrl.replace('/w342/', '/w780/') : remoteUrl;
  }

  function image(imageId, className, remoteUrl) {
    return make('img', { src: imageUrl(imageId, remoteUrl), alt: '', loading: 'lazy', className: className ?? '' });
  }

  function findPoster(posters, imageId) {
    return posters.find((poster) => poster.imageId === imageId);
  }

  function isTextless(poster) {
    return !poster.language || ['none', 'xx', 'zxx'].includes(poster.language.toLowerCase());
  }

  function languageLabel(poster) {
    return isTextless(poster) ? 'textless' : poster.language;
  }

  function describe(poster) {
    const size = poster.width && poster.height ? `${poster.width}×${poster.height}` : 'size unknown';
    const parts = [kinds[poster.kind] ?? poster.kind, size, languageLabel(poster)];
    if (!poster.isAvailable) parts.push('downloads when chosen');
    return parts.join(' · ');
  }

  function lookalikeText(item) {
    return item.likeness === 'SameImage' ? `Looks the same as ${item.usedBy}` : `Same artwork as ${item.usedBy}`;
  }

  function lookalikeBadges(items) {
    return (items ?? []).map((item) => make('span', {
      className: `badge ${item.likeness === 'SameImage' ? 'review' : 'similar'}`,
      textContent: lookalikeText(item),
    }));
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

  function uploadButton(path) {
    const input = make('input', { type: 'file', accept: 'image/*', hidden: true });
    input.addEventListener('change', () => {
      const file = input.files?.[0];
      if (!file) return;
      const body = new FormData();
      body.append('file', file);
      act(() => call(path, { method: 'POST', body }), 'Uploading…');
    });
    const button = make('button', { type: 'button', textContent: 'Upload your own' });
    button.addEventListener('click', () => input.click());
    return [button, input];
  }

  // A full-size look at a poster before choosing it. A plain overlay rather than <dialog>, since Shoko's sandboxed
  // frame may block modal dialogs.
  const viewer = (() => {
    const picture = make('img', { alt: '' });
    const title = make('h3');
    const details = make('p');
    const notes = make('div', { className: 'badges' });
    const use = make('button', { type: 'button', className: 'primary', textContent: 'Use this poster' });
    const previous = make('button', { type: 'button', textContent: '‹ Previous', title: 'Left arrow' });
    const next = make('button', { type: 'button', textContent: 'Next ›', title: 'Right arrow' });
    const close = make('button', { type: 'button', textContent: 'Close', title: 'Escape' });
    const panel = make(
      'div',
      { className: 'viewer-panel', role: 'dialog', ariaModal: 'true', ariaLabel: 'Poster preview' },
      picture,
      make('div', { className: 'viewer-info' }, title, details, notes, make('div', { className: 'series-actions' }, use, previous, next, close)),
    );
    const overlay = make('div', { className: 'viewer', hidden: true }, panel);
    document.body.append(overlay);

    let list = [];
    let index = 0;
    let options = {};

    function show() {
      const poster = list[index];
      picture.src = imageUrl(poster.imageId, poster.remotePreviewUrl, true);
      title.textContent = options.title ?? 'Poster';
      details.textContent = [describe(poster), poster.rating ? `rated ${poster.rating.toFixed(1)}` : null].filter(Boolean).join(' · ');
      const inUse = poster.imageId === options.selectedId;
      notes.replaceChildren(
        ...(inUse ? [make('span', { className: 'badge', textContent: 'In use' })] : []),
        ...lookalikeBadges(options.lookalikes?.(poster.imageId)),
      );
      use.hidden = !options.choose || inUse;
      previous.hidden = next.hidden = list.length < 2;
    }

    function hide() {
      overlay.hidden = true;
      options.returnFocus?.focus();
    }

    function step(delta) {
      index = (index + delta + list.length) % list.length;
      show();
    }

    use.addEventListener('click', () => {
      const poster = list[index];
      const choose = options.choose;
      hide();
      act(() => choose(poster.imageId), 'Saving…');
    });
    previous.addEventListener('click', () => step(-1));
    next.addEventListener('click', () => step(1));
    close.addEventListener('click', hide);
    overlay.addEventListener('click', (event) => { if (event.target === overlay) hide(); });
    document.addEventListener('keydown', (event) => {
      if (overlay.hidden) return;
      if (event.key === 'Escape') hide();
      else if (event.key === 'ArrowLeft') step(-1);
      else if (event.key === 'ArrowRight') step(1);
    });

    return {
      open(posters, start, opts) {
        list = posters;
        index = Math.max(0, start);
        options = { ...opts, returnFocus: document.activeElement };
        overlay.hidden = false;
        show();
        (use.hidden ? close : use).focus();
      },
    };
  })();

  /**
   * A poster chooser with filters and a preview. Builds its posters only when opened, since a library has
   * thousands, and asks the server which posters in the show each one looks like.
   */
  function chooser({ label, posters, selectedId, showKey, seriesId, title, choose }) {
    const panel = make('div', { className: 'chooser', hidden: true });
    const toggle = make('button', { type: 'button', textContent: `${label} (${posters.length})`, ariaExpanded: 'false' });
    let built = false;
    toggle.addEventListener('click', () => {
      panel.hidden = !panel.hidden;
      toggle.ariaExpanded = String(!panel.hidden);
      if (!panel.hidden && !built) {
        built = true;
        build();
      }
    });

    function build() {
      const filters = { language: '', kind: '' };
      let lookalikes = null;
      const status = make('p', { className: 'chooser-status', textContent: 'Checking which posters look like ones already in this show…' });
      const grid = make('div', { className: 'choices' });

      const languages = [...new Set(posters.map(languageLabel))].sort((a, b) => (a === 'textless' ? -1 : b === 'textless' ? 1 : a.localeCompare(b)));
      const sources = [...new Set(posters.map((poster) => poster.kind))];
      const chips = (name, values, labels) => {
        const row = make('div', { className: 'chips', role: 'group', ariaLabel: name });
        const buttons = [['', 'All'], ...values.map((value) => [value, labels?.[value] ?? value])].map(([value, text]) => {
          const count = value === '' ? posters.length : posters.filter((poster) => (name === 'Language' ? languageLabel(poster) : poster.kind) === value).length;
          const chip = make('button', { type: 'button', className: 'chip', textContent: `${text} ${count}`, ariaPressed: String(value === '') });
          chip.addEventListener('click', () => {
            filters[name === 'Language' ? 'language' : 'kind'] = value;
            buttons.forEach((other) => { other.ariaPressed = String(other === chip); });
            draw();
          });
          return chip;
        });
        row.append(...buttons);
        return row;
      };

      const visible = () => posters.filter((poster) => (!filters.language || languageLabel(poster) === filters.language)
        && (!filters.kind || poster.kind === filters.kind));
      const lookalikesOf = (imageId) => lookalikes?.[imageId] ?? [];

      function draw() {
        const shown = visible();
        grid.replaceChildren(...shown.map((poster, position) => {
          const similar = lookalikesOf(poster.imageId);
          const tile = make(
            'button',
            {
              type: 'button',
              className: `choice${poster.imageId === selectedId ? ' selected' : ''}${similar.some((item) => item.likeness === 'SameImage') ? ' same' : similar.length ? ' similar' : ''}`,
              title: [describe(poster), ...similar.map(lookalikeText)].join('\n'),
            },
            image(poster.imageId, '', poster.remotePreviewUrl),
            make('span', { textContent: describe(poster) }),
            poster.imageId === selectedId ? make('span', { className: 'tag', textContent: 'In use' }) : null,
            similar.length ? make('span', { className: 'tag warn', textContent: similar.map(lookalikeText).join('; ') }) : null,
          );
          tile.addEventListener('click', () => viewer.open(shown, position, { title, selectedId, choose, lookalikes: lookalikesOf }));
          return tile;
        }));
        if (shown.length === 0) grid.append(make('p', { textContent: 'No posters match these filters.' }));
      }

      panel.append(
        languages.length > 1 ? chips('Language', languages) : null,
        sources.length > 1 ? chips('Source', sources, kinds) : null,
        status,
        grid,
      );
      draw();

      call('/lookalikes', { method: 'POST', json: { showKey, seriesId, imageIds: posters.map((poster) => poster.imageId) } })
        .then((result) => {
          lookalikes = result.images;
          const count = Object.values(lookalikes).filter((items) => items.length > 0).length;
          status.textContent = count ? `${count} of these look like a poster already in this show.` : 'None of these look like a poster already in this show.';
          draw();
        })
        .catch((error) => {
          status.textContent = `Could not check for lookalikes: ${error.message}`;
        });
    }

    return { toggle, panel };
  }

  function previewCard(imageId, remoteUrl, label, note, { big = false, lookalike = false, posters = null } = {}) {
    const picture = imageId
      ? image(imageId, 'preview-image', remoteUrl)
      : make('div', { className: 'preview-image empty', textContent: 'No poster' });
    const card = make(
      imageId ? 'button' : 'div',
      { className: `preview-card${big ? ' big' : ''}${lookalike ? ' lookalike' : ''}`, title: note ? `${label}\n${note}` : label },
      picture,
      make('span', { className: 'preview-label', textContent: label }),
    );
    if (imageId) {
      card.type = 'button';
      card.addEventListener('click', () => {
        const known = posters && findPoster(posters, imageId);
        viewer.open([known ?? { imageId, kind: 'Other', remotePreviewUrl: remoteUrl, isAvailable: !remoteUrl }], 0, { title: label });
      });
    }
    return card;
  }

  /** What a client will show: the show poster beside each season's. */
  function renderPreview(show) {
    const showRemote = findPoster(show.showPosterAlternatives, show.showPosterImageId)?.remotePreviewUrl;
    return make(
      'div',
      { className: 'preview' },
      previewCard(show.showPosterImageId, showRemote, 'Show', show.showPosterSource, {
        big: true,
        lookalike: show.showPosterLooksLike.length > 0,
        posters: show.showPosterAlternatives,
      }),
      make('div', { className: 'preview-seasons' }, ...show.series.map((series) => previewCard(
        series.imageId,
        findPoster(series.alternatives, series.imageId)?.remotePreviewUrl,
        series.title,
        series.match,
        { lookalike: series.looksLike.length > 0, posters: series.alternatives },
      ))),
    );
  }

  function renderShowPoster(show) {
    if (show.showMode !== 'JellyfinUpload') {
      return make('p', { className: 'show-poster-note', textContent: `${show.showPosterSource ?? 'The main season\'s poster'} is the show poster. Choose "Own show poster" to give the show a poster of its own.` });
    }

    const badges = make('div', { className: 'badges' });
    if (show.showPosterNeedsReview) badges.append(make('span', { className: 'badge review', textContent: 'Needs review' }));
    if (show.showPosterPinned) badges.append(make('span', { className: 'badge', textContent: 'Your choice' }));
    if (show.showPosterPending) badges.append(make('span', { className: 'badge', textContent: 'Change pending' }));
    badges.append(...lookalikeBadges(show.showPosterLooksLike));

    const actions = make('div', { className: 'series-actions' });
    const picker = show.showPosterAlternatives.length > 0
      ? chooser({
        label: 'Choose the show poster',
        posters: show.showPosterAlternatives,
        selectedId: show.showPosterImageId,
        showKey: show.key,
        seriesId: null,
        title: `Show poster for ${show.title}`,
        choose: (imageId) => call(`/shows/${encodeURIComponent(show.key)}/poster`, { method: 'PUT', json: { imageId } }),
      })
      : null;
    if (picker) actions.append(picker.toggle);
    actions.append(...uploadButton(`/shows/${encodeURIComponent(show.key)}/poster/upload`));
    if (show.showPosterPinned) {
      const auto = make('button', { type: 'button', textContent: 'Return to automatic' });
      auto.addEventListener('click', () => act(() => call(`/shows/${encodeURIComponent(show.key)}/poster`, { method: 'DELETE' }), 'Saving…'));
      actions.append(auto);
    }

    return make(
      'div',
      { className: 'show-poster' },
      make('h3', { textContent: 'Show poster' }),
      make('p', { textContent: show.showPosterImageId ? `${show.showPosterSource}. Applied with the show.` : 'No poster of its own yet. Choose or upload one.' }),
      badges,
      actions,
      picker?.panel,
    );
  }

  function renderSeries(show, series) {
    const poster = series.imageId
      ? image(series.imageId, 'current', findPoster(series.alternatives, series.imageId)?.remotePreviewUrl)
      : make('div', { className: 'current empty', textContent: 'No poster' });

    const badges = make('div', { className: 'badges' });
    if (series.needsReview) badges.append(make('span', { className: 'badge review', textContent: 'Needs review' }));
    if (series.isMovieType) badges.append(make('span', { className: 'badge', textContent: 'Movie' }));
    if (series.kind) badges.append(make('span', { className: 'badge', textContent: kinds[series.kind] ?? series.kind }));
    if (series.action === 'SetPreferred') badges.append(make('span', { className: 'badge', textContent: 'Change pending' }));
    badges.append(...lookalikeBadges(series.looksLike));

    const actions = make('div', { className: 'series-actions' });
    const picker = chooser({
      label: 'Choose another poster',
      posters: series.alternatives,
      selectedId: series.imageId,
      showKey: show.key,
      seriesId: series.seriesId,
      title: series.title,
      choose: (imageId) => call(`/series/${series.seriesId}/pin`, { method: 'PUT', json: { imageId } }),
    });
    actions.append(picker.toggle);
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
    actions.append(...uploadButton(`/series/${series.seriesId}/upload`));

    const reasonText = series.reason === 'NoTmdbPosters' || series.reason === 'LooksLikeAnotherPoster'
      ? `${series.match}. ${reasons[series.reason]}.`
      : `${series.match}.`;
    return make(
      'article',
      { className: 'series' },
      poster,
      make('div', {}, make('h3', { textContent: series.title }), make('p', { textContent: reasonText }), badges, actions, picker.panel),
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
      show.showMode === 'JellyfinUpload'
        ? select('Show poster text', showTexts, show.showText, (value) => act(() => saveShow(show, { showText: value }), 'Saving…'))
        : null,
    );
    const applyButton = make('button', { type: 'button', textContent: 'Apply this show' });
    applyButton.addEventListener('click', () => apply([show.key], 'Applying…'));

    return make(
      'section',
      { className: 'show' },
      make('div', { className: 'show-head' }, make('h2', { textContent: show.title }), applyButton),
      options,
      renderPreview(show),
      renderShowPoster(show),
      ...show.series.map((series) => renderSeries(show, series)),
    );
  }

  function updateReasonFilter(allSeries) {
    const counts = new Map();
    for (const series of allSeries) counts.set(series.reason, (counts.get(series.reason) ?? 0) + 1);
    const lookalikeShows = plan.shows.filter(hasLookalike).length;
    const showReviews = plan.shows.filter((show) => show.showPosterNeedsReview).length;
    const selected = el.reason.value;
    el.reason.replaceChildren(
      make('option', { value: '', textContent: 'Any reason' }),
      ...[...counts].sort((a, b) => b[1] - a[1]).map(([reason, count]) => make('option', {
        value: reason,
        textContent: `${reasons[reason] ?? reason} (${count})`,
        selected: reason === selected,
      })),
      lookalikeShows ? make('option', { value: 'lookalike', textContent: `${extraFilters.lookalike} (${lookalikeShows} shows)`, selected: selected === 'lookalike' }) : null,
      showReviews ? make('option', { value: 'show-review', textContent: `${extraFilters['show-review']} (${showReviews})`, selected: selected === 'show-review' }) : null,
    );
  }

  function hasLookalike(show) {
    return show.showPosterLooksLike.length > 0 || show.series.some((series) => series.looksLike.length > 0);
  }

  function render() {
    const query = el.search.value.trim().toLowerCase();
    const flaggedOnly = el.flagged.checked;
    const reason = el.reason.value;
    const matches = (show) => !query
      || show.title.toLowerCase().includes(query)
      || show.series.some((series) => series.title.toLowerCase().includes(query));
    const hasReason = (show) => {
      if (!reason) return true;
      if (reason === 'lookalike') return hasLookalike(show);
      if (reason === 'show-review') return show.showPosterNeedsReview;
      return show.series.some((series) => series.reason === reason);
    };
    const shows = plan.shows.filter((show) => (!flaggedOnly || show.needsReview) && hasReason(show) && matches(show));
    const allSeries = plan.shows.flatMap((show) => show.series);
    const flagged = plan.shows.filter((show) => show.showPosterNeedsReview).length + allSeries.filter((series) => series.needsReview).length;
    const pending = allSeries.filter((series) => series.action === 'SetPreferred').length + plan.shows.filter((show) => show.showPosterPending).length;
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

  const settingFields = [
    {
      key: 'showPosterText', label: 'Show poster text', options: { FollowLanguages: 'Follow languages', TextlessFirst: 'Textless first' },
      help: 'Textless first gives each show a textless poster whenever one exists, while its seasons keep their titles. Textless artwork unlike every season\'s comes first. Only applies with Own show poster. Each show can override this.',
    },
    {
      key: 'lookalikes', label: 'Avoid lookalike posters', options: { AvoidSameArtwork: 'Avoid same artwork', AvoidSameImageOnly: 'Avoid same image only', Off: 'Off' },
      help: 'TMDB often holds one artwork in each language, textless or cropped differently. Avoid same artwork keeps those apart within a show when it can.',
    },
    { key: 'showPoster', label: 'Show poster', options: Object.fromEntries(Object.entries(showModes).filter(([value]) => value)), help: 'How the show poster relates to its main season. Each show can override this.' },
    { key: 'splitSeasons', label: 'Split seasons', options: Object.fromEntries(Object.entries(splitPolicies).filter(([value]) => value)), help: 'What later parts use when they share a TMDB season or movie with an earlier part. Each show can override this.' },
    { key: 'posterLanguages', label: 'Poster languages', type: 'text', help: 'Comma-separated, best first. "none" means textless or unknown.' },
    { key: 'showPosterLanguages', label: 'Show poster languages', type: 'text', help: 'For the show\'s own poster. Empty uses the poster languages.' },
    { key: 'logoLanguages', label: 'Logo languages', type: 'text', help: 'Comma-separated, best first. Empty leaves logos alone.' },
    { key: 'minimumSeasonSharePercent', label: 'Season match threshold (%)', type: 'number', min: 51, max: 100, help: 'The share of an entry\'s episodes that must point at one TMDB season.' },
    { key: 'autoApply', label: 'Apply automatically', type: 'checkbox', help: 'Off: scans only plan. On: scans and detected changes apply their plan.' },
    { key: 'jellyfinUrl', label: 'Jellyfin URL', type: 'text', help: 'Only needed to upload own show posters to Jellyfin, for example http://jellyfin:8096.' },
    { key: 'jellyfinApiKey', label: 'Jellyfin API key', type: 'password', help: '' },
  ];

  async function openSettings() {
    const settings = await call('/settings');
    const inputs = {};
    const rows = settingFields.map((field) => {
      let input;
      if (field.options) {
        input = make('select', {});
        for (const [value, text] of Object.entries(field.options)) input.append(make('option', { value, textContent: text, selected: value === settings[field.key] }));
      } else if (field.type === 'checkbox') {
        input = make('input', { type: 'checkbox', checked: Boolean(settings[field.key]) });
      } else if (field.type === 'password') {
        input = make('input', { type: 'password', autocomplete: 'off', placeholder: settings.jellyfinApiKeySet ? 'Stored. Type a new key to replace it.' : 'Not set' });
      } else {
        input = make('input', { type: field.type, value: settings[field.key] ?? '', min: field.min ?? '', max: field.max ?? '' });
      }
      input.id = `setting-${field.key}`;
      inputs[field.key] = input;
      const help = field.key === 'jellyfinApiKey'
        ? (settings.jellyfinApiKeySet ? 'A key is stored. Leave this empty to keep it.' : 'Only needed to upload own show posters to Jellyfin.')
        : field.help;
      return make(
        'div',
        { className: `setting${field.type === 'checkbox' ? ' check' : ''}` },
        make('label', { htmlFor: input.id, textContent: field.label }),
        input,
        help ? make('p', { textContent: help }) : null,
      );
    });

    const clearKey = make('input', { type: 'checkbox', id: 'setting-clear-key' });
    const save = make('button', { type: 'button', className: 'primary', textContent: 'Save and scan' });
    const close = make('button', { type: 'button', textContent: 'Close' });
    save.addEventListener('click', () => run(async () => {
      const body = {};
      for (const field of settingFields) {
        const input = inputs[field.key];
        if (field.type === 'checkbox') body[field.key] = input.checked;
        else if (field.type === 'number') body[field.key] = Number(input.value);
        else body[field.key] = input.value;
      }
      body.clearJellyfinApiKey = clearKey.checked;
      await call('/settings', { method: 'PUT', json: body });
      el.summary.textContent = 'Saved. Scanning with the new settings… the first scan with lookalike checks can take a few minutes.';
      plan = await call('/scan', { method: 'POST' });
      el.settings.hidden = true;
      el.settingsToggle.ariaExpanded = 'false';
      render();
      el.summary.textContent = `Settings saved. ${el.summary.textContent}`;
    }, 'Saving settings…'));
    close.addEventListener('click', () => {
      el.settings.hidden = true;
      el.settingsToggle.ariaExpanded = 'false';
    });

    el.settings.replaceChildren(
      make('h2', { textContent: 'Settings' }),
      make('p', { textContent: 'These apply to every show. Shoko\'s own settings pages do not show this plugin\'s settings, so they live here.' }),
      make('div', { className: 'settings-grid' }, ...rows),
      settings.jellyfinApiKeySet
        ? make('div', { className: 'setting check' }, make('label', { htmlFor: clearKey.id, textContent: 'Remove the stored Jellyfin API key' }), clearKey)
        : null,
      make('div', { className: 'series-actions' }, save, close),
    );
    el.settings.hidden = false;
    el.settingsToggle.ariaExpanded = 'true';
  }

  el.settingsToggle.addEventListener('click', () => {
    if (!el.settings.hidden) {
      el.settings.hidden = true;
      el.settingsToggle.ariaExpanded = 'false';
      return;
    }
    run(openSettings, 'Loading settings…').then((ok) => { if (ok && plan) render(); });
  });

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
  }, 'Scanning… the first scan checks posters for lookalikes, which can take a few minutes.'));
  el.logos.addEventListener('click', () => run(async () => {
    const result = await call('/logos/apply', { method: 'POST' });
    el.summary.textContent = `Logos: ${result.changed} set, ${result.kept} kept, ${result.failed} failed.`;
  }, 'Applying logos…'));
  confirmClick(
    el.apply,
    () => {
      const pending = plan
        ? plan.shows.flatMap((show) => show.series).filter((series) => series.action === 'SetPreferred').length + plan.shows.filter((show) => show.showPosterPending).length
        : 0;
      return `Click again to apply ${pending} changes`;
    },
    () => apply(null, 'Applying every pending change… this can take several minutes.'),
  );

  run(reload, 'Loading… the first load checks posters for lookalikes, which can take a few minutes.');
})();
