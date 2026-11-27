using System;
using UnityEngine;
using TMPro;
using System.Collections.Generic;

namespace SAH
{
    public class DropdownToHelperBridge : MonoBehaviour
    {
        [SerializeField] private TMP_Dropdown _dropdown;
        [SerializeField] private SpatialAnchorHelperFunctionsWrapper _spatialAnchorHelperFunctionsWrapper;
        [SerializeField] private TMP_Text _summaryTextField;

        public bool sendValueOnStart = true;
        private SpatialAnchorHelper _helper;
        private List<TMP_Dropdown.OptionData> _contentOptions;
        private Guid[] _layoutChoices;
        private int _contentIndex;
        private string _displayedContentLayout;

        private void Start()
        {
            _dropdown.onValueChanged.AddListener(OnDropdownChanged);
            
            SpatialAnchorHelper.OnSaving += UpdateSummary;
            SpatialAnchorHelper.OnClearingRoomCache += UpdateSummary;
            SpatialAnchorHelper.OnClearingAllCaches += UpdateSummary;
            _helper = SpatialAnchorHelper.Instance;
            if (_helper != null)
                _helper.LayoutStateChanged += UpdateLayoutOptions;
            _contentOptions = new List<TMP_Dropdown.OptionData>(_dropdown.options);
            _contentIndex = _dropdown.value;

            if (sendValueOnStart)
            {
                string selectedValue = _dropdown.options[_dropdown.value].text;
                _spatialAnchorHelperFunctionsWrapper.layoutID = selectedValue;

                UpdateLayoutOptions();
            }

        }

        private void OnDestroy()
        {
            SpatialAnchorHelper.OnSaving -= UpdateSummary;
            SpatialAnchorHelper.OnClearingRoomCache -= UpdateSummary;
            SpatialAnchorHelper.OnClearingAllCaches -= UpdateSummary;
            if (_helper != null)
                _helper.LayoutStateChanged -= UpdateLayoutOptions;
        }

        private async void OnDropdownChanged(int index)
        {
            if (_layoutChoices != null)
            {
                if (index > 0 && index <= _layoutChoices.Length)
                    await _helper.SelectLayoutAsync(_layoutChoices[index - 1]);
                return;
            }

            _contentIndex = index;
            string selectedValue = _dropdown.options[index].text;
            _spatialAnchorHelperFunctionsWrapper.layoutID = selectedValue;

            UpdateSummary();
        }

        private void UpdateLayoutOptions()
        {
            if (_helper != null && !_helper.LayoutId.HasValue && _helper.LocalizedLayoutIds.Count > 1)
            {
                _layoutChoices = new List<Guid>(_helper.LocalizedLayoutIds).ToArray();
                var options = new List<TMP_Dropdown.OptionData> { new TMP_Dropdown.OptionData("Select a place") };
                foreach (Guid id in _layoutChoices)
                    options.Add(new TMP_Dropdown.OptionData(_helper.DescribeLayout(id)));

                _dropdown.options = options;
                _dropdown.SetValueWithoutNotify(0);
                _summaryTextField.text = "Several saved places are nearby.";
                return;
            }

            if (_layoutChoices != null)
            {
                _layoutChoices = null;
                _dropdown.options = _contentOptions;
                _dropdown.SetValueWithoutNotify(_contentIndex);
            }

            string selected = _helper?.SelectedContentLayout;
            if (selected != _displayedContentLayout)
            {
                _displayedContentLayout = selected;
                int index = _contentOptions.FindIndex(option => option.text == selected);
                if (index >= 0)
                {
                    _contentIndex = index;
                    _dropdown.SetValueWithoutNotify(index);
                    _spatialAnchorHelperFunctionsWrapper.layoutID = selected;
                }
            }

            UpdateSummary();
        }

        private void UpdateSummary()
        {
            List<SAHAnchorSaveData> savedAnchors = null;

            if(SpatialAnchorHelper.Instance != null) 
                savedAnchors = SpatialAnchorHelper.Instance.LoadLayoutCache(_spatialAnchorHelperFunctionsWrapper.layoutID);

            if (_helper != null && _helper.CanCreateLayout && _helper.HasSavedLayouts)
            {
                _summaryTextField.text = "No saved place localized. Use Load to retry or Create Layout for a new place.";
                return;
            }

            if(savedAnchors != null)
            {
                string summary = "";
                foreach(SAHAnchorSaveData anchor in savedAnchors)
                {
                    string entry = $"\n{anchor.anchorId}, {anchor.type}";
                    summary += entry;
                }

                if(summary != "")
                    _summaryTextField.text = summary;
                else
                    _summaryTextField.text = "Nothing found...";
            }
            else 
                _summaryTextField.text = "Nothing found...";
        }

    }
}
