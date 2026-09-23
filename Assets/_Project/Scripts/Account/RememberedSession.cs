using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Game.Account
{
    /// <summary>Current-Windows-user DPAPI. Never persists passwords; fail closed on other platforms.</summary>
    public static class RememberedSession
    {
        [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
        [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr ptr);
        private static string PathName => Path.Combine(Application.persistentDataPath, "Sessions", (Game.Core.ClientReleaseEnvironment.Current?.environmentId ?? "local") + ".bin");
        public static void Clear() { try { if (File.Exists(PathName)) File.Delete(PathName); } catch (IOException) { } }
        public static bool Save(AuthSessionDto session)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathName));
                var bytes = Transform(Encoding.UTF8.GetBytes(JsonUtility.ToJson(session)), true);
                File.WriteAllBytes(PathName + ".tmp", bytes);
                if (File.Exists(PathName)) File.Replace(PathName + ".tmp", PathName, null);
                else File.Move(PathName + ".tmp", PathName);
                return true;
            }
            catch { Clear(); return false; }
        }
        public static AuthSessionDto Load()
        {
            try
            {
                if (!File.Exists(PathName)) return null;
                var session = JsonUtility.FromJson<AuthSessionDto>(Encoding.UTF8.GetString(Transform(File.ReadAllBytes(PathName), false)));
                if (session == null || string.IsNullOrEmpty(session.token) || !DateTime.TryParse(session.expiresAtUtc, out var expiry) || expiry.ToUniversalTime() <= DateTime.UtcNow)
                { Clear(); return null; }
                return session;
            }
            catch { Clear(); return null; }
        }
        private static byte[] Transform(byte[] value, bool protect)
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor) throw new PlatformNotSupportedException();
            var input = new Blob { Size = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
            Blob output = default;
            try
            {
                Marshal.Copy(value, 0, input.Data, value.Length);
                var ok = protect ? CryptProtectData(ref input, "UnityFps session", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
                if (!ok) throw new InvalidOperationException("SESSION_PROTECTION_FAILED");
                var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
            }
            finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); Array.Clear(value, 0, value.Length); }
        }
    }
}
