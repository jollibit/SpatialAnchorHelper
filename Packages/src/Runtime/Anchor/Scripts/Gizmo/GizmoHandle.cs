using System.Collections;
using UnityEngine;

namespace SAH
{
    public enum GizmoHandleAxis
    {
        X,
        Y,
        Z,
        ALL
    }

    public class GizmoHandle : MonoBehaviour
    {
        public virtual bool BeginGrab(Transform attachPoint) => false;
        public virtual void OnRelease() { }
    }
}
