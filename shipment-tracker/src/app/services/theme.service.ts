import { Injectable } from '@angular/core';
import { BehaviorSubject } from 'rxjs';

export type Theme = 'light' | 'dark';
const KEY = 'st-theme';

@Injectable({ providedIn: 'root' })
export class ThemeService {
  private subject = new BehaviorSubject<Theme>(this.initial());
  theme$ = this.subject.asObservable();

  constructor() {
    this.apply(this.subject.value);
  }

  get theme(): Theme {
    return this.subject.value;
  }

  toggle(): void {
    this.set(this.theme === 'dark' ? 'light' : 'dark');
  }

  set(t: Theme): void {
    this.subject.next(t);
    this.apply(t);
    try {
      localStorage.setItem(KEY, t);
    } catch {
      /* ignore */
    }
  }

  private initial(): Theme {
    try {
      const saved = localStorage.getItem(KEY) as Theme | null;
      if (saved === 'light' || saved === 'dark') return saved;
    } catch {
      /* ignore */
    }
    return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }

  private apply(t: Theme): void {
    document.documentElement.setAttribute('data-theme', t);
  }
}
