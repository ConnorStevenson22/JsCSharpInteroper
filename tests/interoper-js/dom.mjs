// A library-style subscription over a DOM EventTarget, for event-option tests (loaded with a module `call`, not part
// of demo-maps).
export class Target {
  #target = new EventTarget();

  on(type, listener) {
    this.#target.addEventListener(type, listener);
    return { dispose: () => this.#target.removeEventListener(type, listener) };
  }

  // Dispatches a cancelable event and returns whether a listener prevented its default.
  dispatch(type) {
    const event = new Event(type, { cancelable: true });
    this.#target.dispatchEvent(event);
    return event.defaultPrevented;
  }
}

export const target = () => new Target();
