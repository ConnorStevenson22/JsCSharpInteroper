// A library-style subscription over a DOM EventTarget, for the PreventDefault checks in the browser and in
// tests/interoper-js (not part of demo-maps).
export class Target {
  #target = new EventTarget();

  on(type, listener) {
    this.#target.addEventListener(type, listener);
    return { dispose: () => this.#target.removeEventListener(type, listener) };
  }

  // Dispatches a cancelable event; a later DOM listener reports whether it saw the default prevented.
  dispatch(type) {
    let prevented = null;
    const after = (e) => { prevented = e.defaultPrevented; };
    this.#target.addEventListener(type, after);
    this.#target.dispatchEvent(new Event(type, { cancelable: true }));
    this.#target.removeEventListener(type, after);
    return prevented;
  }
}

export const target = () => new Target();
