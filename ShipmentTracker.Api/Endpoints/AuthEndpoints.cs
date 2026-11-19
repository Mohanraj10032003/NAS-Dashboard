using System.Security.Claims;
using ShipmentTracker.Api.Services.Auth;

namespace ShipmentTracker.Api.Endpoints;

public sealed record LoginRequest(string? Username, string? Password);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/auth");

        group.MapPost("/login", async (LoginRequest request, UserStore users, JwtTokenService tokens) =>
        {
            var username = request.Username?.Trim() ?? "";
            var password = request.Password ?? "";
            if (username.Length == 0 || password.Length == 0)
                return Results.Json(new { message = "Enter your username and password." }, statusCode: 400);

            if (users.IsLocked(username))
                return Results.Json(new { message = "Too many wrong attempts. Try again in a few minutes." }, statusCode: 429);

            var user = users.Validate(username, password);
            if (user is null)
            {
                await Task.Delay(400);   // small delay makes password guessing slower
                return Results.Json(new { message = "Invalid username or password." }, statusCode: 401);
            }

            var (token, expires) = tokens.Create(user);
            return Results.Ok(new
            {
                token,
                expiresAtUtc = expires,
                user = new { username = user.Username, name = user.Name, role = user.Role }
            });
        }).AllowAnonymous();

        group.MapGet("/me", (ClaimsPrincipal principal) => Results.Ok(new
        {
            username = principal.FindFirstValue(ClaimTypes.NameIdentifier),
            name = principal.FindFirstValue(ClaimTypes.Name),
            role = principal.FindFirstValue(ClaimTypes.Role)
        })).RequireAuthorization();
    }
}
