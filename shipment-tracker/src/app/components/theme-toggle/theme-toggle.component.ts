import { Component } from '@angular/core';
import { ThemeService } from '../../services/theme.service';

@Component({
  selector: 'app-theme-toggle',
  template: `
    <button
      class="toggle"
      type="button"
      (click)="theme.toggle()"
      [attr.aria-label]="theme.theme === 'dark' ? 'Switch to day theme' : 'Switch to night theme'"
      [title]="theme.theme === 'dark' ? 'Switch to day theme' : 'Switch to night theme'"
    >
      <span class="track" [class.night]="theme.theme === 'dark'">
        <span class="thumb">
          <!-- sun -->
          <svg class="sun" viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
            <circle cx="12" cy="12" r="4" />
            <path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" />
          </svg>
          <!-- moon -->
          <svg class="moon" viewBox="0 0 24 24" width="16" height="16" fill="currentColor">
            <path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z" />
          </svg>
        </span>
      </span>
    </button>
  `,
  styles: [
    `
      .toggle {
        background: none;
        border: 0;
        padding: 0;
        cursor: pointer;
        display: inline-flex;
      }
      .track {
        position: relative;
        width: 58px;
        height: 30px;
        border-radius: 999px;
        background: linear-gradient(135deg, #fde68a, #fbbf24);
        box-shadow: inset 0 1px 3px rgba(0, 0, 0, 0.15), 0 2px 8px rgba(0, 0, 0, 0.1);
        transition: background 0.4s ease;
      }
      .track.night {
        background: linear-gradient(135deg, #312e81, #1e1b4b);
      }
      .thumb {
        position: absolute;
        top: 3px;
        left: 3px;
        width: 24px;
        height: 24px;
        border-radius: 50%;
        background: #fff;
        color: #f59e0b;
        display: grid;
        place-items: center;
        box-shadow: 0 2px 6px rgba(0, 0, 0, 0.25);
        transition: transform 0.35s cubic-bezier(0.5, 1.6, 0.4, 1), color 0.3s;
      }
      .track.night .thumb {
        transform: translateX(28px);
        color: #6366f1;
      }
      .sun,
      .moon {
        position: absolute;
        transition: opacity 0.25s, transform 0.35s;
      }
      .moon {
        opacity: 0;
        transform: rotate(-40deg) scale(0.6);
      }
      .track.night .sun {
        opacity: 0;
        transform: rotate(60deg) scale(0.6);
      }
      .track.night .moon {
        opacity: 1;
        transform: none;
      }
      .toggle:focus-visible .track {
        outline: 2px solid var(--accent);
        outline-offset: 3px;
      }
    `,
  ],
})
export class ThemeToggleComponent {
  constructor(public theme: ThemeService) {}
}
