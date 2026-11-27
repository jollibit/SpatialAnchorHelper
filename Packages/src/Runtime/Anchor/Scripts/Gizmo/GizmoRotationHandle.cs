using System.Collections;
using System.Linq;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using UnityEngine;

namespace SAH
{
    public class GizmoRotationHandle : GizmoHandle
    {
        public Gizmo gizmo;
        private bool _isActive = false;

        [SerializeField] private GizmoHandleAxis axis;

        [SerializeField] private HandGrabInteractable _handGrabInteractable;
        public Transform _io;
        private Vector3 lastDir;


        void Update()
        {
            if (!_isActive || _io == null || gizmo.target == null) return;

            Vector3 handPos = _io.position;
            Vector3 targetPos = gizmo.target.position;

            Vector3 currentDir = handPos - targetPos;

            if (currentDir.sqrMagnitude < 0.0001f) return;

            Quaternion deltaRot = Quaternion.FromToRotation(lastDir, currentDir);

            Vector3 worldAxis = Vector3.zero;
            switch (axis)
            {
                case GizmoHandleAxis.X: worldAxis = gizmo.transform.right; break;
                case GizmoHandleAxis.Y: worldAxis = gizmo.transform.up; break;
                case GizmoHandleAxis.Z: worldAxis = gizmo.transform.forward; break;
            }

            deltaRot.ToAngleAxis(out float angle, out Vector3 rotAxis);
            float projectedAngle = Vector3.Dot(rotAxis, worldAxis) * angle;

            Quaternion rotation = Quaternion.AngleAxis(projectedAngle, worldAxis);

            gizmo.RotateTarget(rotation);

            lastDir = currentDir.normalized;
        }

        public void OnGrab()
        {
            BeginGrab(_handGrabInteractable?.SelectingInteractors.FirstOrDefault()?.WristPoint);
        }

        public override bool BeginGrab(Transform attachPoint)
        {
            if (attachPoint == null || gizmo == null || gizmo.target == null
                || !gizmo.TryActivateHandle(this))
                return false;

            _io = attachPoint;
            lastDir = (_io.position - gizmo.target.position).normalized;
            _isActive = true;
            return true;
        }

        public override void OnRelease()
        {
            if (!_isActive) return;
            _isActive = false;
        }
    }
}
