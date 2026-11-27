using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace SAH
{
    public class SpatialAnchorHelper : MonoBehaviour
    {
        public static SpatialAnchorHelper Instance { get; private set; }

        public static Action OnRoomIdFound;
        public static Action OnSaving;
        public static Action OnLoading;
        public static Action OnCreating;
        public static Action OnClearingScene;
        public static Action OnClearingRoomCache;
        public static Action OnClearingAllCaches;

        public List<SAHAnchor> Anchors { get; private set; } = new List<SAHAnchor>();


        [SerializeField] private string _persistentDataLocation = "anchors";
        [SerializeField] private SpatialAnchorBackendType _backendType = SpatialAnchorBackendType.Meta;

        private ISpatialAnchorBackend _backend;
        private const int TrackingTimeoutSeconds = 10;
        private const int LocalizationTimeoutSeconds = 5;
        private const float MinimumDistanceLead = 1f;

        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly Dictionary<Guid, LayoutFile> _layouts = new Dictionary<Guid, LayoutFile>();
        private readonly List<Guid> _localizedLayoutIds = new List<Guid>();
        private LayoutFile _currentLayout;
        private bool _layoutBusy;
        private bool _identificationFinished;

        public Guid? LayoutId => _currentLayout?.Id;
        public IReadOnlyList<Guid> LocalizedLayoutIds => _localizedLayoutIds;
        public string SelectedContentLayout { get; private set; }
        public bool CanCreateLayout => !_layoutBusy && _identificationFinished
            && _currentLayout == null && _localizedLayoutIds.Count == 0;
        internal bool HasSavedLayouts => _layouts.Count > 0;
        public string RoomId => LayoutId?.ToString();
        public event Action LayoutStateChanged;

        private string LayoutFolder => Path.Combine(Application.persistentDataPath, _persistentDataLocation);

        private sealed class LayoutFile
        {
            [JsonProperty("layout_id", Required = Required.Always)]
            public Guid Id;

            [JsonProperty("last_scenario")]
            public string LastLayout;

            [JsonProperty("scenarios", Required = Required.Always)]
            public Dictionary<string, List<SAHAnchorSaveData>> Content =
                new Dictionary<string, List<SAHAnchorSaveData>>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            _backend = SpatialAnchorBackendFactory.Create(_backendType);
        }

        private void OnEnable()
        {
            SpatialAnchorSpawnEmitter.OnDespawned += RemoveSpatialAnchor;
        }

        private void OnDisable()
        {
            SpatialAnchorSpawnEmitter.OnDespawned -= RemoveSpatialAnchor;
        }

        private async void Start()
        {
            await IdentifyLayoutsAsync();
        }

        private void OnDestroy()
        {
            _lifetime.Cancel();
            if (Instance == this)
                Instance = null;
        }

        public Task IdentifyLayoutsAsync() => RunLayoutOperation(async () =>
        {
            if (_currentLayout != null)
                return;

            _identificationFinished = false;
            _localizedLayoutIds.Clear();
            if (!await WaitForTracking())
            {
                Debug.LogWarning("Tracking is unavailable. Retry layout identification once the headset is tracking.");
                return;
            }

            if (!_backend.SupportsPersistence)
            {
                Debug.LogWarning("The active XR provider does not support persistent anchor save and load.");
                return;
            }

            ReadLayouts();
            var matches = new List<(Guid id, float distance)>();
            for (int attempt = 0; attempt < 2 && matches.Count == 0 && _layouts.Count > 0; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(500, _lifetime.Token);

                var located = await LocalizeLayouts(_layouts.Keys);
                Camera camera = Camera.main;
                if (camera == null || !_backend.IsTrackingReady)
                    continue;

                Vector3 position = camera.transform.position;
                foreach (var match in located)
                {
                    float distance = Vector3.Distance(position, match.pose.position);
                    if (!float.IsNaN(distance) && !float.IsInfinity(distance))
                        matches.Add((match.id, distance));
                }
            }

            _identificationFinished = _backend.IsTrackingReady;
            if (!_identificationFinished)
                return;

            matches.Sort((a, b) => a.distance.CompareTo(b.distance));
            _localizedLayoutIds.AddRange(matches.Select(match => match.id));

            if (matches.Count == 0)
            {
                if (_layouts.Count > 0)
                    Debug.LogWarning("No saved layout localized. Retry identification or explicitly create a new layout.");
                return;
            }

            if (matches.Count > 1 && matches[1].distance - matches[0].distance < MinimumDistanceLead)
            {
                Debug.LogWarning($"Several layouts localized nearby. Select a layout explicitly: {string.Join(", ", _localizedLayoutIds)}");
                return;
            }

            await ActivateLayout(_layouts[matches[0].id]);
        });

        public Task SelectLayoutAsync(Guid id) => RunLayoutOperation(async () =>
        {
            if (!_localizedLayoutIds.Contains(id) || !_layouts.TryGetValue(id, out LayoutFile layout))
                return;

            if ((await LocalizeLayouts(new[] { id })).Count == 0 || !_backend.IsTrackingReady)
                return;

            await ActivateLayout(layout);
        });

        internal string DescribeLayout(Guid id) =>
            _layouts.TryGetValue(id, out LayoutFile layout) && !string.IsNullOrEmpty(layout.LastLayout)
                ? $"{layout.LastLayout} ({id.ToString().Substring(0, 8)})"
                : id.ToString();

        public Task CreateLayoutAsync(string layoutID) => RunLayoutOperation(async () =>
        {
            if (_currentLayout != null || !_identificationFinished || _localizedLayoutIds.Count > 0)
            {
                Debug.LogWarning("Finish layout identification and resolve any nearby layouts before creating a new one.");
                return;
            }

            if (await CreateLayoutFile())
                await SaveContent(layoutID);
        });

        public Task SaveSpatialAnchors(string layoutID) => RunLayoutOperation(async () =>
        {
            if (_currentLayout == null)
            {
                Debug.LogWarning("Select a localized layout or use Create Layout.");
                return;
            }

            await SaveContent(layoutID);
        });

        public async Task<List<SAHAnchor>> LoadSpatialAnchors(string layoutID)
        {
            List<SAHAnchor> loaded = null;
            await RunLayoutOperation(async () =>
            {
                if (_currentLayout != null)
                    loaded = await LoadContent(layoutID);
            });
            return loaded;
        }

        public Task DeleteLayoutAsync(Guid id) => RunLayoutOperation(async () =>
        {
            if (!_layouts.ContainsKey(id))
                return;

            if (!await _backend.EraseSystemAnchor(id))
            {
                Debug.LogWarning($"The provider could not erase layout anchor {id}. The layout file was kept.");
                return;
            }

            File.Delete(LayoutPath(id));
            _layouts.Remove(id);
            _localizedLayoutIds.Remove(id);
            if (_currentLayout?.Id == id)
            {
                _currentLayout = null;
                SelectedContentLayout = null;
                ClearContent();
            }

            OnClearingRoomCache?.Invoke();
        });

        private async Task<bool> WaitForTracking()
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!_backend.IsTrackingReady || Camera.main == null)
            {
                if (timer.Elapsed.TotalSeconds >= TrackingTimeoutSeconds)
                    return false;

                await Task.Delay(100, _lifetime.Token);
            }

            return true;
        }

        private async Task<(Guid id, Pose? pose)> ProbeLayout(Guid id, CancellationToken cancellationToken)
        {
            try
            {
                return (id, await _backend.LocateSystemAnchor(id, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                return (id, null);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Could not localize layout {id}: {exception.Message}");
                return (id, null);
            }
        }

        private async Task<List<(Guid id, Pose pose)>> LocalizeLayouts(IEnumerable<Guid> ids)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var probes = ids.Select(id => ProbeLayout(id, timeout.Token)).ToArray();
            await Task.WhenAny(Task.WhenAll(probes),
                Task.Delay(TimeSpan.FromSeconds(LocalizationTimeoutSeconds), timeout.Token));
            timeout.Cancel();
            _lifetime.Token.ThrowIfCancellationRequested();

            var localized = new List<(Guid id, Pose pose)>();
            foreach (var probe in probes)
            {
                if (probe.Status == TaskStatus.RanToCompletion && probe.Result.pose is Pose pose)
                    localized.Add((probe.Result.id, pose));
            }

            return localized;
        }

        private void ReadLayouts()
        {
            _layouts.Clear();
            if (!Directory.Exists(LayoutFolder))
                return;

            int legacyFiles = 0;
            foreach (string path in Directory.GetFiles(LayoutFolder))
            {
                if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(Path.GetExtension(path)))
                        legacyFiles++;
                    continue;
                }

                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out Guid id)
                    || id == Guid.Empty || Path.GetFileName(path) != $"{id}.json")
                    continue;

                try
                {
                    LayoutFile layout = JsonConvert.DeserializeObject<LayoutFile>(File.ReadAllText(path));
                    if (layout == null || layout.Id != id)
                    {
                        Debug.LogWarning($"Ignoring layout file with a missing or mismatched layout ID: {path}");
                        continue;
                    }

                    _layouts.Add(id, layout);
                }
                catch (JsonException exception)
                {
                    Debug.LogWarning($"Could not read layout file '{path}': {exception.Message}");
                }
            }

            if (legacyFiles > 0)
                Debug.LogWarning($"Found {legacyFiles} legacy anchor cache file(s). They were left in place and require explicit migration.");
        }

        private async Task<bool> CreateLayoutFile()
        {
            Camera camera = Camera.main;
            if (camera == null || !_backend.IsTrackingReady || !_backend.SupportsPersistence)
                return false;

            // Place the identifier at the user's position without creating a visible prefab.
            Guid? id = await _backend.CreateSystemAnchor(new Pose(camera.transform.position, camera.transform.rotation));
            if (!id.HasValue || id.Value == Guid.Empty)
            {
                Debug.LogWarning("Could not persist a system anchor for the new layout.");
                return false;
            }

            var layout = new LayoutFile { Id = id.Value };
            if (File.Exists(LayoutPath(layout.Id)))
                throw new IOException($"A file already exists for layout anchor {layout.Id}.");

            try
            {
                WriteLayout(layout);
            }
            catch
            {
                await _backend.EraseSystemAnchor(layout.Id);
                throw;
            }

            _layouts.Add(layout.Id, layout);
            _localizedLayoutIds.Add(layout.Id);
            SetCurrentLayout(layout);
            return true;
        }

        private async Task ActivateLayout(LayoutFile layout)
        {
            SetCurrentLayout(layout);
            string layoutID = layout.LastLayout != null && layout.Content.ContainsKey(layout.LastLayout)
                ? layout.LastLayout
                : layout.Content.Keys.FirstOrDefault();
            if (layoutID != null)
                await LoadContent(layoutID);
            else
                ClearContent();
        }

        private void SetCurrentLayout(LayoutFile layout)
        {
            _currentLayout = layout;
            SelectedContentLayout = null;
            Debug.Log($"Localized layout {layout.Id}");
            OnRoomIdFound?.Invoke();
        }

        private async Task SaveContent(string layoutID)
        {
            if (string.IsNullOrEmpty(layoutID))
                return;

            var content = new List<SAHAnchorSaveData>();
            foreach (SAHAnchor anchor in Anchors.ToArray())
            {
                Guid? id = await _backend.SaveSpatialAnchor(anchor);
                if (!id.HasValue || id.Value == Guid.Empty)
                {
                    Debug.LogWarning("An anchor could not be saved. The previous layout content was kept.");
                    return;
                }

                anchor.Id = id.Value.ToString();
                if (anchor.Content != null && anchor.Content.TryGetComponent(out SAHAnchorTag tag))
                    tag.Initialize(anchor);

                Vector3 scale = anchor.Content != null ? anchor.Content.transform.localScale : Vector3.one;
                content.Add(new SAHAnchorSaveData
                {
                    anchorId = anchor.Id,
                    prefabPath = anchor.PrefabPath,
                    type = anchor.Type,
                    scale = new SAHVector3 { x = scale.x, y = scale.y, z = scale.z }
                });
            }

            var updated = CopyLayout(_currentLayout);
            updated.Content[layoutID] = content;
            updated.LastLayout = layoutID;
            UpdateLayout(updated);
            SelectedContentLayout = layoutID;
            OnSaving?.Invoke();
        }

        private async Task<List<SAHAnchor>> LoadContent(string layoutID)
        {
            if (string.IsNullOrEmpty(layoutID) || !_currentLayout.Content.TryGetValue(layoutID, out var data))
                return null;

            ClearContent();
            Anchors = await _backend.LoadSpatialAnchors(data) ?? new List<SAHAnchor>();
            foreach (SAHAnchor anchor in Anchors)
            {
                var tag = anchor.Content.GetComponent<SAHAnchorTag>()
                    ?? anchor.Content.AddComponent<SAHAnchorTag>();
                tag.Initialize(anchor);
            }

            SelectedContentLayout = layoutID;
            OnLoading?.Invoke();
            return Anchors;
        }

        private async Task RunLayoutOperation(Func<Task> operation)
        {
            if (_layoutBusy)
                return;

            _layoutBusy = true;
            try
            {
                await operation();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                _layoutBusy = false;
                if (!_lifetime.IsCancellationRequested)
                    LayoutStateChanged?.Invoke();
            }
        }

        private string LayoutPath(Guid id) => Path.Combine(LayoutFolder, $"{id}.json");

        private void WriteLayout(LayoutFile layout)
        {
            Directory.CreateDirectory(LayoutFolder);
            string path = LayoutPath(layout.Id);
            string temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonConvert.SerializeObject(layout, Formatting.Indented));
                if (File.Exists(path))
                    File.Replace(temporary, path, null);
                else
                    File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        private static LayoutFile CopyLayout(LayoutFile layout) => new LayoutFile
        {
            Id = layout.Id,
            LastLayout = layout.LastLayout,
            Content = new Dictionary<string, List<SAHAnchorSaveData>>(layout.Content)
        };

        private void UpdateLayout(LayoutFile layout)
        {
            WriteLayout(layout);
            _layouts[layout.Id] = layout;
            if (_currentLayout?.Id == layout.Id)
                _currentLayout = layout;
        }

        public async Task CreateSpatialAnchor(Vector3 position, Quaternion rotation, Vector3 scale, string prefabPath, string type = null)
        {
            GameObject prefab = Resources.Load<GameObject>(prefabPath);

            if (prefab == null)
            {
                Debug.LogError($"Prefab not found at Resources/{prefabPath}");
                return;
            }

            if (type == null)
            {
                SpatialAnchorSpawnEmitter spawnEmitter = prefab.GetComponent<SpatialAnchorSpawnEmitter>();

                if (spawnEmitter != null && spawnEmitter.optionalSpatialAnchorType != null)
                {
                    type = spawnEmitter.optionalSpatialAnchorType;
                }
            }

            SAHAnchor anchor = await _backend.PlaceSpatialAnchor(position, rotation, scale, prefab);
            if (anchor == null)
            {
                return;
            }

            anchor.PrefabPath = prefabPath;
            anchor.Type = type;

            SAHAnchorTag tag =
                anchor.Content.GetComponent<SAHAnchorTag>()
                ?? anchor.Content.AddComponent<SAHAnchorTag>();

            tag.Initialize(anchor);

            Anchors.Add(anchor);

            OnCreating?.Invoke();
        }

        public void ClearSpatialAnchors()
        {
            if (!_layoutBusy)
                ClearContent();
        }

        private void ClearContent()
        {
            _backend.ClearSpatialAnchors();
            Anchors.Clear();

            OnClearingScene?.Invoke();
        }

        // These existing UI actions reset content only. Deleting a layout is a separate action.
        public void ClearRoomCache()
        {
            if (_layoutBusy || _currentLayout == null)
                return;

            ResetLayoutContent(_currentLayout);
            OnClearingRoomCache?.Invoke();
        }

        public void ClearAllCaches()
        {
            if (_layoutBusy || !_identificationFinished)
                return;

            foreach (LayoutFile layout in _layouts.Values.ToArray())
                ResetLayoutContent(layout);

            OnClearingAllCaches?.Invoke();
        }

        private void ResetLayoutContent(LayoutFile layout)
        {
            var empty = new LayoutFile { Id = layout.Id };
            UpdateLayout(empty);
        }

        public Dictionary<string, List<SAHAnchorSaveData>> LoadRoomCache() =>
            _currentLayout == null ? null : CopyLayout(_currentLayout).Content;

        public List<SAHAnchorSaveData> LoadLayoutCache(string layoutID)
        {
            if (string.IsNullOrEmpty(layoutID) || _currentLayout == null)
                return null;

            return _currentLayout.Content.TryGetValue(layoutID, out var data) ? data : null;
        }

        private void RemoveSpatialAnchor(SpatialAnchorSpawnEmitter emitter)
        {
            int index = Anchors.FindIndex(a => a.Content == emitter.gameObject);

            if (index == -1)
            {
                Debug.LogWarning($"Anchor not found for emitter {emitter.name}");
                return;
            }

            Anchors.RemoveAt(index);
        }
    }
}
