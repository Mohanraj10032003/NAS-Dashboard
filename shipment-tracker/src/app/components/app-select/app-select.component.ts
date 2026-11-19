import { Component, ElementRef, EventEmitter, HostListener, Input, Output, ViewChild } from '@angular/core';

export interface SelectOption {
  id: string;
  name: string;
}

/**
 * The application drop-down: a button that opens a list (with a search box when there are many entries).
 * Works with mouse, touch and keyboard (arrows, Enter, Esc, type to search). On a phone the list opens as a bottom sheet.
 */
@Component({
  selector: 'app-select',
  template: `
    <button
      type="button"
      class="trigger"
      [class.open]="open"
      (click)="toggle()"
      (keydown)="onTriggerKey($event)"
      role="combobox"
      aria-haspopup="listbox"
      [attr.aria-expanded]="open"
      [attr.aria-label]="label"
      #trigger
    >
      <span class="ico" aria-hidden="true">
        <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="7" height="7" rx="1.5" /><rect x="14" y="3" width="7" height="7" rx="1.5" /><rect x="3" y="14" width="7" height="7" rx="1.5" /><rect x="14" y="14" width="7" height="7" rx="1.5" /></svg>
      </span>
      <span class="val">{{ selectedName }}</span>
      <span class="count" *ngIf="options.length > 1">{{ options.length }}</span>
      <svg class="caret" viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M6 9l6 6 6-6" /></svg>
    </button>

    <div class="backdrop" *ngIf="open" (click)="close()"></div>
    <div class="pop" *ngIf="open" role="presentation">
      <div class="sheet-grip" aria-hidden="true"></div>
      <div class="search" *ngIf="options.length > 7">
        <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><circle cx="11" cy="11" r="7" /><path d="M20 20l-3.5-3.5" /></svg>
        <input #search type="text" [(ngModel)]="query" (ngModelChange)="active = 0" placeholder="Search application" aria-label="Search application" autocomplete="off" (keydown)="onListKey($event)" />
      </div>
      <ul class="list" role="listbox" [attr.aria-label]="label" tabindex="-1" #list (keydown)="onListKey($event)">
        <li
          *ngFor="let o of shown; let i = index"
          role="option"
          [attr.aria-selected]="o.id === value"
          [class.sel]="o.id === value"
          [class.act]="i === active"
          (mouseenter)="active = i"
          (click)="pick(o)"
        >
          <span class="name">{{ o.name }}</span>
          <svg *ngIf="o.id === value" class="tick" viewBox="0 0 24 24" width="17" height="17" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M5 12.5l4.5 4.5L19 7.5" /></svg>
        </li>
        <li class="none" *ngIf="!shown.length">No application matches "{{ query }}"</li>
      </ul>
      <button type="button" class="manage" *ngIf="canManage" (click)="manage.emit(); close()">
        <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="3" /><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z" /></svg>
        Manage paths
      </button>
    </div>
  `,
  styles: [
    `
      :host {
        position: relative;
        display: block;
        min-width: 0;
      }
      .trigger {
        width: 100%;
        height: 48px;
        display: flex;
        align-items: center;
        gap: 10px;
        padding: 0 14px;
        border-radius: 16px;
        color: var(--text);
        background: var(--input-bg);
        -webkit-backdrop-filter: var(--glass-blur);
        backdrop-filter: var(--glass-blur);
        border: 1.5px solid color-mix(in srgb, var(--accent) 45%, var(--border));
        box-shadow: 0 8px 20px -14px rgba(79, 91, 255, 0.7);
        font: inherit;
        text-align: left;
        cursor: pointer;
        transition: border-color 0.2s, box-shadow 0.2s;
      }
      .trigger:hover,
      .trigger.open {
        border-color: var(--accent);
      }
      .trigger:focus-visible,
      .trigger.open {
        outline: 0;
        box-shadow: 0 0 0 4px var(--accent-soft);
      }
      .ico {
        display: grid;
        color: var(--accent);
      }
      .val {
        flex: 1;
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
        font-family: var(--font-display);
        font-weight: 650;
        font-size: 15.5px;
      }
      .count {
        flex: none;
        min-width: 22px;
        height: 22px;
        padding: 0 7px;
        display: grid;
        place-items: center;
        border-radius: 999px;
        font-size: 11.5px;
        font-weight: 700;
        color: var(--accent);
        background: var(--accent-soft);
      }
      .caret {
        flex: none;
        color: var(--text-dim);
        transition: transform 0.2s;
      }
      .trigger.open .caret {
        transform: rotate(180deg);
      }

      .backdrop {
        position: fixed;
        inset: 0;
        z-index: 40;
        background: transparent;
      }
      .pop {
        position: absolute;
        z-index: 41;
        top: calc(100% + 8px);
        left: 0;
        right: 0;
        display: flex;
        flex-direction: column;
        padding: 8px;
        border-radius: 22px;
        background: var(--pop-bg);
        border: 1px solid var(--glass-border);
        -webkit-backdrop-filter: var(--glass-blur);
        backdrop-filter: var(--glass-blur);
        box-shadow: var(--shadow-lg), var(--glass-hi);
        animation: pop 0.14s ease-out;
      }
      @keyframes pop {
        from {
          opacity: 0;
          transform: translateY(-4px) scale(0.99);
        }
      }
      .sheet-grip {
        display: none;
      }
      .search {
        display: flex;
        align-items: center;
        gap: 8px;
        height: 40px;
        margin-bottom: 6px;
        padding: 0 12px;
        border-radius: 12px;
        color: var(--text-dim);
        background: var(--input-bg);
        border: 1px solid var(--border);
      }
      .search:focus-within {
        border-color: var(--accent);
        box-shadow: 0 0 0 3px var(--accent-soft);
      }
      .search input {
        flex: 1;
        min-width: 0;
        border: 0;
        outline: 0;
        background: transparent;
        color: var(--text);
        font: inherit;
        font-size: 14px;
      }
      .list {
        margin: 0;
        padding: 0;
        list-style: none;
        max-height: min(380px, 55vh);
        overflow-y: auto;
        overscroll-behavior: contain;
        outline: 0;
      }
      .list li {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 10px;
        min-height: 42px;
        padding: 0 12px;
        border-radius: 11px;
        font-size: 14.5px;
        cursor: pointer;
      }
      .list li.act {
        background: var(--row-hover);
      }
      .list li.sel {
        font-weight: 700;
        color: var(--accent);
        background: var(--accent-soft);
      }
      .list li.none {
        justify-content: center;
        color: var(--text-dim);
        cursor: default;
      }
      .name {
        min-width: 0;
        line-height: 1.6;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .manage {
        display: flex;
        align-items: center;
        justify-content: center;
        gap: 8px;
        height: 40px;
        margin-top: 6px;
        border: 0;
        border-top: 1px solid var(--border);
        border-radius: 0 0 11px 11px;
        background: transparent;
        color: var(--text-dim);
        font: inherit;
        font-size: 13.5px;
        font-weight: 600;
        cursor: pointer;
      }
      .manage:hover {
        color: var(--accent);
      }

      /* phone: bottom sheet */
      @media (max-width: 560px) {
        .backdrop {
          background: rgba(7, 11, 24, 0.45);
        }
        .pop {
          position: fixed;
          top: auto;
          left: 0;
          right: 0;
          bottom: 0;
          padding: 8px 12px calc(12px + env(safe-area-inset-bottom));
          border-radius: 22px 22px 0 0;
          animation: sheet 0.2s ease-out;
        }
        .sheet-grip {
          display: block;
          width: 40px;
          height: 4px;
          margin: 2px auto 10px;
          border-radius: 4px;
          background: var(--border-strong);
        }
        .list {
          max-height: 55vh;
        }
        .list li {
          min-height: 48px;
          font-size: 16px;
        }
        .search input {
          font-size: 16px;
        }
      }
      @keyframes sheet {
        from {
          transform: translateY(40px);
          opacity: 0;
        }
      }
      @media (prefers-reduced-motion: reduce) {
        .pop {
          animation: none;
        }
      }
    `,
  ],
})
export class AppSelectComponent {
  @Input() options: SelectOption[] = [];
  @Input() value = '';
  @Input() label = 'Application';
  /** show the "Manage paths" entry at the bottom of the list */
  @Input() canManage = false;
  @Output() valueChange = new EventEmitter<string>();
  @Output() manage = new EventEmitter<void>();

  @ViewChild('trigger') trigger?: ElementRef<HTMLButtonElement>;
  @ViewChild('search') searchBox?: ElementRef<HTMLInputElement>;
  @ViewChild('list') listEl?: ElementRef<HTMLUListElement>;

  open = false;
  query = '';
  active = 0;

  constructor(private host: ElementRef<HTMLElement>) {}

  get selectedName(): string {
    return this.options.find((o) => o.id === this.value)?.name ?? 'Select application';
  }

  get shown(): SelectOption[] {
    const q = this.query.trim().toLowerCase();
    return q ? this.options.filter((o) => o.name.toLowerCase().includes(q)) : this.options;
  }

  toggle(): void {
    this.open ? this.close() : this.show();
  }

  show(): void {
    this.query = '';
    this.active = Math.max(0, this.options.findIndex((o) => o.id === this.value));
    this.open = true;
    setTimeout(() => {
      if (this.searchBox) this.searchBox.nativeElement.focus();
      else this.listEl?.nativeElement.focus();
      this.scrollToActive();
    });
  }

  close(focusTrigger = false): void {
    this.open = false;
    if (focusTrigger) this.trigger?.nativeElement.focus();
  }

  pick(o: SelectOption): void {
    this.close(true);
    if (o.id !== this.value) this.valueChange.emit(o.id);
  }

  onTriggerKey(e: KeyboardEvent): void {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      this.show();
    }
  }

  onListKey(e: KeyboardEvent): void {
    const n = this.shown.length;
    switch (e.key) {
      case 'ArrowDown':
        e.preventDefault();
        this.active = n ? (this.active + 1) % n : 0;
        this.scrollToActive();
        break;
      case 'ArrowUp':
        e.preventDefault();
        this.active = n ? (this.active - 1 + n) % n : 0;
        this.scrollToActive();
        break;
      case 'Home':
        if (!this.searchBox) {
          e.preventDefault();
          this.active = 0;
          this.scrollToActive();
        }
        break;
      case 'End':
        if (!this.searchBox) {
          e.preventDefault();
          this.active = Math.max(0, n - 1);
          this.scrollToActive();
        }
        break;
      case 'Enter':
        e.preventDefault();
        if (this.shown[this.active]) this.pick(this.shown[this.active]);
        break;
      case 'Escape':
        e.preventDefault();
        this.close(true);
        break;
      case 'Tab':
        this.close();
        break;
    }
  }

  @HostListener('document:keydown.escape')
  onEsc(): void {
    if (this.open) this.close(true);
  }

  private scrollToActive(): void {
    setTimeout(() => {
      const li = this.listEl?.nativeElement.children[this.active] as HTMLElement | undefined;
      li?.scrollIntoView({ block: 'nearest' });
    });
  }
}
