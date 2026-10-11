// No export reaches this file; the package's `./components/*` export reaches index.js next to it, which re-exports it.

// Outside a browser the element is a plain EventTarget, so its logic still runs under node --test.
const Base = globalThis.HTMLElement ?? EventTarget;

export class ZoomLabelElement extends Base {
  view = null;
  precision = 0;

  get text() {
    return (this.view?.zoom ?? 0).toFixed(this.precision);
  }

  copy() {
    this.dispatchEvent(new CustomEvent("demo-zoom-label-copy", { detail: { text: this.text } }));
  }
}

if (globalThis.customElements && !customElements.get("demo-zoom-label")) customElements.define("demo-zoom-label", ZoomLabelElement);
