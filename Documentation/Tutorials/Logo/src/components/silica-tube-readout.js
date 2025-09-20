const template = document.createElement('template');
template.innerHTML = `
  <style>
    :host{display:block;color:#e6eef8;font-family:Inter,system-ui,Segoe UI,Roboto,Arial}
    .readout{display:flex;flex-direction:column;align-items:center}
    .value{font-size:20px;font-weight:600}
    .unit{font-size:11px;color:#9aa7bd}
  </style>
  <div class="readout" role="status" aria-live="polite">
    <div class="value"><slot name="value">0</slot></div>
    <div class="unit"><slot name="unit">units</slot></div>
  </div>
`;

export class SilicaTubeReadout extends HTMLElement {
  constructor(){
    super();
    this.attachShadow({mode:'open'});
    this.shadowRoot.appendChild(template.content.cloneNode(true));
  }
  setValue(n){
    const v = this.shadowRoot.querySelector('slot[name="value"]');
    // host should manage slot content; keep API minimal
  }
}
customElements.define('silica-tube-readout', SilicaTubeReadout);
