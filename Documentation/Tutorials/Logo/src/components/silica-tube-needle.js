// Presentational needle component (optional, small)
const template = document.createElement('template');
template.innerHTML = `<style>:host{display:block}</style><slot></slot>`;

export class SilicaTubeNeedle extends HTMLElement {
  constructor(){
    super();
    this.attachShadow({mode:'open'});
    this.shadowRoot.appendChild(template.content.cloneNode(true));
  }
  // Pure presentational; renderer currently draws the needle for performance reasons.
}
customElements.define('silica-tube-needle', SilicaTubeNeedle);
