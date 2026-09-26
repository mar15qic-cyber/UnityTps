using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>Player-avatar wrist calibration, expressed in this weapon's
    /// frame. Attachment socket rotation must never become wrist rotation.</summary>
    public sealed class TPGripPose : MonoBehaviour
    {
        [SerializeField] private Vector3 verticalWristFromGripCenter = new(-.0636f, .0062f, -.0809f);
        [SerializeField] private Quaternion verticalWristRotation = new(.07523985f, .6240461f, .7569626f, -.1786448f);

        public void CalibrateAttachmentTargets(WeaponAttachmentView attachments)
        {
            if (attachments == null) return;
            foreach (var spawned in attachments.Spawned)
            {
                if (spawned == null) continue;
                var marker = spawned.transform.Find("LeftHandGrip");
                if (marker == null) continue;
                Bounds bounds = default;
                bool any = false;
                foreach (var filter in spawned.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null) continue;
                    // Bounds are readable in player builds even when mesh CPU
                    // vertices are stripped. Evaluate once when mounting the grip.
                    var b = filter.sharedMesh.bounds;
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = b.center + Vector3.Scale(b.extents,
                            new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                        var point = transform.InverseTransformPoint(filter.transform.TransformPoint(corner));
                        if (!any) { bounds = new Bounds(point, Vector3.zero); any = true; }
                        else bounds.Encapsulate(point);
                    }
                }
                if (!any) continue;
                marker.position = transform.TransformPoint(bounds.center + verticalWristFromGripCenter);
                marker.rotation = transform.rotation * verticalWristRotation;
            }
        }
    }
}
