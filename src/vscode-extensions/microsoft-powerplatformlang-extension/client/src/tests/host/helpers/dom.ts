interface ElementEvent {
  key: string;
  ctrlKey?: boolean;
  metaKey?: boolean;
  altKey?: boolean;
  isComposing?: boolean;
  target?: Element | null;
  relatedTarget?: Element | null;
  preventDefault(): void;
}

export class Element {
  id = '';
  textContent = '';
  title = '';
  className = '';
  value = '';
  disabled = false;
  focusCount = 0;
  children: Element[] = [];
  private readonly listeners = new Map<string, (event: ElementEvent) => void>();
  private readonly attributes = new Map<string, string>();
  readonly hiddenClasses = new Set<string>();
  readonly style: Record<string, string> = {};
  readonly classList = {
    toggle: (name: string, force: boolean) => {
      if (force) {
        this.hiddenClasses.add(name);
      } else {
        this.hiddenClasses.delete(name);
      }
    }
  };
  appendChild(child: Element): void { this.children.push(child); }
  replaceChildren(): void { this.children = []; }
  addEventListener(type: string, listener: (event: ElementEvent) => void): void { this.listeners.set(type, listener); }
  fire(type: string, event: Partial<ElementEvent> = {}): void {
    this.listeners.get(type)?.({ key: '', preventDefault() {}, ...event });
  }
  setAttribute(name: string, value: string): void { this.attributes.set(name, value); }
  getAttribute(name: string): string | null { return this.attributes.get(name) ?? null; }
  removeAttribute(name: string): void { this.attributes.delete(name); }
  contains(target: Element | null | undefined): boolean {
    return target === this || this.children.some(child => child.contains(target));
  }
  focus(): void { this.focusCount++; }
  scrollIntoView(): void {}
  querySelectorAll(): Element[] { return []; }
}
