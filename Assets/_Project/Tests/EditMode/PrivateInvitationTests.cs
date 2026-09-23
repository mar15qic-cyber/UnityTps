using Game.Core;
using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    public sealed class PrivateInvitationTests
    {
        private static ClientReleaseEnvironment Config() => new ClientReleaseEnvironment { environmentId="private",releaseId="release-1",inviteOnly=true,
            networkMode="private-overlay",zeroTierNetworkId="743993800fd77ce1",hostOverlayAddress="10.16.41.231",apiBaseUrl="http://10.16.41.231:5081",hotUpdateBaseUrl="http://10.16.41.231:5081/hotupdate" };
        [Test] public void PrivateEndpointsMustMatchHost() { var c=Config(); Assert.IsTrue(c.TryValidate(out _)); c.apiBaseUrl="http://127.0.0.1:5080"; Assert.IsFalse(c.TryValidate(out _)); }
        [Test] public void PublicModeCannotUsePrivateHttp() { var c=Config(); c.networkMode="public"; Assert.IsFalse(c.TryValidate(out _)); }
        [Test] public void PrivateModeStillRequiresInvitation() { var c=Config();c.inviteOnly=false;Assert.IsFalse(c.TryValidate(out _)); }
        [TestCase("8.141.92.231")][TestCase("127.0.0.1")][TestCase("0.0.0.0")][TestCase("::1")]
        public void RejectNonPrivateHost(string host) { Assert.IsFalse(ClientReleaseEnvironment.IsPrivateIPv4(host)); }
        [Test] public void ContentMismatchIsRejected() { Assert.IsFalse(MapContentIdentity.Matches("abc","")); Assert.IsFalse(MapContentIdentity.Matches("abc","def")); Assert.IsTrue(MapContentIdentity.Matches("abc","ABC")); }
        [Test] public async System.Threading.Tasks.Task CancelledInstallCannotReplacePointer()
        {
            var root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"private-map-test-"+System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(root);
            try
            {
                var pointer=System.IO.Path.Combine(root,"installed.json");System.IO.File.WriteAllText(pointer,"old-pointer");
                var cancellation=new System.Threading.CancellationToken(true);
                var result=await Game.UI.HotUpdateInstaller.InstallAsync(null,"new-pointer",root,null,null,
                    _=>System.Threading.Tasks.Task.FromResult(new byte[0]),cancellation);
                Assert.IsFalse(result.Success);Assert.AreEqual("old-pointer",System.IO.File.ReadAllText(pointer));
            }
            finally {System.IO.Directory.Delete(root,true);}
        }
        [Test] public void WindowsProtectedSessionIsNotPlaintextAndRoundTrips()
        {
            var method=typeof(Game.Account.RememberedSession).GetMethod("Transform",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
            var plain=System.Text.Encoding.UTF8.GetBytes("test-session-token");
            var protectedBytes=(byte[])method.Invoke(null,new object[]{plain,true});
            Assert.AreNotEqual("test-session-token",System.Text.Encoding.UTF8.GetString(protectedBytes));
            var decoded=(byte[])method.Invoke(null,new object[]{protectedBytes,false});
            Assert.AreEqual("test-session-token",System.Text.Encoding.UTF8.GetString(decoded));
        }
    }
}
