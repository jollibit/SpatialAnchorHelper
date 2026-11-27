using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SAH
{
    public interface ISpatialAnchorBackend
    {
        bool IsTrackingReady { get; }
        bool SupportsPersistence { get; }
        Task<Guid?> CreateSystemAnchor(Pose pose);
        Task<Pose?> LocateSystemAnchor(Guid id, CancellationToken cancellationToken);
        Task<bool> EraseSystemAnchor(Guid id);

        Task<SAHAnchor> PlaceSpatialAnchor(
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            GameObject prefab);

        void ClearSpatialAnchors();

        Task<Guid?> SaveSpatialAnchor(SAHAnchor anchor);

        Task<List<SAHAnchor>> LoadSpatialAnchors(
            List<SAHAnchorSaveData> saveData);
    }
}
