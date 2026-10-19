// A mixin: a function from a base class to a subclass of it. Its typings declare the class it returns.
export const Refreshable = (Base) => class extends Base {
  get refreshInterval() { return this._get("refreshInterval") ?? 0; }
  set refreshInterval(value) { this._set("refreshInterval", value); }
  get refreshCount() { return this._get("refreshCount") ?? 0; }

  refresh() {
    this._set("refreshCount", this.refreshCount + 1);
  }

  sibling() {
    return new this.constructor();
  }
};
