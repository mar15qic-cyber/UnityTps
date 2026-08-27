using System;

namespace Game.Account
{

public sealed class AccountSession
{
    public string Token { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }
    public PlayerProfileDto Profile { get; private set; }
    public LoadoutDto Loadout { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(Token) && ExpiresAtUtc > DateTime.UtcNow;

    public event Action Changed;

    public void Apply(AuthSessionDto session)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        Token = session.token;
        ExpiresAtUtc = DateTime.TryParse(session.expiresAtUtc, out var expiry) ? expiry.ToUniversalTime() : DateTime.UtcNow.AddHours(12);
        Profile = session.profile;
        Loadout = session.loadout;
        Changed?.Invoke();
    }

    public void ApplyProfile(PlayerProfileDto profile)
    {
        Profile = profile;
        Changed?.Invoke();
    }

    public void ApplyLoadout(LoadoutDto loadout)
    {
        Loadout = loadout;
        Changed?.Invoke();
    }

    public void Clear()
    {
        Token = null;
        ExpiresAtUtc = null;
        Profile = null;
        Loadout = null;
        Changed?.Invoke();
    }
}
}
