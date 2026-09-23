using UnityFps.Api.Features;
using UnityFps.Api.Services;
using Xunit;

namespace UnityFps.Api.Tests;
public sealed class PrivateTestBoundaryTests
{
    [Fact] public async Task MaintenanceCannotBeUndoneByAnOldHeartbeat()
    {
        using var factory = new ServerApiFactory();
        using var client = factory.CreateClient();
        const string id = "private-maintenance-test";
        var register = await ServerTest.SendWithKeyAsync(client, System.Net.Http.HttpMethod.Post, "/api/server-instances/register",
            ServerTest.ServerKey, new { instanceId=id,address="10.16.41.231",port=7775,capacity=8 });
        Assert.True(register.IsSuccessStatusCode);
        var maintenance = await ServerTest.SendWithKeyAsync(client, System.Net.Http.HttpMethod.Post, "/api/server-instances/"+id+"/maintenance", ServerTest.ServerKey, new {});
        Assert.True(maintenance.IsSuccessStatusCode);
        var heartbeat = await ServerTest.SendWithKeyAsync(client, System.Net.Http.HttpMethod.Post, "/api/server-instances/"+id+"/heartbeat", ServerTest.ServerKey, new {state="Ready",currentPlayers=0});
        Assert.Equal(System.Net.HttpStatusCode.Conflict,heartbeat.StatusCode);
    }
    [Theory]
    [InlineData("/api/server-instances",false)] [InlineData("/api/server-instances/pool",false)]
    [InlineData("/swagger",false)] [InlineData("/health",true)] [InlineData("/api/auth/login",true)]
    [InlineData("/hotupdate/1/maps/a.bundle",true)]
    public void PlayerBoundary(string path,bool allowed) => Assert.Equal(allowed,PrivatePlayerBoundary.Allows(path));
    [Fact] public void RejectDuplicatePublishedMaps()
    {
        var map=new MapCatalogDto("test","Test","Test",["TDM"],8,["Red","Blue","FFA"],"1",new string('a',64));
        Assert.Throws<InvalidDataException>(()=>PublishedMapCatalog.Validate([map,map]));
    }
}
