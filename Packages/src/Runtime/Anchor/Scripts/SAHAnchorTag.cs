using UnityEngine;

namespace SAH
{
    [DisallowMultipleComponent]
    public sealed class SAHAnchorTag : MonoBehaviour
    {
        public string ID;
        public SAHAnchor Anchor { get; private set; }

        internal void Initialize(SAHAnchor anchor)
        {
            Anchor = anchor;
            ID = anchor.Id;
            Debug.Log("TEST: " + anchor.Id + " " + ID);
        }
    }
}