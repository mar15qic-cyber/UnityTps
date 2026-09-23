using System;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>Channel map artwork and its original orthographic projection, shared with radar.</summary>
    public sealed class MapRadarCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public string sceneName;
            public Sprite image;
            public Vector3 cameraPosition;
            public Quaternion cameraRotation;
            public float halfHeight;
            public Vector2 WorldToUv(Vector3 world)
            {
                var p = Quaternion.Inverse(cameraRotation) * (world - cameraPosition);
                float aspect = image.rect.width / image.rect.height;
                return new Vector2(.5f + p.x / (2f * halfHeight * aspect), .5f + p.y / (2f * halfHeight));
            }
        }
        public Entry[] entries = Array.Empty<Entry>();
        public Entry Find(string scene) => Array.Find(entries, e => e.sceneName == scene);
    }
}
