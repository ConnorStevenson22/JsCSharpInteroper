/** Returned by `watch` and `on`; call `dispose()` to stop listening. */
export interface Subscription {
  dispose(): void;
}

export type ChangeCallback<T> = (newValue: T, oldValue: T) => void;

/** One value or several: a generic union alias, which each use instantiates (§2). */
export type OneOrMany<T> = T | T[];

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

// `T` has a default, as many libraries' generic types do (§2).
export interface CollectionChangeEvent<T = any> {
  added: T[];
  removed: T[];
}

export interface CollectionEvents<T> {
  change: CollectionChangeEvent<T>;
}

export class Collection<T = any> extends Emitter {
  "$events": CollectionEvents<T>;
  constructor(items?: T[]);
  readonly length: number;
  at(index: number): T | undefined;
  add(item: T): void;
  /** Returns whether the item was found. */
  remove(item: T): boolean;
  find(predicate: (item: T, index: number) => boolean): T | undefined;
  /**
   * Calls `callback` for each item, in order; its result is ignored. Like `Array.prototype.forEach`, it also passes
   * the collection, which the type leaves out.
   */
  forEach(callback: (item: T, index: number) => void): void;
  toArray(): T[];
}
