// /js/doc-layout.js
class DocLayout extends HTMLElement {
  static get observedAttributes() { return ['layout']; }

  constructor() {
    super();
    this.attachShadow({ mode: 'open' });

    const tpl = document.createElement('template');
    tpl.innerHTML = `
      <style>
        :host {
          display: grid;
          height: 100vh;
          width: 100vw;
          color: var(--color-fg);
          background: var(--color-bg);
          grid-template-rows: var(--layout-header-height) 1fr var(--layout-footer-height);
          grid-template-columns: 100%;
        }

        header {
          display: flex;
          align-items: center;
          gap: 12px;
          padding: 0 12px;
          background: var(--header-bg);
          color: var(--header-fg);
          box-shadow: var(--shadow);
        }

        main { min-height: 0; } /* allow children to scroll */

        aside[part="sidebar"] {
          overflow: auto;
          background: var(--sidebar-bg);
          color: var(--sidebar-fg);
          border-right: 1px solid var(--splitter-color);
        }

        section[part="content"] {
          overflow: hidden;
          background: var(--content-bg);
          color: var(--content-fg);
        }

        footer {
          display: flex;
          align-items: center;
          justify-content: flex-end;
          padding: 0 12px;
          background: var(--footer-bg);
          color: var(--footer-fg);
          box-shadow: var(--shadow);
        }

        /* Minimal layout: hide sidebar entirely */
        :host([data-layout="minimal"]) aside[part="sidebar"] { display: none; }

        /* Responsive collapse: hide sidebar on narrow screens */
        @media (max-width: 900px) {
          aside[part="sidebar"] { display: none; }
        }
      </style>

      <header part="header"><slot name="header"></slot></header>

      <main part="main">
        <!-- Only split-pane in the app: between sidebar and content -->
        <split-pane orientation="vertical" min="200" max="800" persist-key="sidebar-width">
          <aside slot="first" part="sidebar"><slot name="sidebar"></slot></aside>
          <section slot="second" part="content"><slot name="content"></slot></section>
        </split-pane>
      </main>

      <footer part="footer"><slot name="footer"></slot></footer>
    `;
    this.shadowRoot.appendChild(tpl.content.cloneNode(true));
  }

  connectedCallback() {
    if (!this.hasAttribute('layout')) this.setAttribute('layout', 'robust');
    this._syncLayoutAttr();
  }

  attributeChangedCallback(name) {
    if (name === 'layout') this._syncLayoutAttr();
  }

  _syncLayoutAttr() {
    const val = (this.getAttribute('layout') || 'robust').toLowerCase();
    const supported = new Set(['robust', 'minimal']);
    const normalized = supported.has(val) ? val : 'robust';
    this.setAttribute('data-layout', normalized);
  }
}
customElements.define('doc-layout', DocLayout);
