import { Component } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '../../services/auth.service';
import { APP_CONFIG } from '../../config';

@Component({
  selector: 'app-login',
  templateUrl: './login.component.html',
  styleUrls: ['./login.component.scss'],
})
export class LoginComponent {
  appName = APP_CONFIG.appName;
  companyName = APP_CONFIG.companyName;
  showHint = APP_CONFIG.showLoginHint;
  username = '';
  password = '';
  showPassword = false;
  remember = true;
  loading = false;
  error = '';
  shake = false;

  /** Always the current year, so the footer rolls over on 1 January by itself. */
  get year(): number {
    return new Date().getFullYear();
  }

  constructor(private auth: AuthService, private router: Router) {
    if (this.auth.isLoggedIn) {
      this.router.navigate(['/dashboard']);
    }
  }

  async submit(): Promise<void> {
    this.error = '';
    if (!this.username.trim() || !this.password) {
      this.fail('Please enter your username and password.');
      return;
    }
    this.loading = true;
    await new Promise((r) => setTimeout(r, 400)); // small delay for a smooth feel
    const result = await this.auth.login(this.username.trim(), this.password, this.remember);
    this.loading = false;
    if (result.ok) {
      this.router.navigate(['/dashboard']);
    } else {
      this.fail(result.message);
    }
  }

  private fail(msg: string): void {
    this.error = msg;
    this.shake = true;
    setTimeout(() => (this.shake = false), 500);
  }
}
