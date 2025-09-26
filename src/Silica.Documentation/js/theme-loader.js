/* js/theme-loader.js
   Fixed: construct theme link hrefs relative to this module using import.meta.url
   Usage: applyThemeSelector(hostElement, 'dark'|'light'|'sepia'|'auto')
*/

const THEME_KEY = 'three-row-theme';
const availableThemes = new Set(['light','dark','sepia']);

const linkCache = new Map();

function _createLink(theme) {
  // compute path relative to this module file (js/theme-loader.js)
  // css files are located at ../css/themes/<theme>.css relative to this module
  const href = new URL(`../css/themes/${theme}.css`, import.meta.url).href;
  const link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = href;
  link.dataset.threeRowTheme = theme;
  link.id = `three-row-theme-${theme}`;
  link.onerror = () => {
    console.error(`Failed to load theme CSS: ${href}`);
  };
  return link;
}

function _ensureThemeLink(theme) {
  if (!availableThemes.has(theme)) return null;
  if (linkCache.has(theme)) return linkCache.get(theme);
  const link = _createLink(theme);
  linkCache.set(theme, link);
  return link;
}

function _applyThemeToDocument(theme) {
  // Remove non-matching theme links (but keep cached references)
  for (const [t, link] of linkCache.entries()) {
    if (t === theme) {
      if (!document.head.contains(link)) document.head.appendChild(link);
    } else {
      if (document.head.contains(link)) document.head.removeChild(link);
    }
  }
  // ensure requested theme link exists and is attached
  const requested = _ensureThemeLink(theme);
  if (requested && !document.head.contains(requested)) document.head.appendChild(requested);
}

function _systemPrefersDark() {
  return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
}

export function loadPreferredTheme() {
  const stored = localStorage.getItem(THEME_KEY);
  if (!stored) return 'light';
  return stored;
}

export function applyThemeSelector(hostElement, theme) {
  const host = (typeof hostElement === 'string') ? document.querySelector(hostElement) : hostElement;
  if (!host) return;

  if (theme === 'auto') {
    const sys = _systemPrefersDark() ? 'dark' : 'light';
    host.setAttribute('theme', sys);
    localStorage.setItem(THEME_KEY, 'auto');
    _ensureThemeLink(sys);
    _applyThemeToDocument(sys);
    return;
  }

  if (!availableThemes.has(theme)) theme = 'light';
  host.setAttribute('theme', theme);
  localStorage.setItem(THEME_KEY, theme);

  _ensureThemeLink(theme);
  _applyThemeToDocument(theme);
}
