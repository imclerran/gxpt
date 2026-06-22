/* GxPT landing page — shared release lookup (ES5, no dependencies).
   Loaded on every page. Unauthenticated calls to the GitHub Releases API
   populate, where present on the page:
     - the Download button's target (#download-btn) -> newest .msi
     - the download note's version (#download-note)
     - the sidebar "Latest release" widget version (#latest-ver)
   We prefer the dedicated /releases/latest endpoint because its cache stays
   fresh, whereas the paginated /releases list endpoint can lag by a day or
   more after a new release. Release assets are uploaded manually, so the very
   latest tag may not have its installer yet; when that happens (or the latest
   lookup fails) we fall back to walking the list for the most recent release
   that does. On any failure every element keeps its static fallback. */
(function () {
  var btn = document.getElementById('download-btn');
  var note = document.getElementById('download-note');
  var latest = document.getElementById('latest-ver');
  var baseNote = 'Windows XP or later · .NET Framework 3.5';

  function findMsi(release) {
    var assets = (release && release.assets) || [];
    for (var i = 0; i < assets.length; i++) {
      if (/\.msi$/i.test(assets[i].name) && assets[i].browser_download_url) {
        return assets[i];
      }
    }
    return null;
  }

  function apply(release, msi) {
    // Normalize the tag's leading "v" so we never produce "vv0.15.0".
    var tag = release.tag_name ? String(release.tag_name).replace(/^v/i, '') : '';
    if (btn) { btn.setAttribute('data-href', msi.browser_download_url); }
    if (note && tag) { note.innerHTML = baseNote + ' · GxPT v' + tag; }
    if (latest && tag) { latest.innerHTML = 'GxPT v' + tag; }
  }

  function fetchJson(url, cb) {
    try {
      var xhr = new XMLHttpRequest();
      xhr.open('GET', url, true);
      xhr.onreadystatechange = function () {
        if (xhr.readyState !== 4) return;
        if (xhr.status < 200 || xhr.status >= 300) { cb(null); return; } // keep fallbacks
        try { cb(JSON.parse(xhr.responseText)); }
        catch (e) { cb(null); }
      };
      xhr.send();
    } catch (e) { cb(null); }
  }

  // Fallback: walk the full list (newest first) for the most recent release
  // that carries an .msi. Used when /releases/latest has no installer yet.
  function applyFromList() {
    fetchJson('https://api.github.com/repos/imclerran/GxPT/releases?per_page=20', function (releases) {
      if (!releases || !releases.length) return; // keep fallbacks
      for (var i = 0; i < releases.length; i++) {
        if (releases[i].draft) continue;
        var msi = findMsi(releases[i]);
        if (msi) { apply(releases[i], msi); return; }
      }
      // No release carries an .msi yet — leave buttons on the releases page.
    });
  }

  // Prefer the dedicated "latest" endpoint; fall back to the list when it has
  // no installer attached yet or the request fails.
  fetchJson('https://api.github.com/repos/imclerran/GxPT/releases/latest', function (release) {
    var msi = (release && !release.draft) ? findMsi(release) : null;
    if (msi) { apply(release, msi); return; }
    applyFromList();
  });
})();
