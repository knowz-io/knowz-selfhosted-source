/*
 * First-paint theme bootstrap for the self-hosted web client.
 *
 * `src/lib/theme.ts` applies the stored theme when the bundle runs, but a fresh document load
 * paints before the bundle (and its CSS) arrive, so a dark-theme user sees a white flash on
 * every reload and every login hop. This runs as a blocking classic script from index.html so
 * the `dark` class is on <html> before first paint; the inline #theme-prepaint style in
 * index.html supplies the matching ground colour.
 *
 * External file on purpose: index.html ships a CSP of `script-src 'self' https:` with no
 * 'unsafe-inline', so an inline <script> would be dropped.
 *
 * Keep the rule in step with src/lib/theme.ts: dark only when 'dark' is stored, else light.
 */
(function () {
  var theme = null;
  try {
    theme = window.localStorage.getItem('theme');
  } catch (e) {
    /* storage blocked — fall through to the light default */
  }
  var isDark = theme === 'dark';
  document.documentElement.classList.toggle('dark', isDark);
  document.documentElement.style.colorScheme = isDark ? 'dark' : 'light';
})();
