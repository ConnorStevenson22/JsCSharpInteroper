/** Returned by `watch` and `on`; call `remove()` to stop listening. */
export interface Handle {
  remove(): void;
}

export type WatchCallback<T> = (newValue: T, oldValue: T) => void;

export class Accessor {
  /** The runtime class name, e.g. `"geometry.Point"`. */
  readonly declaredClass: string;
  /** Calls `callback` synchronously whenever the property changes. */
  watch<K extends keyof this & string>(name: K, callback: WatchCallback<this[K]>): Handle;
}

export class Evented extends Accessor {
  /** Phantom property: never set at runtime, it only declares the event map. */
  "@eventTypes": object;
  on<K extends keyof this["@eventTypes"] & string>(type: K, listener: (event: this["@eventTypes"][K]) => void): Handle;
  /** Returns whether any listener ran. */
  emit<K extends keyof this["@eventTypes"] & string>(type: K, event: this["@eventTypes"][K]): boolean;
}

export interface CollectionChangeEvent<T> {
  added: T[];
  removed: T[];
}

export interface CollectionEvents<T> {
  change: CollectionChangeEvent<T>;
}

export class Collection<T> extends Evented {
  "@eventTypes": CollectionEvents<T>;
  constructor(items?: T[]);
  readonly length: number;
  at(index: number): T | undefined;
  add(item: T): void;
  /** Returns whether the item was found. */
  remove(item: T): boolean;
  find(predicate: (item: T, index: number) => boolean): T | undefined;
  toArray(): T[];
}
