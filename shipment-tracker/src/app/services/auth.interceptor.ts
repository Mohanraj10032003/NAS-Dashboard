import { HttpErrorResponse, HttpEvent, HttpHandler, HttpInterceptor, HttpRequest } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, Observable, throwError } from 'rxjs';
import { APP_CONFIG } from '../config';
import { clearSession, getToken } from './session-store';

/** Adds the login token to every API call, and sends the user back to the login page when the token is rejected. */
@Injectable()
export class AuthInterceptor implements HttpInterceptor {
  constructor(private router: Router) {}

  intercept(req: HttpRequest<unknown>, next: HttpHandler): Observable<HttpEvent<unknown>> {
    const apiPrefix = `${APP_CONFIG.apiBaseUrl}/api`;
    if (!req.url.startsWith(apiPrefix)) return next.handle(req);

    const token = getToken();
    const authReq = token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;

    return next.handle(authReq).pipe(
      catchError((err: unknown) => {
        const isLogin = req.url.startsWith(`${apiPrefix}/auth/login`);
        if (err instanceof HttpErrorResponse && err.status === 401 && !isLogin) {
          clearSession();
          this.router.navigate(['/login']);
        }
        return throwError(() => err);
      })
    );
  }
}
