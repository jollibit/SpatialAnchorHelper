using System.Collections;
using System.Linq;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using UnityEngine;

namespace SAH
{
    public class GizmoTranslationHandle : GizmoHandle
    {
        public Gizmo gizmo;
        private bool _isActive = false;

        [SerializeField] private GizmoHandleAxis axis;

        [SerializeField] private HandGrabInteractable _handGrabInteractable;
        public Transform _io;
        private Vector3 lastHandPos;


        void Update()
        {
            if (!_isActive || _io == null || gizmo.target == null) return;

            Vector3 delta = _io.position - lastHandPos;

            Vector3 localDelta = Vector3.zero;
            switch (axis)
            {
                case GizmoHandleAxis.X:
                    localDelta = Vector3.Project(delta, gizmo.target.right);
                    break;
                case GizmoHandleAxis.Y:
                    localDelta = Vector3.Project(delta, gizmo.target.up);
                    break;
                case GizmoHandleAxis.Z:
                    localDelta = Vector3.Project(delta, gizmo.target.forward);
                    break;
            }

            gizmo.TranslateTarget(localDelta);

            lastHandPos = _io.position;
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
            lastHandPos = _io.position;
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
