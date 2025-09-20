document.addEventListener("DOMContentLoaded", () => {
  const content = document.getElementById("content");
  const links = document.querySelectorAll("nav a");
  const themeToggle = document.getElementById("theme-toggle");
  const themeLink = document.getElementById("theme-link");

  // Helper: set active nav link
  function setActive(page) {
    links.forEach(a => {
      const p = a.getAttribute("data-page");
      if (p === page) a.classList.add("active");
      else a.classList.remove("active");
    });
  }

  // Load markdown and wrap in a readable container
  async function loadPage(page) {
    try {
      const html = await loadMarkdown(page);
      content.innerHTML = `<article class="doc">${html}</article>`;
      setActive(page);
    } catch (err) {
      content.innerHTML = `<article class="doc"><div class="callout-danger callout"><strong>Error:</strong> Failed to load <code>${page}</code>.</div></article>`;
      console.error(err);
    }
  }

  // Click handlers
  links.forEach(link => {
    link.addEventListener("click", async (e) => {
      e.preventDefault();
      const page = link.getAttribute("data-page");
      // update URL hash so refresh/back works
      window.location.hash = page;
      loadPage(page);
    });
  });

  // Theme toggle via stylesheet swap
  themeToggle.addEventListener("click", () => {
    const isLight = themeLink.getAttribute("href").includes("light");
    const nextHref = isLight ? "css/theme-dark.css" : "css/theme-light.css";
    themeLink.setAttribute("href", nextHref);
    // persist theme preference
    localStorage.setItem("themeStylesheet", nextHref);
  });

  // On load: restore theme and route
  const savedThemeHref = localStorage.getItem("themeStylesheet");
  if (savedThemeHref) {
    themeLink.setAttribute("href", savedThemeHref);
  }

  const initial = window.location.hash ? window.location.hash.slice(1) : null;
  const defaultPage = initial || (links[0] && links[0].getAttribute("data-page")) || "intro.md";
  if (defaultPage) {
    setActive(defaultPage);
    // If there's an initial hash, load it; otherwise leave the placeholder
    if (initial) loadPage(defaultPage);
  }

  // Respond to manual hash changes (back/forward)
  window.addEventListener("hashchange", () => {
    const page = window.location.hash.slice(1);
    if (page) loadPage(page);
  });
});

