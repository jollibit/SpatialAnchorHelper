using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace SAH
{
    public sealed class FoundationGizmoInput : MonoBehaviour
    {
        private GizmoHandle _heldHandle;

        private void Awake()
        {
            foreach (GizmoHandle handle in GetComponentsInChildren<GizmoHandle>())
            {
                var interactable = handle.gameObject.AddComponent<XRSimpleInteractable>();
                interactable.selectEntered.AddListener(args => BeginGrab(handle, args));
                interactable.selectExited.AddListener(_ => Release(handle));
            }
        }

        private void BeginGrab(GizmoHandle handle, SelectEnterEventArgs args)
        {
            if (!isActiveAndEnabled || _heldHandle != null)
                return;

            Transform attachPoint = args.interactorObject.GetAttachTransform(args.interactableObject);
            if (handle.BeginGrab(attachPoint))
                _heldHandle = handle;
        }

        private void Release(GizmoHandle handle)
        {
            if (_heldHandle != handle)
                return;

            handle.OnRelease();
            _heldHandle = null;
        }

        private void OnDisable()
        {
            if (_heldHandle != null)
                Release(_heldHandle);
        }
    }
}
