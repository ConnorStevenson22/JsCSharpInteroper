// Base classes shared by every live object in the library.

// Properties live in a private map so that `watch` can report every change.
export class Observable {
  static className = "core.Observable";
  #values = new Map();
  #watchers = new Map();

  // The runtime class name, so callers can find the most-derived type.
  get className() {
    return this.constructor.className;
  }

  watch(name, callback) {
    const callbacks = this.#watchers.get(name) ?? new Set();
    this.#watchers.set(name, callbacks);
    callbacks.add(callback);
    return { dispose: () => callbacks.delete(callback) };
  }

  _get(name) {
    return this.#values.get(name);
  }

  _set(name, value) {
    const old = this.#values.get(name);
    if (Object.is(old, value)) return;
    this.#values.set(name, value);
    for (const callback of this.#watchers.get(name) ?? []) callback(value, old);
  }
}

export class Emitter extends Observable {
  static className = "core.Emitter";
  #listeners = new Map();

  on(type, listener) {
    const listeners = this.#listeners.get(type) ?? new Set();
    this.#listeners.set(type, listeners);
    listeners.add(listener);
    return { dispose: () => listeners.delete(listener) };
  }

  emit(type, event) {
    const listeners = this.#listeners.get(type);
    for (const listener of listeners ?? []) listener(event);
    return (listeners?.size ?? 0) > 0;
  }
}

export class Collection extends Emitter {
  static className = "core.Collection";
  #items = [];

  constructor(items = []) {
    super();
    this.#items = [...items];
  }

  get length() {
    return this.#items.length;
  }

  at(index) {
    return this.#items.at(index);
  }

  add(item) {
    this.#items.push(item);
    this.emit("change", { added: [item], removed: [] });
  }

  remove(item) {
    const index = this.#items.indexOf(item);
    if (index < 0) return false;
    this.#items.splice(index, 1);
    this.emit("change", { added: [], removed: [item] });
    return true;
  }

  find(predicate) {
    return this.#items.find(predicate);
  }

  toArray() {
    return [...this.#items];
  }
}
