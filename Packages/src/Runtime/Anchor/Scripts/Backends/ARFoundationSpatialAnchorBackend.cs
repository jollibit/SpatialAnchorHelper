using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SAH
{
    public sealed class ARFoundationSpatialAnchorBackend : ISpatialAnchorBackend
    {
        private ARAnchorManager _anchorManager;
        private readonly List<ARAnchor> _activeAnchors = new List<ARAnchor>();

        public bool IsTrackingReady
        {
            get
            {
                if (!HasRunningManager())
                    return false;

                InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
                return head.isValid
                    ? head.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && tracked
                    : ARSession.state == ARSessionState.SessionTracking;
            }
        }

        public bool SupportsPersistence => HasRunningManager()
            && _anchorManager.descriptor.supportsSaveAnchor
            && _anchorManager.descriptor.supportsLoadAnchor;

        public async Task<Guid?> CreateSystemAnchor(Pose pose)
        {
            if (!SupportsPersistence)
                return null;

            ARAnchorManager manager = _anchorManager;
            var result = await manager.TryAddAnchorAsync(pose);
            if (!result.status.IsSuccess())
                return null;

            ARAnchor anchor = result.value;
            try
            {
                HideSystemAnchor(anchor);
                return await SaveAnchor(anchor);
            }
            finally
            {
                _ = ReleaseSystemAnchor(manager, anchor);
            }
        }

        public async Task<Pose?> LocateSystemAnchor(Guid id, CancellationToken cancellationToken)
        {
            if (!SupportsPersistence)
                return null;

            ARAnchorManager manager = _anchorManager;
            var result = await manager.TryLoadAnchorAsync(new SerializableGuid(id), cancellationToken);
            if (!result.status.IsSuccess())
                return null;

            ARAnchor anchor = result.value;
            try
            {
                HideSystemAnchor(anchor);
                while (anchor != null && !cancellationToken.IsCancellationRequested)
                {
                    if (!anchor.pending && anchor.trackingState == TrackingState.Tracking)
                        return new Pose(anchor.transform.position, anchor.transform.rotation);

                    await Task.Delay(100, cancellationToken);
                }

                return null;
            }
            finally
            {
                _ = ReleaseSystemAnchor(manager, anchor);
            }
        }

        public async Task<bool> EraseSystemAnchor(Guid id)
        {
            if (!HasRunningManager() || !_anchorManager.descriptor.supportsEraseAnchor)
                return false;

            var result = await _anchorManager.TryEraseAnchorAsync(new SerializableGuid(id));
            return result.IsSuccess();
        }

        private static async Task ReleaseSystemAnchor(ARAnchorManager manager, ARAnchor anchor)
        {
            while (anchor != null && manager != null && manager.isActiveAndEnabled
                && manager.subsystem?.running == true)
            {
                // The subsystem must report the add before it can report the removal.
                if (anchor.pending)
                {
                    await Awaitable.NextFrameAsync();
                    continue;
                }

                if (manager.GetAnchor(anchor.trackableId) == anchor)
                    manager.TryRemoveAnchor(anchor);
                return;
            }
        }

        private static void HideSystemAnchor(ARAnchor anchor)
        {
            anchor.gameObject.hideFlags |= HideFlags.HideInHierarchy;
            foreach (var renderer in anchor.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = false;
            foreach (var collider in anchor.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;
            foreach (var canvas in anchor.GetComponentsInChildren<Canvas>(true))
                canvas.enabled = false;
        }

        private async Task<Guid?> SaveAnchor(ARAnchor anchor)
        {
            var result = await _anchorManager.TrySaveAnchorAsync(anchor);
            if (result.status.IsSuccess())
                return result.value.guid;

            Debug.LogWarning($"AR Foundation failed to save an anchor: {result.status}");
            return null;
        }

        public async Task<SAHAnchor> PlaceSpatialAnchor(
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            GameObject prefab)
        {
            EnsureManagerReady();

            var result = await _anchorManager.TryAddAnchorAsync(
                new Pose(position, rotation));
            if (!result.status.IsSuccess())
            {
                Debug.LogError($"AR Foundation failed to create an anchor: {result.status}");
                return null;
            }

            ARAnchor nativeAnchor = result.value;
            _activeAnchors.Add(nativeAnchor);
            GameObject content = InstantiateContent(prefab, nativeAnchor, null);
            content.transform.localScale = scale;

            return new SAHAnchor
            {
                Content = content,
                NativeHandle = nativeAnchor
            };
        }

        public void ClearSpatialAnchors()
        {
            foreach (ARAnchor anchor in _activeAnchors)
            {
                if (anchor != null)
                {
                    UnityEngine.Object.Destroy(anchor.gameObject);
                }
            }

            _activeAnchors.Clear();
            Debug.Log("Cleared all AR Foundation spatial anchors.");
        }

        public Task<Guid?> SaveSpatialAnchor(SAHAnchor anchor)
        {
            EnsureManagerReady();
            EnsurePersistenceSupported(save: true);
            return anchor.NativeHandle is ARAnchor nativeAnchor && nativeAnchor != null
                ? SaveAnchor(nativeAnchor)
                : Task.FromResult<Guid?>(null);
        }

        public async Task<List<SAHAnchor>> LoadSpatialAnchors(List<SAHAnchorSaveData> saveData)
        {
            EnsureManagerReady();
            EnsurePersistenceSupported(save: false);

            var anchors = new List<SAHAnchor>();
            foreach (SAHAnchorSaveData data in saveData)
            {
                if (!Guid.TryParse(data.anchorId, out Guid guid))
                {
                    Debug.LogError($"Invalid persistent anchor UUID: {data.anchorId}");
                    continue;
                }

                var result = await _anchorManager.TryLoadAnchorAsync(
                    new SerializableGuid(guid));
                if (!result.status.IsSuccess())
                {
                    Debug.LogWarning(
                        $"AR Foundation failed to load anchor '{guid}': {result.status}");
                    continue;
                }

                GameObject prefab = Resources.Load<GameObject>(data.prefabPath);
                if (prefab == null)
                {
                    Debug.LogError($"Prefab not found in Resources/{data.prefabPath}");
                    continue;
                }

                ARAnchor nativeAnchor = result.value;
                _activeAnchors.Add(nativeAnchor);
                GameObject content = InstantiateContent(prefab, nativeAnchor, data.scale);

                anchors.Add(new SAHAnchor
                {
                    Id = data.anchorId,
                    PrefabPath = data.prefabPath,
                    Type = data.type,
                    Content = content,
                    NativeHandle = nativeAnchor
                });
            }

            return anchors;
        }

        private bool HasRunningManager()
        {
            if (_anchorManager == null)
                _anchorManager = UnityEngine.Object.FindFirstObjectByType<ARAnchorManager>();

            return _anchorManager != null && _anchorManager.isActiveAndEnabled
                && _anchorManager.subsystem?.running == true;
        }

        private void EnsureManagerReady()
        {
            if (!HasRunningManager())
                throw new InvalidOperationException("Enable an ARAnchorManager and its anchor provider.");
        }

        private void EnsurePersistenceSupported(bool save)
        {
            XRAnchorSubsystemDescriptor descriptor = _anchorManager.descriptor;
            bool supported = save
                ? descriptor.supportsSaveAnchor
                : descriptor.supportsLoadAnchor;

            if (!supported)
            {
                throw new NotSupportedException(
                    save
                        ? "The active AR Foundation provider cannot save persistent anchors."
                        : "The active AR Foundation provider cannot load persistent anchors.");
            }
        }

        private static GameObject InstantiateContent(
            GameObject prefab,
            ARAnchor nativeAnchor,
            SAHVector3? scale)
        {
            GameObject content = UnityEngine.Object.Instantiate(
                prefab,
                nativeAnchor.transform);
            content.transform.localPosition = Vector3.zero;
            content.transform.localRotation = Quaternion.identity;
            if (scale.HasValue)
            {
                content.transform.localScale = ToUnityVector3(scale.Value);
            }

            return content;
        }

        private static Vector3 ToUnityVector3(SAHVector3 value)
        {
            return new Vector3(value.x, value.y, value.z);
        }
    }
}
