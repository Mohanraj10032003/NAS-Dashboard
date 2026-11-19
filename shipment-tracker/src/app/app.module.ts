import { NgModule } from '@angular/core';
import { BrowserModule } from '@angular/platform-browser';
import { FormsModule } from '@angular/forms';
import { HTTP_INTERCEPTORS, HttpClientModule } from '@angular/common/http';

import { AppRoutingModule } from './app-routing.module';
import { AppComponent } from './app.component';
import { LoginComponent } from './components/login/login.component';
import { DashboardComponent } from './components/dashboard/dashboard.component';
import { SettingsComponent } from './components/settings/settings.component';
import { AppSelectComponent } from './components/app-select/app-select.component';
import { ThemeToggleComponent } from './components/theme-toggle/theme-toggle.component';
import { AuthInterceptor } from './services/auth.interceptor';

@NgModule({
  declarations: [AppComponent, LoginComponent, DashboardComponent, SettingsComponent, AppSelectComponent, ThemeToggleComponent],
  imports: [BrowserModule, FormsModule, HttpClientModule, AppRoutingModule],
  providers: [{ provide: HTTP_INTERCEPTORS, useClass: AuthInterceptor, multi: true }],
  bootstrap: [AppComponent],
})
export class AppModule {}
