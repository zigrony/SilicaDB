// ---------- Utilities ----------
function $(sel, ctx = document) { return ctx.querySelector(sel); }
function $all(sel, ctx = document) { return Array.from(ctx.querySelectorAll(sel)); }

// Robust copy-to-clipboard that works offline and under file:// when async API is restricted
async function copyText(text) {
  try {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {}
  // Fallback for non-secure contexts
  const ta = document.createElement('textarea');
  ta.value = text;
  ta.style.position = 'fixed';
  ta.style.top = '-9999px';
  document.body.appendChild(ta);
  ta.focus();
  ta.select();
  let ok = false;
  try { ok = document.execCommand('copy'); } catch {}
  document.body.removeChild(ta);
  return ok;
}

// Smooth scroll helper
function smoothScrollTo(el, offset = 110) {
  const top = Math.max(0, el.getBoundingClientRect().top + window.scrollY - offset);
  window.scrollTo({ top, behavior: 'smooth' });
}

// ---------- Highlight.js ----------
function initHighlighting() {
  if (window.hljs && typeof hljs.highlightElement === 'function') {
    $all('pre code').forEach(block => hljs.highlightElement(block));
  }
}

// ---------- Copy buttons ----------
function initCopyButtons() {
  $all('.copy-btn').forEach(btn => {
    btn.addEventListener('click', async () => {
      const wrap = btn.closest('.code-block-wrap') || btn.parentElement;
      const code = wrap ? wrap.querySelector('code') : null;
      const text = code ? code.innerText : '';
      const success = await copyText(text);
      const prev = btn.innerText;
      btn.innerText = success ? 'Copied!' : 'Copy failed';
      setTimeout(() => (btn.innerText = prev), 1200);
    });
  });
}

// ---------- Tabs ----------
function initTabs() {
  $all('.tab-nav').forEach(nav => {
    nav.addEventListener('click', e => {
      if (!e.target.classList.contains('tab-btn')) return;
      const group = nav.parentElement;
      const tab = e.target.dataset.tab;
      $all('.tab-btn', nav).forEach(b => b.classList.remove('active'));
      e.target.classList.add('active');
      $all('.tab-content', group).forEach(c => {
        c.style.display = c.dataset.tab === tab ? '' : 'none';
      });
    });
  });
}

// ---------- Theme toggle ----------
function initTheme() {
  const btn = $('#toggle-theme');
  const root = document.documentElement;

  function applySavedOrSystem() {
    const saved = localStorage.getItem('theme');
    if (saved) {
      root.setAttribute('data-theme', saved);
    } else if (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) {
      root.setAttribute('data-theme', 'dark');
    } else {
      root.setAttribute('data-theme', 'light');
    }
  }

  applySavedOrSystem();

  if (btn) {
    btn.addEventListener('click', () => {
      const next = root.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
      root.setAttribute('data-theme', next);
      localStorage.setItem('theme', next);
    });
  }
}

// ---------- Search filter ----------
function initSearch() {
  const input = $('#search-input');
  if (!input) return;
  input.addEventListener('input', () => {
    const term = input.value.trim().toLowerCase();
    $all('.docs-section').forEach(sec => {
      const text = sec.innerText.toLowerCase();
      sec.style.display = text.includes(term) ? '' : 'none';
    });
  });
}

// ---------- Scroll spy ----------
function initScrollSpy() {
  const links = $all('.toc-link');
  const sections = links.map(link => $(link.getAttribute('href'))).filter(Boolean);

  function onScroll() {
    let activeIndex = -1;
    sections.forEach((sec, i) => {
      const rect = sec.getBoundingClientRect();
      if (rect.top <= 120) activeIndex = i;
    });
    links.forEach(l => l.classList.remove('active'));
    if (activeIndex >= 0) links[activeIndex].classList.add('active');
  }

  window.addEventListener('scroll', onScroll, { passive: true });
  onScroll();
}

// ---------- Smooth anchor scrolling ----------
function initSmoothAnchors() {
  $all('a[href^="#"]').forEach(a => {
    a.addEventListener('click', e => {
      const target = $(a.getAttribute('href'));
      if (target) {
        e.preventDefault();
        smoothScrollTo(target);
      }
    });
  });
}

// ---------- Auto-generate ToC ----------
function initAutoToc() {
  const tocList = $('#toc-list');
  if (!tocList || !tocList.dataset.autoToc) return;
  tocList.innerHTML = '';
  $all('.docs-section').forEach(sec => {
    const h = sec.querySelector('h1, h2');
    if (h && sec.id) {
      const li = document.createElement('li');
      const a = document.createElement('a');
      a.href = `#${sec.id}`;
      a.textContent = h.textContent;
      a.className = 'toc-link';
      li.appendChild(a);
      tocList.appendChild(li);
    }
  });
}

// ---------- Init all ----------
document.addEventListener('DOMContentLoaded', () => {
  initTheme();
  initHighlighting();
  initCopyButtons();
  initTabs();
  initSearch();
  initAutoToc();
  initScrollSpy();
  initSmoothAnchors();
});
