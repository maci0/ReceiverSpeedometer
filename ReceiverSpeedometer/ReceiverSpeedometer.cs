using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class Main : Mod
{
    // ================= USER TWEAKABLE =================
    private static readonly Vector2 ReceiverOffset = new Vector2(-70f, -65f);
    private static readonly Vector2 GaugeOffset = new Vector2(-60f, -95f);

    private const float SpeedUpdateInterval = 0.15f;
    private const float ReceiverScanInterval = 20.0f;
    private const float RaftPivotScanInterval = 3.0f;

    // Fast bootstrapping when loaded from main menu
    private const float BootstrapScanInterval = 0.5f;     // keep low, runs only until bound
    private const float BootstrapTimeout = 60.0f;         // stop fast scanning after 60s anyway

    private const string SpeedFormat = "SPD {0:0.0} m/s";
    private const float MaxValidSpeed = 500f;
    private const float FallbackFontSize = 20f;

    private const float GaugeMaxSpeed = 8.0f;
    private const int GaugeBlocks = 10;
    private const string GaugePrefix = "";
    private const float GaugeMspaceEm = 0.6f;
    // ==================================================

    private static readonly Color LabelColor = new Color(0.63f, 0.89f, 0.87f, 1.0f);

    private float _nextSpeedUpdate;
    private float _nextReceiverScan;
    private float _nextPivotScan;

    private float _speed;
    private float _smoothedSpeed;
    private string _cachedSpeedText = "SPD 0.0 m/s";
    private string _lastAppliedSpeedText = "";

    private Transform _raftPivot;
    private Vector3 _lastRaftPos;
    private float _lastRaftT;

    private readonly Dictionary<int, TextMeshProUGUI> _receiverSpeed = new Dictionary<int, TextMeshProUGUI>();
    private readonly Dictionary<int, TextMeshProUGUI> _receiverGauge = new Dictionary<int, TextMeshProUGUI>();
    private readonly Dictionary<int, int> _receiverGaugeState = new Dictionary<int, int>();
    private readonly List<int> _deadIds = new List<int>(16);

    private static readonly string[] _gaugeCache = BuildGaugeCache();

    // Bootstrap state
    private bool _bootstrapping = true;
    private float _bootstrapStartT;
    private float _nextBootstrapScan;

    public void Start()
    {
        _bootstrapStartT = Time.time;
        _nextBootstrapScan = 0f;

        // Do not assume we're in game yet
        _raftPivot = null;
        _receiverSpeed.Clear();
        _receiverGauge.Clear();
        _receiverGaugeState.Clear();
    }

    public void Update()
    {
        float now = Time.time;

        // 1) Bootstrap: keep trying to bind quickly until in-game objects exist
        if (_bootstrapping)
        {
            if (now >= _nextBootstrapScan)
            {
                _nextBootstrapScan = now + BootstrapScanInterval;

                if (_raftPivot == null || !IsValid(_raftPivot))
                    FindRaftPivot(true);

                ScanReceivers(false);

                // Stop bootstrapping once we have pivot + at least 1 receiver label, or after timeout
                if (_raftPivot != null && _receiverSpeed.Count > 0)
                {
                    _bootstrapping = false;
                    _nextPivotScan = now + RaftPivotScanInterval;
                    _nextReceiverScan = now + ReceiverScanInterval;
                    _nextSpeedUpdate = now + SpeedUpdateInterval;
                }
                else if (now - _bootstrapStartT > BootstrapTimeout)
                {
                    _bootstrapping = false;
                    _nextPivotScan = now + RaftPivotScanInterval;
                    _nextReceiverScan = now + ReceiverScanInterval;
                    _nextSpeedUpdate = now + SpeedUpdateInterval;
                }
            }

            // Nothing else to do yet
            UpdateLabels(); // harmless, mostly no-op until labels exist
            return;
        }

        // 2) Normal operation
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

            _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, raw, 0.35f);
            _speed = _smoothedSpeed;

            _cachedSpeedText = string.Format(SpeedFormat, _speed);
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
        foreach (var t in _receiverSpeed.Values.ToList())
            if (t != null) Destroy(t.gameObject);
        _receiverSpeed.Clear();

        foreach (var t in _receiverGauge.Values.ToList())
            if (t != null) Destroy(t.gameObject);
        _receiverGauge.Clear();

        _receiverGaugeState.Clear();
        _deadIds.Clear();

        _bootstrapping = true;
        _raftPivot = null;
    }

    // ================= SPEED =================

    private void FindRaftPivot(bool resetBaseline)
    {
        _raftPivot = null;

        foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
        {
            if (t == null) continue;
            if (!t.gameObject.scene.IsValid()) continue;
            if (!t.gameObject.activeInHierarchy) continue;
            if (t.name != "LockedPivot") continue;

            Transform rotatePivot = t.parent;
            if (rotatePivot == null || rotatePivot.name != "RotatePivot")
                continue;

            Transform raft = rotatePivot.parent;
            if (raft == null || !raft.name.StartsWith("Raft", StringComparison.Ordinal))
                continue;

            _raftPivot = t;
            break;
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
        if (_raftPivot == null)
            return 0f;

        float now = Time.time;
        float dt = now - _lastRaftT;
        if (dt <= 0.0001f) dt = 0.0001f;

        Vector3 pos = _raftPivot.position;
        float speed = (pos - _lastRaftPos).magnitude / dt;

        _lastRaftPos = pos;
        _lastRaftT = now;

        if (speed > MaxValidSpeed) return 0f;
        return speed;
    }

    // ================= RECEIVER =================

    private void UpdateLabels()
    {
        _deadIds.Clear();

        bool speedChanged = !string.Equals(_lastAppliedSpeedText, _cachedSpeedText, StringComparison.Ordinal);
        int filled = GaugeFilled(_speed);

        foreach (var kv in _receiverSpeed)
        {
            int id = kv.Key;
            var spd = kv.Value;

            if (!IsAlive(spd))
            {
                _deadIds.Add(id);
                continue;
            }

            if (speedChanged)
                spd.text = _cachedSpeedText;

            if (_receiverGauge.TryGetValue(id, out var g) && IsAlive(g))
            {
                int last;
                if (!_receiverGaugeState.TryGetValue(id, out last) || last != filled)
                {
                    g.text = _gaugeCache[filled];
                    _receiverGaugeState[id] = filled;
                }
            }
        }

        _lastAppliedSpeedText = _cachedSpeedText;

        for (int i = 0; i < _deadIds.Count; i++)
        {
            int id = _deadIds[i];

            _receiverSpeed.Remove(id);

            if (_receiverGauge.TryGetValue(id, out var g) && g != null)
                Destroy(g.gameObject);
            _receiverGauge.Remove(id);

            _receiverGaugeState.Remove(id);
        }
    }

    private void ScanReceivers(bool forceRebind)
    {
        RemoveDeadTracked();

        foreach (Transform tr in Resources.FindObjectsOfTypeAll<Transform>())
        {
            if (tr == null) continue;
            if (!tr.gameObject.scene.IsValid()) continue;
            if (!tr.gameObject.activeInHierarchy) continue;
            if (!tr.name.StartsWith("Placeable_Reciever", StringComparison.Ordinal))
                continue;

            int id = tr.gameObject.GetInstanceID();

            if (!forceRebind && _receiverSpeed.ContainsKey(id) && _receiverGauge.ContainsKey(id))
                continue;

            Canvas canvas = FindReceiverCanvas(tr);
            if (canvas == null)
                continue;

            RemoveForReceiver(id);

            TMP_Text reference = FindReferenceTMP(canvas.transform);

            var spd = CreateSpeedLabel(canvas.transform, id, reference);
            var gauge = CreateGaugeLabel(canvas.transform, id, reference);

            if (spd != null) _receiverSpeed[id] = spd;
            if (gauge != null) _receiverGauge[id] = gauge;

            _receiverGaugeState[id] = -1;
        }
    }

    private void RemoveDeadTracked()
    {
        _deadIds.Clear();

        foreach (var kv in _receiverSpeed)
        {
            if (!IsAlive(kv.Value))
                _deadIds.Add(kv.Key);
        }

        for (int i = 0; i < _deadIds.Count; i++)
        {
            int id = _deadIds[i];

            _receiverSpeed.Remove(id);

            if (_receiverGauge.TryGetValue(id, out var g) && g != null)
                Destroy(g.gameObject);
            _receiverGauge.Remove(id);

            _receiverGaugeState.Remove(id);
        }

        _deadIds.Clear();
    }

    private void RemoveForReceiver(int id)
    {
        if (_receiverSpeed.TryGetValue(id, out var s) && s != null)
            Destroy(s.gameObject);
        _receiverSpeed.Remove(id);

        if (_receiverGauge.TryGetValue(id, out var g) && g != null)
            Destroy(g.gameObject);
        _receiverGauge.Remove(id);

        _receiverGaugeState.Remove(id);
    }

    private static Canvas FindReceiverCanvas(Transform receiver)
    {
        var canvases = receiver.GetComponentsInChildren<Canvas>(true);
        if (canvases == null || canvases.Length == 0)
            return null;

        foreach (var c in canvases)
        {
            if (c == null) continue;
            if (!c.gameObject.activeInHierarchy) continue;

            if (c.GetComponentInChildren<Image>(true) != null || c.GetComponentInChildren<RawImage>(true) != null)
                return c;
        }

        return canvases[0];
    }

    private static TMP_Text FindReferenceTMP(Transform root)
    {
        var tmps = root.GetComponentsInChildren<TMP_Text>(true);
        for (int i = 0; i < tmps.Length; i++)
        {
            var t = tmps[i];
            if (t == null) continue;

            var s = t.text;
            if (!string.IsNullOrEmpty(s) && s.EndsWith("m", StringComparison.Ordinal))
                return t;
        }
        return null;
    }

    private static bool IsAlive(Component component)
    {
        return component != null &&
               component.gameObject != null &&
               component.gameObject.scene.IsValid();
    }

    private static TextMeshProUGUI CreateSpeedLabel(Transform root, int receiverId, TMP_Text reference)
    {
        GameObject go = new GameObject("ReceiverSpeedometer_SPD_" + receiverId);
        go.transform.SetParent(root, false);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.raycastTarget = false;
        tmp.enableWordWrapping = false;
        tmp.alignment = TextAlignmentOptions.TopRight;
        tmp.text = "SPD 0.0 m/s";

        if (reference != null)
        {
            tmp.font = reference.font;
            tmp.fontSharedMaterial = reference.fontSharedMaterial;

            tmp.color = LabelColor;

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
            tmp.fontSize = reference.fontSize - 1f;
        }
        else
        {
            tmp.color = LabelColor;
            tmp.enableAutoSizing = false;
            tmp.fontSize = FallbackFontSize;
        }

        RectTransform rt = tmp.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = ReceiverOffset;
        rt.sizeDelta = new Vector2(220f, 40f);

        return tmp;
    }

    private static TextMeshProUGUI CreateGaugeLabel(Transform root, int receiverId, TMP_Text reference)
    {
        GameObject go = new GameObject("ReceiverSpeedometer_Gauge_" + receiverId);
        go.transform.SetParent(root, false);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.raycastTarget = false;
        tmp.enableWordWrapping = false;
        tmp.alignment = TextAlignmentOptions.TopRight;
        tmp.text = _gaugeCache[0];

        if (reference != null)
        {
            tmp.font = reference.font;
            tmp.fontSharedMaterial = reference.fontSharedMaterial;

            tmp.color = LabelColor;

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
            tmp.fontSize = reference.fontSize - 2f;
        }
        else
        {
            tmp.color = LabelColor;
            tmp.enableAutoSizing = false;
            tmp.fontSize = FallbackFontSize;
        }

        RectTransform rt = tmp.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = GaugeOffset;
        rt.sizeDelta = new Vector2(220f, 40f);

        return tmp;
    }

    private static int GaugeFilled(float speed)
    {
        float t = Mathf.Clamp01(speed / Mathf.Max(0.001f, GaugeMaxSpeed));
        int filled = Mathf.RoundToInt(t * GaugeBlocks);
        if (filled < 0) return 0;
        if (filled > GaugeBlocks) return GaugeBlocks;
        return filled;
    }

    private static string[] BuildGaugeCache()
    {
        var arr = new string[GaugeBlocks + 1];
        string em = GaugeMspaceEm.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        for (int filled = 0; filled <= GaugeBlocks; filled++)
        {
            string bar = new string('#', filled) + new string('.', GaugeBlocks - filled);
            arr[filled] = "<mspace=" + em + "em>" + GaugePrefix + bar + "</mspace>";
        }
        return arr;
    }

    private static bool IsValid(Transform t)
    {
        return t != null &&
               t.gameObject != null &&
               t.gameObject.scene.IsValid() &&
               t.gameObject.activeInHierarchy;
    }
}
