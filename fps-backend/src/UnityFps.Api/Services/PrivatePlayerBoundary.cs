using System.Net;

namespace UnityFps.Api.Services;

public static class PrivatePlayerBoundary
{
    public static bool Allows(PathString path) =>
        path.Equals("/health") || path.StartsWithSegments("/hotupdate") ||
        path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/server-instances");

    public static void Configure(WebApplicationBuilder builder)
    {
        var address = builder.Configuration["PrivateTest:Address"];
        if (string.IsNullOrWhiteSpace(address)) return;
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidOperationException("PrivateTest address must be a private IPv4 address.");
        var b = ip.GetAddressBytes();
        if (!(b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168))
            throw new InvalidOperationException("PrivateTest address must be private.");
        var port = builder.Configuration.GetValue("PrivateTest:Port", 5081);
        var internalPort = builder.Configuration.GetValue("PrivateTest:InternalPort", 5080);
        if (port == internalPort || port < 1024 || port > 65535) throw new InvalidOperationException("Invalid private listener port.");
        builder.WebHost.ConfigureKestrel(o => { o.Listen(IPAddress.Loopback, internalPort); o.Listen(ip, port); });
    }

    public static void Use(WebApplication app)
    {
        if (string.IsNullOrWhiteSpace(app.Configuration["PrivateTest:Address"])) return;
        var port = app.Configuration.GetValue("PrivateTest:Port", 5081);
        app.Use(async (context, next) =>
        {
            if (context.Connection.LocalPort == port && !Allows(context.Request.Path))
            { context.Response.StatusCode = 404; return; }
            await next(context);
        });
    }
}
