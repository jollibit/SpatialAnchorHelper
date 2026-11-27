using UnityEngine;
using UnityEngine.InputSystem;

public sealed class XRMenuShell : MonoBehaviour
{
    [SerializeField] private Canvas canvas;
    [SerializeField] private float distance = 0.8f;
    [SerializeField] private InputActionReference toggleAction;

    private void Start()
    {
        Recenter();
    }

    private void Update()
    {
        if (toggleAction != null && toggleAction.action.WasPressedThisFrame())
            Toggle();
    }

    public void Toggle()
    {
        bool show = !canvas.gameObject.activeSelf;
        if (show)
            Recenter();

        canvas.gameObject.SetActive(show);
    }

    public void Recenter()
    {
        return;
        Camera camera = Camera.main;
        if (camera == null)
            return;

        canvas.worldCamera = camera;
        Transform view = camera.transform;
        transform.SetPositionAndRotation(
            view.position + view.forward * distance,
            Quaternion.LookRotation(view.forward));
    }
}
