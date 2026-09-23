using System.Reflection;
using UnityFps.Api.Data;
using UnityFps.Api.Services;
using Xunit;

namespace UnityFps.Api.Tests;

public sealed class PlaytestAttachmentPolicyTests
{
    [Fact]
    public void NativePistolsHaveExactlyTheTwoApprovedOpticsAndMp5HasNoGrip()
    {
        var rows = ((IEnumerable<AttachmentCompat>)typeof(AttachmentSystemSeeder)
            .GetMethod("BuildMatrix", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!).ToArray();
        foreach (var id in new[] { "weapon.service_pistol", "weapon.handgun02", "weapon.handgun03", "weapon.handgun04" })
            Assert.Equal(new[] { "attach.lpfp.optic.01", "attach.lpfp.optic.03" }, rows
                .Where(r => r.WeaponItemId == id && r.SlotType == "Optic").Select(r => r.AttachmentItemId).Order().ToArray());
        Assert.DoesNotContain(rows, r => r.WeaponItemId == "weapon.smg05" && r.SlotType == "Underbarrel");
    }
}
