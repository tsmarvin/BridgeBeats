using BridgeBeats.Web.Configuration;

namespace BridgeBeats.Web.Authentication;

internal sealed class AuthenticationOriginMiddleware(
    RequestDelegate next,
    AuthenticationOrigin? canonicalOrigin
) {
    public async Task InvokeAsync( HttpContext context ) {
        if (canonicalOrigin is null || IsCanonicalRequest( context.Request, canonicalOrigin )) {
            await next( context );
            return;
        }

        string path = context.Request.Path.Value ?? string.Empty;
        string routePath = path.EndsWith( "/", StringComparison.Ordinal ) ? path[..^1] : path;
        if (HttpMethods.IsGet( context.Request.Method ) || HttpMethods.IsHead( context.Request.Method )) {
            if (!IsLoginOrRegisterPath( routePath )) {
                await next( context );
                return;
            }

            string canonicalPath = routePath.Equals( "/account/login", StringComparison.OrdinalIgnoreCase )
                ? "/account/login" : "/account/register";
            UriBuilder target = new( canonicalOrigin.Origin ) {
                Path = path.EndsWith( "/", StringComparison.Ordinal ) ? canonicalPath + "/" : canonicalPath,
                Query = context.Request.QueryString.Value?.TrimStart( '?' ) ?? string.Empty
            };
            context.Response.Redirect( target.Uri.ToString( ) );
            return;
        }

        if (HttpMethods.IsPost( context.Request.Method ) && IsAuthenticationEntryPath( routePath )) {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync( new {
                message = "Sign-in requests must use the configured canonical host.",
                loginUrl = new Uri( canonicalOrigin.Origin, "/account/login" ).ToString( )
            }, context.RequestAborted );
            return;
        }

        await next( context );
    }

    private static bool IsCanonicalRequest( HttpRequest request, AuthenticationOrigin origin ) {
        // The proxy terminates TLS, so Request.Scheme is HTTP even for the public HTTPS origin.
        return request.Host.Host.Equals( origin.Origin.Host, StringComparison.OrdinalIgnoreCase ) &&
               (request.Host.Port ?? 443) == origin.Origin.Port;
    }

    private static bool IsAuthenticationEntryPath( string path ) =>
        path.Equals( "/account/login", StringComparison.OrdinalIgnoreCase ) ||
        path.Equals( "/account/register", StringComparison.OrdinalIgnoreCase ) ||
        path.Equals( "/account/login-atproto", StringComparison.OrdinalIgnoreCase );

    private static bool IsLoginOrRegisterPath( string path ) =>
        path.Equals( "/account/login", StringComparison.OrdinalIgnoreCase ) ||
        path.Equals( "/account/register", StringComparison.OrdinalIgnoreCase );
}

internal static class AuthenticationOriginMiddlewareExtensions {
    internal static IApplicationBuilder UseCanonicalAuthenticationOrigin(
        this IApplicationBuilder app,
        AuthenticationOrigin? canonicalOrigin
    ) => canonicalOrigin is null ? app : app.UseMiddleware<AuthenticationOriginMiddleware>( canonicalOrigin );
}
