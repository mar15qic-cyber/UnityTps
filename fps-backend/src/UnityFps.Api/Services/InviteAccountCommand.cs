using Microsoft.EntityFrameworkCore;
using UnityFps.Api.Data;
using UnityFps.Api.Features;

namespace UnityFps.Api.Services;

/// <summary>Offline administrative command. Never exposed as an HTTP endpoint.</summary>
public static class InviteAccountCommand
{
    public static async Task ExecuteAsync(IServiceProvider services, IConfiguration config)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var name = config["InviteAdmin:Username"] ?? throw new InvalidOperationException("InviteAdmin:Username required");
        switch (config["InviteAdmin:Action"])
        {
            case "create":
                // Password comes from environment/stdin wrapper, never from command-line arguments or output.
                var password = Environment.GetEnvironmentVariable("InviteAdmin__Password");
                if (string.IsNullOrEmpty(password) || password.Length < 12)
                    throw new InvalidOperationException("InviteAdmin__Password must contain at least 12 characters");
                await scope.ServiceProvider.GetRequiredService<AuthService>()
                    .CreateInvitedAsync(new RegisterRequest { Username = name, Password = password }, CancellationToken.None);
                break;
            case "disable":
                var user = await db.Users.SingleAsync(u => u.NormalizedUsername == AuthService.Normalize(name));
                user.Disabled = true;
                user.TokenVersion++;
                await db.SaveChangesAsync();
                break;
            default: throw new InvalidOperationException("InviteAdmin:Action must be create or disable");
        }
        Console.WriteLine("INVITE_ACCOUNT_UPDATED");
    }
}
