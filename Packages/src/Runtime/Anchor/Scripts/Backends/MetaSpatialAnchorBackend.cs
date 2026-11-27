using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SAH
{
    public sealed class MetaSpatialAnchorBackend : ISpatialAnchorBackend
    {
        public bool IsTrackingReady => OVRPlugin.GetNodePositionTracked(OVRPlugin.Node.EyeCenter);
        public bool SupportsPersistence => OVRManager.isHmdPresent;

        public async Task<Guid?> CreateSystemAnchor(Pose pose)
        {
            var instance = new GameObject("Layout System Anchor") { hideFlags = HideFlags.HideInHierarchy };
            instance.transform.SetPositionAndRotation(pose.position, pose.rotation);

            try
            {
                var anchor = instance.AddComponent<OVRSpatialAnchor>();
                return await anchor.WhenCreatedAsync() ? await SaveAnchor(anchor) : null;
            }
            finally
            {
                UnityEngine.Object.Destroy(instance);
            }
        }

        public async Task<Pose?> LocateSystemAnchor(Guid id, CancellationToken cancellationToken)
        {
            var anchors = new List<OVRAnchor>();
            try
            {
                var result = await OVRAnchor.FetchAnchorsAsync(
                    anchors, new OVRAnchor.FetchOptions { Uuids = new[] { id } });
                if (!result.Success || anchors.Count == 0 || cancellationToken.IsCancellationRequested)
                    return null;

                OVRAnchor anchor = anchors[0];
                if (!OVRSpatialAnchor.FromOVRAnchor(anchor, out var unbound)
                    || !await unbound.LocalizeAsync(4)
                    || !anchor.TryGetComponent<OVRLocatable>(out var locatable))
                    return null;

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (locatable.TryGetSpatialAnchorPose(out var trackedPose)
                        && trackedPose.IsPositionTracked && trackedPose.IsRotationTracked
                        && unbound.TryGetPose(out Pose pose))
                        return pose;

                    await Task.Delay(100, cancellationToken);
                }

                return null;
            }
            finally
            {
                foreach (OVRAnchor anchor in anchors)
                    anchor.Dispose();
            }
        }

        public async Task<bool> EraseSystemAnchor(Guid id)
        {
            var result = await OVRAnchor.EraseAsync(Array.Empty<OVRAnchor>(), new[] { id });
            return result.Success;
        }

        private static async Task<Guid?> SaveAnchor(OVRSpatialAnchor anchor)
        {
            var result = await anchor.SaveAnchorAsync();
            if (result.Success)
                return anchor.Uuid;

            Debug.LogWarning($"Meta failed to save an anchor: {result.Status}");
            return null;
        }

        public async Task<SAHAnchor> PlaceSpatialAnchor(
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            GameObject prefab)
        {

            GameObject parent =
                GameObject.Find("SpatialAnchors")
                ?? new GameObject("SpatialAnchors");

            GameObject instance = UnityEngine.Object.Instantiate(
                prefab,
                position,
                rotation,
                parent.transform);

            instance.transform.localScale = scale;

            OVRSpatialAnchor nativeAnchor =
                instance.AddComponent<OVRSpatialAnchor>();

            if (!await nativeAnchor.WhenCreatedAsync())
            {
                UnityEngine.Object.Destroy(instance);
                return null;
            }

            return new SAHAnchor
            {
                Id = nativeAnchor.Uuid.ToString(),
                Content = instance,
                NativeHandle = nativeAnchor
            };
        }

        public void ClearSpatialAnchors()
        {
            GameObject parent = GameObject.Find("SpatialAnchors");
            if (parent == null)
            {
                Debug.LogWarning("No 'SpatialAnchors' GameObject found.");
                return;
            }

            for (int i = parent.transform.childCount - 1; i >= 0; i--)
            {
                GameObject child = parent.transform.GetChild(i).gameObject;
                UnityEngine.Object.Destroy(child);
            }

            Debug.Log("Cleared all spatial anchors.");
        }

        public Task<Guid?> SaveSpatialAnchor(SAHAnchor anchor) =>
            anchor.NativeHandle is OVRSpatialAnchor nativeAnchor && nativeAnchor != null
                ? SaveAnchor(nativeAnchor)
                : Task.FromResult<Guid?>(null);

        public async Task<List<SAHAnchor>> LoadSpatialAnchors(List<SAHAnchorSaveData> saveDataList)
        {
            List<Guid> anchorIds = new List<Guid>();
            Dictionary<Guid, SAHAnchorSaveData> idToData = new Dictionary<Guid, SAHAnchorSaveData>();

            foreach (var data in saveDataList)
            {
                if (Guid.TryParse(data.anchorId, out Guid guid))
                {
                    anchorIds.Add(guid);
                    idToData[guid] = data;
                }
                else
                {
                    Debug.LogError($"Invalid anchor UUID: {data.anchorId}");
                }
            }

            List<OVRSpatialAnchor.UnboundAnchor> unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>();
            if (anchorIds.Count == 0)
                return new List<SAHAnchor>();

            var result = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(anchorIds, unboundAnchors);

            if (!result.Success)
            {
                Debug.LogError($"Failed to load anchors. Status: {result.Status}");
                return null;
            }

            List<SAHAnchor> anchors = new List<SAHAnchor>();

            foreach (var unbound in unboundAnchors)
            {
                bool success = await unbound.LocalizeAsync();
                if (!success)
                {
                    Debug.LogWarning($"Localization failed for anchor {unbound.Uuid}");
                    continue;
                }

                if (!idToData.TryGetValue(unbound.Uuid, out SAHAnchorSaveData anchorData))
                {
                    Debug.LogWarning($"No prefab path found for anchor {unbound.Uuid}");
                    continue;
                }

                GameObject prefab = Resources.Load<GameObject>(anchorData.prefabPath);
                if (prefab == null)
                {
                    Debug.LogError($"Prefab not found in Resources/{anchorData.prefabPath}");
                    continue;
                }

                GameObject parent = GameObject.Find("SpatialAnchors") ?? new GameObject("SpatialAnchors");

                Pose pose = default;

                if (!unbound.TryGetPose(out pose))
                {
                    Debug.Log("Failed to find pose!");
                }

                GameObject spawned = UnityEngine.Object.Instantiate(prefab, pose.position, pose.rotation, parent.transform);

                // Older cache entries have no scale field. In that case, retain
                // the scale configured on the prefab.
                if (anchorData.scale.HasValue)
                {
                    spawned.transform.localScale = ToUnityVector3(anchorData.scale.Value);
                }

                var boundAnchor = spawned.AddComponent<OVRSpatialAnchor>();
                unbound.BindTo(boundAnchor);

                anchors.Add(new SAHAnchor
                {
                    Id = unbound.Uuid.ToString(),
                    PrefabPath = anchorData.prefabPath,
                    Type = anchorData.type,
                    Content = spawned,
                    NativeHandle = boundAnchor
                });

                Debug.Log($"Loaded and placed prefab for anchor {unbound.Uuid}");
            }

            return anchors;
        }

        private static Vector3 ToUnityVector3(SAHVector3 value)
        {
            return new Vector3(value.x, value.y, value.z);
        }
    }
}
