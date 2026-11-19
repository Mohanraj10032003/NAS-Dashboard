import { Component } from '@angular/core';
import { ThemeService } from './services/theme.service';

@Component({
  selector: 'app-root',
  template: '<router-outlet></router-outlet>',
})
export class AppComponent {
  // Injecting ThemeService here makes sure the saved theme is applied on every page.
  constructor(_theme: ThemeService) {}
}
