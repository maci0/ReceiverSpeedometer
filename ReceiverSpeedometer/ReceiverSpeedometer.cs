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

    private const string SpeedFormat = "SPD {0:0.0} m/s";
    private const float MaxValidSpeed = 500f;
    private const float FallbackFontSize = 20f;

    private const float GaugeMaxSpeed = 8.0f;   // full-scale (m/s)
    private const int GaugeBlocks = 10;         // width of bar
    private const string GaugePrefix = "";      // e.g. "G "
    // ==================================================

    private float _nextSpeedUpdate;
    private float _nextReceiverScan;
    private float _nextPivotScan;

    private float _speed;
    private string _cachedSpeedText = "SPD 0.0 m/s";

    private Transform _raftPivot;
    private Vector3 _lastRaftPos;
    private float _lastRaftT;

    private readonly Dictionary<int, TextMeshProUGUI> _receiverSpeed = new Dictionary<int, TextMeshProUGUI>();
    private readonly Dictionary<int, TextMeshProUGUI> _receiverGauge = new Dictionary<int, TextMeshProUGUI>();

    public void Start()
    {
        FindRaftPivot(true);
        ScanReceivers(true);
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
            _speed = ComputeRaftSpeed();
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
        }
    }

    private float ComputeRaftSpeed()
    {
        if (_raftPivot == null)
            return 0f;

        float now = Time.time;
        float dt = Mathf.Max(0.0001f, now - _lastRaftT);

        Vector3 pos = _raftPivot.position;
        float speed = (pos - _lastRaftPos).magnitude / dt;

        _lastRaftPos = pos;
        _lastRaftT = now;

        return speed > MaxValidSpeed ? 0f : speed;
    }

    // ================= RECEIVER =================

    private void UpdateLabels()
    {
        var dead = new List<int>();

        foreach (var kv in _receiverSpeed)
        {
            var spd = kv.Value;
            if (spd == null || spd.gameObject == null || !spd.gameObject.scene.IsValid())
            {
                dead.Add(kv.Key);
                continue;
            }

            spd.text = _cachedSpeedText;

            if (_receiverGauge.TryGetValue(kv.Key, out var g) && g != null && g.gameObject.scene.IsValid())
                g.text = MakeGauge(_speed);
        }

        foreach (int id in dead)
        {
            _receiverSpeed.Remove(id);

            if (_receiverGauge.TryGetValue(id, out var g) && g != null)
                Destroy(g.gameObject);
            _receiverGauge.Remove(id);
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
        }
    }

    private void RemoveDeadTracked()
    {
        var dead = new List<int>();

        foreach (var kv in _receiverSpeed)
        {
            if (kv.Value == null || kv.Value.gameObject == null || !kv.Value.gameObject.scene.IsValid())
                dead.Add(kv.Key);
        }

        foreach (int id in dead)
        {
            _receiverSpeed.Remove(id);

            if (_receiverGauge.TryGetValue(id, out var g) && g != null)
                Destroy(g.gameObject);
            _receiverGauge.Remove(id);
        }
    }

    private void RemoveForReceiver(int id)
    {
        if (_receiverSpeed.TryGetValue(id, out var s) && s != null)
            Destroy(s.gameObject);
        _receiverSpeed.Remove(id);

        if (_receiverGauge.TryGetValue(id, out var g) && g != null)
            Destroy(g.gameObject);
        _receiverGauge.Remove(id);
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

            // Your tuned tint
            tmp.color = new Color(0.63f, 0.89f, 0.87f, 1.0f);

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
            tmp.color = new Color(0.63f, 0.89f, 0.87f, 1.0f);
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
        tmp.text = MakeGauge(0f);
        

        if (reference != null)
        {
            tmp.font = reference.font;
            tmp.fontSharedMaterial = reference.fontSharedMaterial;

            // Match SPD tint (keeps it consistent)
            tmp.color = new Color(0.63f, 0.89f, 0.87f, 1.0f);

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
            tmp.color = new Color(0.63f, 0.89f, 0.87f, 1.0f);
            tmp.enableAutoSizing = false;
            tmp.fontSize = FallbackFontSize;
        }
        //tmp.characterSpacing = 26f; 

        RectTransform rt = tmp.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = GaugeOffset;
        rt.sizeDelta = new Vector2(220f, 40f);

        return tmp;
    }

    private static string MakeGauge(float speed)
    {
        float t = Mathf.Clamp01(speed / Mathf.Max(0.001f, GaugeMaxSpeed));
        int filled = Mathf.RoundToInt(t * GaugeBlocks);

        const char full = '#';
        const char empty = '.';

        string bar = new string(full, filled) + new string(empty, GaugeBlocks - filled);

        // 1em is close to TMP default. Adjust slightly if needed (0.9em–1.1em).
        return "<mspace=0.6em>" + GaugePrefix + bar + "</mspace>";
    }


    private static bool IsValid(Transform t)
    {
        return t != null &&
               t.gameObject != null &&
               t.gameObject.scene.IsValid() &&
               t.gameObject.activeInHierarchy;
    }
}
