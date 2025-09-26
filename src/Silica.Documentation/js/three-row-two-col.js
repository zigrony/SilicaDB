/* three-row-two-col.js
   - Shadow DOM for structure and encapsulated layout
   - Consumes theme tokens provided via host attribute three-row-two-col[theme="..."]
   - Resizer with pointer + keyboard accessibility
   - Ensures internal regions scroll; component fills parent (styles in component-base.css)
*/

const template = document.createElement('template');
template.innerHTML = `
  <style>
    :host{
      display: block;
      width:100%;
      height:100%;
      min-height:0;
      --menu-width: var(--initial-menu-width, 240px);
      font-family: inherit;
      color: inherit;
      box-sizing: border-box;
    }

    .container{
      height:100%;
      width:100%;
      display:flex;
      flex-direction:column;
      background: var(--bg);
      min-height:0;
      overflow: hidden;
      box-sizing: border-box;
    }

    .row.header{
      height: var(--header-height);
      display:flex;
      align-items:center;
      padding:0 16px;
      gap:12px;
      background: var(--surface);
      box-shadow: var(--shadow);
      flex: 0 0 var(--header-height);
      box-sizing: border-box;
    }

    .row.footer{
      height: var(--footer-height);
      display:flex;
      align-items:center;
      padding:0 16px;
      gap:12px;
      background: var(--surface);
      box-shadow: var(--shadow);
      flex: 0 0 var(--footer-height);
      box-sizing: border-box;
    }

    .row.main{
      flex:1 1 auto;
      display:flex;
      min-height:0;
      overflow:hidden;
      align-items:stretch;
      box-sizing: border-box;
    }

    .menu{
      width: min(max(var(--menu-width), var(--min-menu-width)), var(--max-menu-width));
      min-width: var(--min-menu-width);
      max-width: var(--max-menu-width);
      overflow:auto;
      padding:12px;
      background: linear-gradient(180deg, var(--menu-gradient-from), var(--menu-gradient-to));
      border-right: 1px solid var(--divider-bg);
      box-sizing: border-box;
      flex: 0 0 auto;
    }

    .resizer{
      width: var(--divider-width);
      cursor: col-resize;
      display:flex;
      align-items:center;
      justify-content:center;
      position:relative;
      flex: 0 0 var(--divider-width);
      box-sizing: border-box;
      background: transparent;
    }
    .resizer::after{
      content: "";
      width:2px;
      height:48px;
      background: var(--divider-bg);
      border-radius:2px;
    }
    .resizer:focus{ outline:none; box-shadow: var(--focus-ring); background: rgba(37,99,235,0.03); }

    .content{
      flex:1 1 auto;
      padding:16px;
      overflow:auto;
      min-height:0;
      box-sizing: border-box;
      background: transparent;
    }

    @media (max-width:720px){
      .menu{ min-width:120px; }
    }
  </style>

  <div class="container" part="container" role="application">
    <header class="row header" part="header" aria-label="Header">
      <slot name="header"></slot>
    </header>

    <main class="row main" part="main" aria-label="Main content">
      <aside class="menu" part="menu" id="menu" role="region" aria-label="Navigation menu">
        <slot name="menu"></slot>
      </aside>

      <div class="resizer" part="resizer" id="resizer" role="separator" tabindex="0"
           aria-orientation="vertical" aria-label="Resize navigation" aria-valuemin="0" aria-valuemax="100" aria-valuenow="50" aria-controls="menu"></div>

      <section class="content" part="content" role="region" aria-label="Content area">
        <slot name="content"></slot>
      </section>
    </main>

    <footer class="row footer" part="footer" aria-label="Footer">
      <slot name="footer"></slot>
    </footer>
  </div>
`;

class ThreeRowTwoCol extends HTMLElement {
  static get observedAttributes(){ return ['menu-width','theme']; }

  constructor(){
    super();
    this._shadow = this.attachShadow({ mode: 'open' });
    this._shadow.appendChild(template.content.cloneNode(true));

    this._menu = this._shadow.getElementById('menu');
    this._resizer = this._shadow.getElementById('resizer');

    this._min = null;
    this._max = null;

    this._dragging = false;
    this._startX = 0;
    this._startWidth = 0;

    this._onPointerDown = this._onPointerDown.bind(this);
    this._onPointerMove = this._onPointerMove.bind(this);
    this._onPointerUp = this._onPointerUp.bind(this);
    this._onKeyDown = this._onKeyDown.bind(this);
    this._onDoubleClick = this._onDoubleClick.bind(this);
    this._onWindowResize = this._onWindowResize.bind(this);
  }

  connectedCallback(){
    const cs = getComputedStyle(this);
    if (!this.style.getPropertyValue('--menu-width')) {
      const initial = cs.getPropertyValue('--initial-menu-width') || '240px';
      this.style.setProperty('--menu-width', initial.trim());
    }

    const minCss = cs.getPropertyValue('--min-menu-width') || '160px';
    const maxCss = cs.getPropertyValue('--max-menu-width') || '540px';
    this._min = parseInt(minCss, 10);
    this._max = parseInt(maxCss, 10);

    this._resizer.addEventListener('pointerdown', this._onPointerDown);
    this._resizer.addEventListener('keydown', this._onKeyDown);
    this._resizer.addEventListener('dblclick', this._onDoubleClick);

    this._updateAriaRange();

    if (this.hasAttribute('menu-width')) {
      this._setMenuWidth(this.getAttribute('menu-width'));
    }

    // reflect or default theme attribute so external theme CSS can match host selector
    if (!this.hasAttribute('theme')) this.setAttribute('theme','light');

    window.addEventListener('pointerup', this._onPointerUp);
    window.addEventListener('resize', this._onWindowResize);
  }

  disconnectedCallback(){
    this._resizer.removeEventListener('pointerdown', this._onPointerDown);
    this._resizer.removeEventListener('keydown', this._onKeyDown);
    this._resizer.removeEventListener('dblclick', this._onDoubleClick);
    window.removeEventListener('pointerup', this._onPointerUp);
    window.removeEventListener('resize', this._onWindowResize);
  }

  attributeChangedCallback(name, oldVal, newVal){
    if (name === 'menu-width' && oldVal !== newVal) this._setMenuWidth(newVal);
    if (name === 'theme' && oldVal !== newVal) {
      // theme visual tokens are applied via external CSS targeting host attribute
      // no extra work needed here, but keep attribute in sync
      if (!newVal) this.setAttribute('theme','light');
    }
  }

  set menuWidth(v){ this.setAttribute('menu-width', v); }
  get menuWidth(){ return this.getAttribute('menu-width') || this.style.getPropertyValue('--menu-width').trim(); }

  set theme(t){ this.setAttribute('theme', t); }
  get theme(){ return this.getAttribute('theme') || 'light'; }

  _updateAriaRange(){
    const min = this._min || 0;
    const max = this._max || 1000;
    this._resizer.setAttribute('aria-valuemin', String(min));
    this._resizer.setAttribute('aria-valuemax', String(max));
    const current = this._menu.getBoundingClientRect().width;
    this._resizer.setAttribute('aria-valuenow', String(Math.round(current)));
  }

  _onWindowResize(){
    const width = this._menu.getBoundingClientRect().width;
    this._setMenuWidth(`${Math.min(this._max, Math.max(this._min, width))}px`);
  }

  _onPointerDown(e){
    if (e.button !== 0) return;
    this._dragging = true;
    this._startX = e.clientX;
    this._startWidth = this._menu.getBoundingClientRect().width;
    this._resizer.setPointerCapture?.(e.pointerId);
    document.documentElement.style.cursor = 'col-resize';
    this._resizer.classList.add('dragging');
    window.addEventListener('pointermove', this._onPointerMove);
    e.preventDefault();
  }

  _onPointerMove(e){
    if (!this._dragging) return;
    const dx = e.clientX - this._startX;
    let newWidth = this._startWidth + dx;
    newWidth = Math.max(this._min, Math.min(this._max, newWidth));
    this._setMenuWidth(`${Math.round(newWidth)}px`);
  }

  _onPointerUp(e){
    if (!this._dragging) return;
    this._dragging = false;
    try { this._resizer.releasePointerCapture?.(e?.pointerId); } catch (_) {}
    document.documentElement.style.cursor = '';
    this._resizer.classList.remove('dragging');
    window.removeEventListener('pointermove', this._onPointerMove);
    const final = this._menu.getBoundingClientRect().width;
    this.setAttribute('menu-width', `${Math.round(final)}px`);
    this._updateAriaRange();
  }

  _onDoubleClick(){
    const current = this._menu.getBoundingClientRect().width;
    const compact = Math.max(this._min, Math.round(this._min * 1.0));
    const expanded = parseInt(getComputedStyle(this).getPropertyValue('--initial-menu-width')) || 240;
    const target = Math.abs(current - compact) < 8 ? `${expanded}px` : `${compact}px`;
    this._setMenuWidth(target);
    this.setAttribute('menu-width', target);
    this._updateAriaRange();
  }

  _onKeyDown(e){
    const step = 8;
    const key = e.key;
    const current = this._menu.getBoundingClientRect().width;
    if (key === 'ArrowLeft' || key === 'ArrowDown'){
      e.preventDefault();
      const w = Math.max(this._min, current - step);
      this._setMenuWidth(`${w}px`);
      this.setAttribute('menu-width', `${w}px`);
      this._updateAriaRange();
    } else if (key === 'ArrowRight' || key === 'ArrowUp'){
      e.preventDefault();
      const w = Math.min(this._max, current + step);
      this._setMenuWidth(`${w}px`);
      this.setAttribute('menu-width', `${w}px`);
      this._updateAriaRange();
    } else if (key === 'Home'){
      e.preventDefault();
      this._setMenuWidth(`${this._min}px`);
      this.setAttribute('menu-width', `${this._min}px`);
      this._updateAriaRange();
    } else if (key === 'End'){
      e.preventDefault();
      this._setMenuWidth(`${this._max}px`);
      this.setAttribute('menu-width', `${this._max}px`);
      this._updateAriaRange();
    }
  }

  _setMenuWidth(value){
    if (typeof value === 'number') value = `${value}px`;
    if (String(value).trim().endsWith('%')) {
      const pct = parseFloat(value);
      const parentWidth = this._shadow.host.getBoundingClientRect().width;
      value = `${Math.round(parentWidth * (pct / 100))}px`;
    }
    const px = parseInt(value, 10);
    const clamped = Math.max(this._min, Math.min(this._max, px));
    this.style.setProperty('--menu-width', `${clamped}px`);
    this._resizer.setAttribute('aria-valuenow', String(clamped));
  }
}

customElements.define('three-row-two-col', ThreeRowTwoCol);
export default ThreeRowTwoCol;
