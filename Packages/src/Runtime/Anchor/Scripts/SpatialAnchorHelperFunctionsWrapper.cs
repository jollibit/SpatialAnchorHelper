using System;
using TMPro;
using UnityEngine;

namespace SAH
{
    public class SpatialAnchorHelperFunctionsWrapper : MonoBehaviour
    {
        public string layoutID;
        [SerializeField] private TMP_Text saveButtonLabel;
        protected SpatialAnchorHelper _spatialAnchorHelper;

        protected virtual void Start()
        {
            _spatialAnchorHelper = SpatialAnchorHelper.Instance;
            if (_spatialAnchorHelper != null)
                _spatialAnchorHelper.LayoutStateChanged += UpdateSaveButton;
            UpdateSaveButton();
        }

        protected virtual void OnDestroy()
        {
            if (_spatialAnchorHelper != null)
                _spatialAnchorHelper.LayoutStateChanged -= UpdateSaveButton;
        }

        private void UpdateSaveButton()
        {
            if (saveButtonLabel != null)
                saveButtonLabel.text = _spatialAnchorHelper?.CanCreateLayout == true ? "Create Layout" : "Save";
        }

        public async void LoadSpatialAnchors()
        {
            if (_spatialAnchorHelper.LayoutId.HasValue)
                await _spatialAnchorHelper.LoadSpatialAnchors(layoutID);
            else
                await _spatialAnchorHelper.IdentifyLayoutsAsync();
        }

        public async void SaveSpatialAnchors()
        {
            if (saveButtonLabel != null && _spatialAnchorHelper.CanCreateLayout)
                await _spatialAnchorHelper.CreateLayoutAsync(layoutID);
            else
                await _spatialAnchorHelper.SaveSpatialAnchors(layoutID);
        }

        public async void CreateNewLayout() => await _spatialAnchorHelper.CreateLayoutAsync(layoutID);

        public async void DeleteLayout(string id)
        {
            if (Guid.TryParse(id, out Guid layoutId))
                await _spatialAnchorHelper.DeleteLayoutAsync(layoutId);
        }

        public void ClearSpatialAnchors()
        {
            _spatialAnchorHelper.ClearSpatialAnchors();
        }

        public void ClearRoomCache()
        {
            _spatialAnchorHelper.ClearRoomCache();
        }

        public void ClearAllCaches()
        {
            _spatialAnchorHelper.ClearAllCaches();
        }
    }
}
