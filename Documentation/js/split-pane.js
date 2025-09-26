// /js/split-pane.js
class SplitPane extends HTMLElement {
  static get observedAttributes() {
    return ['orientation', 'min', 'max', 'persist-key'];
  }

  constructor() {
    super();
    this.attachShadow({ mode: 'open' });

    const tpl = document.createElement('template');
    tpl.innerHTML = `
      <style>
        :host {
          display: grid;
          width: 100%;
          height: 100%;
          /* Grid template is set dynamically based on orientation */
        }

        .pane {
          overflow: auto;
          background: var(--content-bg);
          color: var(--content-fg);
        }

        .separator {
          background: var(--splitter-color);
          cursor: col-resize;
          position: relative;
        }

        .separator[aria-orientation="horizontal"] {
          cursor: row-resize;
        }

        .separator:focus {
          outline: 2px solid var(--focus-ring);
          outline-offset: -2px;
        }

        /* Increase hit area without changing visual size */
        .separator::before {
          content: "";
          position: absolute;
          inset: -2px;
        }
      </style>
      <div class="pane" part="first"><slot name="first"></slot></div>
      <div class="separator"
           role="separator"
           tabindex="0"
           aria-valuemin="0"
           aria-valuemax="100"
           aria-valuenow="50"
           aria-orientation="vertical"></div>
      <div class="pane" part="second"><slot name="second"></slot></div>
    `;
    this.shadowRoot.appendChild(tpl.content.cloneNode(true));

    this._first = this.shadowRoot.querySelector('.pane:nth-of-type(1)');
    this._sep = this.shadowRoot.querySelector('.separator');
    this._second = this.shadowRoot.querySelector('.pane:nth-of-type(3)');

    this._state = {
      orientation: 'vertical', // or 'horizontal'
      min: 160,
      max: 800,
      sizePx: null,
    };

    this._onMouseMove = null;
    this._onMouseUp = null;
  }

  connectedCallback() {
    this._applyOrientation();
    this._restore();
    this._attachEvents();
    this._applyGrid();
  }

  attributeChangedCallback(name, _old, value) {
    if (name === 'orientation') {
      this._state.orientation = (value === 'horizontal') ? 'horizontal' : 'vertical';
      this._sep.setAttribute('aria-orientation', this._state.orientation);
      this._applyOrientation();
    }
    if (name === 'min') this._state.min = Number(value) || this._state.min;
    if (name === 'max') this._state.max = Number(value) || this._state.max;
    if (name === 'persist-key') this._applyGrid(); // size applied after restore
  }

  _applyOrientation() {
    if (this._state.orientation === 'vertical') {
      this.style.gridTemplateColumns = '1fr 4px 2fr';
      this.style.gridTemplateRows = '1fr';
    } else {
      this.style.gridTemplateRows = '1fr 4px 1fr';
      this.style.gridTemplateColumns = '1fr';
    }
  }

  _applyGrid() {
    const sizePx = this._state.sizePx;
    if (sizePx == null) return;

    if (this._state.orientation === 'vertical') {
      this.style.gridTemplateColumns = `${sizePx}px 4px 1fr`;
    } else {
      this.style.gridTemplateRows = `${sizePx}px 4px 1fr`;
    }
  }

  _attachEvents() {
    // Pointer drag
    this._sep.addEventListener('mousedown', (ev) => {
      ev.preventDefault();
      const startX = ev.clientX;
      const startY = ev.clientY;
      const rectFirst = this._first.getBoundingClientRect();
      const startSize = (this._state.orientation === 'vertical') ? rectFirst.width : rectFirst.height;

      const move = (e) => {
        const delta = (this._state.orientation === 'vertical')
          ? e.clientX - startX
          : e.clientY - startY;

        const total = (this._state.orientation === 'vertical')
          ? this.clientWidth
          : this.clientHeight;

        let next = Math.max(this._state.min, Math.min(startSize + delta, total - this._state.min));
        this._state.sizePx = next;
        this._applyGrid();
        this._updateAria();
      };

      const up = () => {
        window.removeEventListener('mousemove', move);
        window.removeEventListener('mouseup', up);
        this._persist();
      };

      window.addEventListener('mousemove', move);
      window.addEventListener('mouseup', up);
    });

    // Keyboard resize for accessibility
    this._sep.addEventListener('keydown', (e) => {
      const step = 10;
      if (['ArrowLeft', 'ArrowUp', 'ArrowRight', 'ArrowDown'].includes(e.key)) {
        e.preventDefault();
        const dir = (e.key === 'ArrowLeft' || e.key === 'ArrowUp') ? -1 : 1;
        const total = (this._state.orientation === 'vertical') ? this.clientWidth : this.clientHeight;
        const start = this._state.sizePx ?? ((this._state.orientation === 'vertical') ? this._first.getBoundingClientRect().width : this._first.getBoundingClientRect().height);
        let next = Math.max(this._state.min, Math.min(start + dir * step, total - this._state.min));
        this._state.sizePx = next;
        this._applyGrid();
        this._persist();
        this._updateAria();
      }
    });

    // ResizeObserver to re-apply constraints if container changes
    const ro = new ResizeObserver(() => {
      const total = (this._state.orientation === 'vertical') ? this.clientWidth : this.clientHeight;
      if (this._state.sizePx != null) {
        this._state.sizePx = Math.max(this._state.min, Math.min(this._state.sizePx, total - this._state.min));
        this._applyGrid();
        this._updateAria();
      }
    });
    ro.observe(this);
  }

  _updateAria() {
    const total = (this._state.orientation === 'vertical') ? this.clientWidth : this.clientHeight;
    const now = Math.round(((this._state.sizePx ?? 0) / total) * 100);
    this._sep.setAttribute('aria-valuenow', String(Math.max(0, Math.min(100, now))));
    this._sep.setAttribute('aria-valuemin', '0');
    this._sep.setAttribute('aria-valuemax', '100');
  }

  _persist() {
    const key = this.getAttribute('persist-key');
    if (!key) return;
    try {
      localStorage.setItem(`splitpane:${key}`, JSON.stringify({
        orientation: this._state.orientation,
        sizePx: this._state.sizePx,
      }));
    } catch {}
  }

  _restore() {
    const key = this.getAttribute('persist-key');
    if (!key) return;
    try {
      const raw = localStorage.getItem(`splitpane:${key}`);
      if (!raw) return;
      const data = JSON.parse(raw);
      if (data && typeof data.sizePx === 'number') {
        this._state.sizePx = data.sizePx;
        this._applyGrid();
        this._updateAria();
      }
    } catch {}
  }
}
customElements.define('split-pane', SplitPane);
