/** Returned by `watch` and `on`; call `dispose()` to stop listening. */
export interface Subscription {
  dispose(): void;
}

export type ChangeCallback<T> = (newValue: T, oldValue: T) => void;

export class Observable {
  /** The runtime class name: `"<module>.<Class>"`, or just `"<Class>"` when the module is named after it. */
  readonly className: string;
  /** Calls `callback` synchronously whenever the property changes. */
  watch<K extends keyof this & string>(name: K, callback: ChangeCallback<this[K]>): Subscription;
}

export class Emitter extends Observable {
  /** Phantom property: never set at runtime, it only declares the event map. */
  "$events": object;
  on<K extends keyof this["$events"] & string>(type: K, listener: (event: this["$events"][K]) => void): Subscription;
  /** Returns whether any listener ran. */
  emit<K extends keyof this["$events"] & string>(type: K, event: this["$events"][K]): boolean;
}

export interface CollectionChangeEvent<T> {
  added: T[];
  removed: T[];
}

export interface CollectionEvents<T> {
  change: CollectionChangeEvent<T>;
}

export class Collection<T> extends Emitter {
  "$events": CollectionEvents<T>;
  constructor(items?: T[]);
  readonly length: number;
  at(index: number): T | undefined;
  add(item: T): void;
  /** Returns whether the item was found. */
  remove(item: T): boolean;
  find(predicate: (item: T, index: number) => boolean): T | undefined;
  /** Calls `callback` for each item, in order; its result is ignored. */
  forEach(callback: (item: T, index: number) => void): void;
  toArray(): T[];
}
