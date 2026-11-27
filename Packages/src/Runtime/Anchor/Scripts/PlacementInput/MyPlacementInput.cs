using UnityEngine;
using UnityEngine.InputSystem;

namespace SAH
{
    public class MyPlacementInput : MonoBehaviour, ISpatialAnchorPlacementInput
    {
        [SerializeField] private Transform rightHand;
        [SerializeField] private InputActionReference selectAction;

        public bool TryGetPose(out Pose pose)
        {
            pose = default;
            if (rightHand == null || !rightHand.gameObject.activeInHierarchy)
                return false;

            pose = new Pose(rightHand.position, rightHand.rotation);
            return true;
        }

        public bool WasConfirmedThisFrame()
        {
            return selectAction != null && selectAction.action?.WasPressedThisFrame() == true;
        }
    }
}
