using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class Main : Mod
{
    // ================= USER TWEAKABLE =================
    private static readonly Vector2 ReceiverOffset = new Vector2(-70f, -65f);
    private static readonly Vector2 GaugeOffset = new Vector2(-60f, -95f);

    private const float SpeedUpdateInterval = 0.15f;
    private const float ReceiverScanInterval = 60.0f;
    private const float RaftPivotScanInterval = 10.0f;
    private const float ReceiverCacheScanInterval = 45.0f;

    private const string SpeedFormat = "SPD {0:0.0} m/s";
    private const float MaxValidSpeed = 500f;
    private const float FallbackFontSize = 20f;

    private const float GaugeMaxSpeed = 8.0f;
    private const int GaugeBlocks = 10;
    private const float GaugeMspaceEm = 0.6f;
    // ==================================================

    private static readonly Color LabelColor = new Color(0.63f, 0.89f, 0.87f, 1.0f);

    private float _nextSpeedUpdate;
    private float _nextReceiverScan;
    private float _nextPivotScan;
    private float _nextReceiverCacheScan;

    private float _speed;
    private float _smoothedSpeed;
    private string _cachedSpeedText = "SPD 0.0 m/s";
    private string _lastAppliedSpeedText = "";

    private Raft _cachedRaft;
    private Transform _raftPivot;
    private Vector3 _lastRaftPos;
    private float _lastRaftT;

    private readonly Dictionary<int, TextMeshProUGUI> _receiverSpeed = new Dictionary<int, TextMeshProUGUI>();
    private readonly Dictionary<int, TextMeshProUGUI> _receiverGauge = new Dictionary<int, TextMeshProUGUI>();
    private readonly Dictionary<int, int> _receiverGaugeState = new Dictionary<int, int>();
    private readonly List<Transform> _receiverCache = new List<Transform>(16);

    private readonly StringBuilder _stringBuilder = new StringBuilder(64);
    private float _cachedSpeedValue;

    public void Start()
    {
        Cleanup();
    }

    public override void WorldEvent_WorldLoaded()
    {
        _cachedRaft = ComponentManager<Raft>.Value;
        float now = Time.time;
        _nextSpeedUpdate = now + SpeedUpdateInterval;
        _nextReceiverScan = now + ReceiverScanInterval;
        _nextPivotScan = now + RaftPivotScanInterval;
        _nextReceiverCacheScan = 0f;
        FindRaftPivot(true);
        ScanReceivers(false);
    }

    public void Update()
    {
        float now = Time.time;
        if (now >= _nextPivotScan)
        {
            _nextPivotScan = now + RaftPivotScanInterval;
            if (_raftPivot == null || !IsValid(_raftPivot))
                FindRaftPivot(false);
        }

        if (now >= _nextSpeedUpdate)
        {
            _nextSpeedUpdate = now + SpeedUpdateInterval;
            float raw = ComputeRaftSpeed();
            if (!(raw >= 0f) || float.IsNaN(raw) || float.IsInfinity(raw)) raw = 0f;
            _speed = _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, raw, 0.35f);
            _cachedSpeedText = GetCachedSpeedText(_speed);
        }

        if (now >= _nextReceiverScan)
        {
            _nextReceiverScan = now + ReceiverScanInterval;
            ScanReceivers(false);
        }

        UpdateLabels();
    }

    public override void UnloadMod()
    {
        Cleanup();
        base.UnloadMod();
    }

    public void OnDestroy() => Cleanup();

    private void Cleanup()
    {
        foreach (var t in _receiverSpeed.Values) if (t != null) Destroy(t.gameObject);
        foreach (var t in _receiverGauge.Values) if (t != null) Destroy(t.gameObject);
        _receiverSpeed.Clear();
        _receiverGauge.Clear();
        _receiverGaugeState.Clear();
        _receiverCache.Clear();
        _cachedRaft = null;
        _raftPivot = null;
    }

    private string GetCachedSpeedText(float speed)
    {
        float rounded = Mathf.Round(speed * 10f) / 10f;
        if (!Mathf.Approximately(rounded, _cachedSpeedValue))
        {
            _cachedSpeedValue = rounded;
            _cachedSpeedText = string.Format(SpeedFormat, rounded);
        }
        return _cachedSpeedText;
    }

    private void FindRaftPivot(bool resetBaseline)
    {
        _raftPivot = null;
        Raft raftComponent = _cachedRaft;
        if (raftComponent != null)
        {
            Transform raftTransform = raftComponent.gameObject.transform;
            foreach (Transform child in raftTransform)
            {
                if (child.name == "RotatePivot")
                {
                    foreach (Transform grandchild in child)
                    {
                        if (grandchild.name == "LockedPivot" && IsValid(grandchild))
                        {
                            _raftPivot = grandchild;
                            break;
                        }
                    }
                    break;
                }
            }
        }
        if (_raftPivot == null)
        {
            foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null) continue;
                if (!t.gameObject.scene.IsValid()) continue;
                if (!t.gameObject.activeInHierarchy) continue;
                if (t.name != "LockedPivot") continue;
                Transform rotatePivot = t.parent;
                if (rotatePivot == null || rotatePivot.name != "RotatePivot") continue;
                Transform raft = rotatePivot.parent;
                if (raft == null || !raft.name.StartsWith("Raft", StringComparison.Ordinal)) continue;
                _raftPivot = t;
                break;
            }
        }
        if (_raftPivot != null && (resetBaseline || _lastRaftT <= 0f))
        {
            _lastRaftPos = _raftPivot.position;
            _lastRaftT = Time.time;
            _smoothedSpeed = 0f;
        }
    }

    private float ComputeRaftSpeed()
    {
        if (_raftPivot == null) return 0f;
        float now = Time.time;
        float dt = Mathf.Max(now - _lastRaftT, 0.0001f);
        Vector3 pos = _raftPivot.position;
        float speed = Mathf.Min((pos - _lastRaftPos).magnitude / dt, MaxValidSpeed);
        _lastRaftPos = pos;
        _lastRaftT = now;
        return speed;
    }

    private void UpdateLabels()
    {
        bool speedChanged = _lastAppliedSpeedText != _cachedSpeedText;
        int filled = GaugeFilled(_speed);

        foreach (var kv in _receiverSpeed.ToList())
        {
            if (!IsReceiverAlive(kv.Key, kv.Value, out var gauge))
            {
                RemoveForReceiver(kv.Key);
                continue;
            }
            if (speedChanged) kv.Value.text = _cachedSpeedText;
            if (!_receiverGaugeState.TryGetValue(kv.Key, out int last) || last != filled)
            {
                gauge.text = BuildGaugeString(filled);
                _receiverGaugeState[kv.Key] = filled;
            }
        }

        _lastAppliedSpeedText = _cachedSpeedText;
    }

    private void ScanReceivers(bool forceRebind)
    {
        RemoveDeadTracked();

        float now = Time.time;
        if (forceRebind || _receiverCache.Count == 0 || now >= _nextReceiverCacheScan)
        {
            _nextReceiverCacheScan = now + ReceiverCacheScanInterval;
            RefreshReceiverCache();
        }

        for (int i = _receiverCache.Count - 1; i >= 0; i--)
        {
            Transform tr = _receiverCache[i];
            if (!IsValid(tr))
            {
                _receiverCache.RemoveAt(i);
                continue;
            }

            int id = tr.gameObject.GetInstanceID();

            if (!forceRebind &&
                _receiverSpeed.TryGetValue(id, out var existingSpeed) && IsAlive(existingSpeed) &&
                _receiverGauge.TryGetValue(id, out var existingGauge) && IsAlive(existingGauge))
                continue;

            Canvas canvas = FindReceiverCanvas(tr);
            if (canvas == null) continue;

            RemoveForReceiver(id);
            TMP_Text reference = FindReferenceTMP(canvas.transform);

            _receiverSpeed[id] = CreateSpeedLabel(canvas.transform, id, reference);
            _receiverGauge[id] = CreateGaugeLabel(canvas.transform, id, reference);
            _receiverGaugeState[id] = -1;
        }
    }

    private void RemoveDeadTracked()
    {
        foreach (var id in _receiverSpeed.Keys.Where(k => !IsReceiverAlive(k, _receiverSpeed[k], out _)).ToList())
            RemoveForReceiver(id);
    }

    private void RefreshReceiverCache()
    {
        _receiverCache.Clear();
        _receiverCache.AddRange(Resources.FindObjectsOfTypeAll<Transform>()
            .Where(tr => tr != null && tr.gameObject.scene.IsValid() &&
                        tr.gameObject.activeInHierarchy &&
                        tr.name.StartsWith("Placeable_Reciever", StringComparison.Ordinal)));
    }

    private void RemoveForReceiver(int id)
    {
        if (_receiverSpeed.TryGetValue(id, out var s) && s != null) Destroy(s.gameObject);
        if (_receiverGauge.TryGetValue(id, out var g) && g != null) Destroy(g.gameObject);
        _receiverSpeed.Remove(id);
        _receiverGauge.Remove(id);
        _receiverGaugeState.Remove(id);
    }

    private bool IsReceiverAlive(int id, TextMeshProUGUI speedLabel, out TextMeshProUGUI gaugeLabel)
    {
        gaugeLabel = null;
        return IsAlive(speedLabel) && _receiverGauge.TryGetValue(id, out gaugeLabel) && IsAlive(gaugeLabel);
    }

    private static Canvas FindReceiverCanvas(Transform receiver)
    {
        var canvases = receiver.GetComponentsInChildren<Canvas>(true);
        if (canvases == null || canvases.Length == 0) return null;

        Canvas firstActive = null;
        foreach (var c in canvases)
        {
            if (c == null || !c.gameObject.activeInHierarchy) continue;
            if (c.GetComponentInChildren<Image>(true) != null || c.GetComponentInChildren<RawImage>(true) != null)
                return c;
            if (firstActive == null)
                firstActive = c;
        }

        return firstActive ?? canvases[0];
    }

    private static TMP_Text FindReferenceTMP(Transform root)
    {
        foreach (var t in root.GetComponentsInChildren<TMP_Text>(true))
            if (t != null && !string.IsNullOrEmpty(t.text) && t.text.EndsWith("m", StringComparison.Ordinal))
                return t;
        return null;
    }

    private static bool IsAlive(Component c) =>
        c != null && c.gameObject != null && c.gameObject.scene.IsValid();

    private TextMeshProUGUI CreateLabel(Transform root, int receiverId, string labelType,
        string initialText, Vector2 offset, float fontSizeAdjust, TMP_Text reference)
    {
        _stringBuilder.Clear();
        _stringBuilder.Append("ReceiverSpeedometer_");
        _stringBuilder.Append(labelType);
        _stringBuilder.Append("_");
        _stringBuilder.Append(receiverId);

        var go = new GameObject(_stringBuilder.ToString());
        go.transform.SetParent(root, false);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.raycastTarget = false;
        tmp.enableWordWrapping = false;
        tmp.alignment = TextAlignmentOptions.TopRight;
        tmp.text = initialText;
        tmp.color = LabelColor;

        if (reference != null)
        {
            tmp.font = reference.font;
            tmp.fontSharedMaterial = reference.fontSharedMaterial;
            tmp.fontStyle = reference.fontStyle;
            tmp.characterSpacing = reference.characterSpacing;
            tmp.wordSpacing = reference.wordSpacing;
            tmp.lineSpacing = reference.lineSpacing;
            tmp.paragraphSpacing = reference.paragraphSpacing;
            tmp.enableVertexGradient = reference.enableVertexGradient;
            tmp.extraPadding = reference.extraPadding;
            tmp.richText = reference.richText;
            tmp.outlineWidth = reference.outlineWidth;
            tmp.outlineColor = reference.outlineColor;
            tmp.enableAutoSizing = reference.enableAutoSizing;
            tmp.fontSize = reference.fontSize + fontSizeAdjust;
        }
        else
        {
            tmp.enableAutoSizing = false;
            tmp.fontSize = FallbackFontSize;
        }

        var rt = tmp.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = offset;
        rt.sizeDelta = new Vector2(220f, 40f);

        return tmp;
    }

    private TextMeshProUGUI CreateSpeedLabel(Transform root, int receiverId, TMP_Text reference) =>
        CreateLabel(root, receiverId, "SPD", "SPD 0.0 m/s", ReceiverOffset, -1f, reference);

    private TextMeshProUGUI CreateGaugeLabel(Transform root, int receiverId, TMP_Text reference) =>
        CreateLabel(root, receiverId, "Gauge", BuildGaugeString(0), GaugeOffset, -2f, reference);

    private static int GaugeFilled(float speed) =>
        Mathf.Clamp(Mathf.RoundToInt(speed / GaugeMaxSpeed * GaugeBlocks), 0, GaugeBlocks);

    private static string BuildGaugeString(int filled) =>
        $"<mspace={GaugeMspaceEm:0.0}em>{new string('#', filled)}{new string('.', GaugeBlocks - filled)}</mspace>";

    private static bool IsValid(Transform t) =>
        t != null && t.gameObject != null && t.gameObject.scene.IsValid() && t.gameObject.activeInHierarchy;
}
