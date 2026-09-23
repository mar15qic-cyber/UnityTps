using System.Collections.Generic;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Tests.EditMode
{
    public sealed class AttachmentStoreCleanupTests
    {
        private const string WeaponId = "weapon.stagea.store-cleanup";
        private const string LoadoutPrefix = "Game.Loadout.Attachments.";
        private const string DraftPrefix = "Game.GunsmithDraft.";

        [SetUp]
        public void SetUpPrefs() => ClearPrefs();

        [TearDown]
        public void TearDownPrefs() => ClearPrefs();

        private static void ClearPrefs()
        {
            foreach (var slot in WeaponAttachmentStore.AllSlots)
                PlayerPrefs.DeleteKey(LoadoutPrefix + WeaponId + "." + slot);
            PlayerPrefs.DeleteKey(DraftPrefix + WeaponId);
            PlayerPrefs.Save();
        }

        [Test]
        public void WeaponAttachmentStore_Load_RemovesRetiredOpticId()
        {
            PlayerPrefs.SetString(LoadoutPrefix + WeaponId + ".Optic", "attach.lpw.optic.01");
            PlayerPrefs.SetString(LoadoutPrefix + WeaponId + ".Muzzle", "attach.lpfp.muffler.01");
            PlayerPrefs.Save();

            var loaded = new Dictionary<string, string>();
            WeaponAttachmentStore.Load(WeaponId, loaded);

            Assert.That(loaded.ContainsKey("Optic"), Is.False);
            Assert.That(loaded["Muzzle"], Is.EqualTo("attach.lpfp.muffler.01"));
            Assert.That(PlayerPrefs.HasKey(LoadoutPrefix + WeaponId + ".Optic"), Is.False);
        }

        [TestCase("attach.lpw.muffler.01")]
        [TestCase("attach.lpw.muffler.02")]
        public void WeaponAttachmentStore_Load_RemovesRetiredSilencer(string id)
        {
            PlayerPrefs.SetString(LoadoutPrefix + WeaponId + ".Muzzle", id);
            var loaded = new Dictionary<string, string>();
            WeaponAttachmentStore.Load(WeaponId, loaded);
            Assert.That(loaded.ContainsKey("Muzzle"), Is.False);
            Assert.That(PlayerPrefs.HasKey(LoadoutPrefix + WeaponId + ".Muzzle"), Is.False);
        }

        [Test]
        public void GunsmithDraftStore_TryLoad_RemovesRetiredOpticIdAndPersistsRepair()
        {
            PlayerPrefs.SetString(DraftPrefix + WeaponId,
                "{\"Optic\":\"attach.pistol.optic\",\"Muzzle\":\"attach.lpfp.muffler.01\"}");
            PlayerPrefs.Save();

            var loaded = new Dictionary<string, string>();
            var found = GunsmithDraftStore.TryLoad(WeaponId, loaded);

            Assert.That(found, Is.True);
            Assert.That(loaded.ContainsKey("Optic"), Is.False);
            Assert.That(loaded["Muzzle"], Is.EqualTo("attach.lpfp.muffler.01"));
            Assert.That(PlayerPrefs.GetString(DraftPrefix + WeaponId), Does.Not.Contain("attach.pistol.optic"));
        }
    }
}
