using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
using UnityEngine.SceneManagement;
using Il2CppInterop.Runtime.Injection;
using System;
using System.Collections.Generic;
using Object = UnityEngine.Object;

namespace ManagerCameraMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class ManagerCameraMod : BasePlugin
{
    internal new static ManualLogSource Log;

    public override void Load()
    {
        // Plugin startup logic
        Log = base.Log;
        Log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");

        // IL2CPP: register our managed class before using AddComponent<T>()
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<ManagerCameraBootstrap>();
        }
        catch (System.Exception)
        {
            return;
        }

        // Bootstrap a persistent script that will attach the main camera
        try
        {
            var go = new GameObject("ManagerCameraBootstrap");
            Object.DontDestroyOnLoad(go);
            var component = go.AddComponent<ManagerCameraBootstrap>();
            component.Init(Log);
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Error creating ManagerCameraBootstrap GameObject/component: {ex}");
        }
    }
}

public class ManagerCameraBootstrap : MonoBehaviour
{
    private const string MatchBuilderRoot = "MatchPlaybackController/MatchComponents/Match3DBuilder";
    private const string BallPath = "MatchPlaybackController/MatchComponents/Match3DBuilder/BallPrefab(Clone)";
    private const string GameScene = "MatchPlayback";
    
    public KeyCode normalCameraKey = KeyCode.F1;
    public KeyCode ballCarrierPovKey = KeyCode.F2;
    public KeyCode previousPlayerKey = KeyCode.F3;
    public KeyCode nextPlayerKey = KeyCode.F4;
    public bool copySettingsFromMain = true;

    private ManualLogSource _logger;

    private Transform _ball;
    private Camera _lastMainCamera;
    private Camera _customCamera;
    private bool _customCameraActive;
    private bool _originalMainEnabled = true;
    private string _lastSceneName = string.Empty;
    private Transform _matchBuilder;
    private CameraMode _cameraMode = CameraMode.None;
    private bool _autoFollowBallCarrier;
    private readonly List<PlayerAvatar> _players = new();
    private PlayerAvatar _currentPlayer;
    private int _currentPlayerIndex = -1;
    private float _nextPlayerScanTime;
    private GUIStyle _hudStyle;
    private bool _loggedMissingPlayerOnce;
    private bool _legacyInputUnavailable;
    
    // Reflection caches for Unity Input System (if present)
    private System.Type _tKeyboard;
    private System.Type _tKeyEnum;
    private System.Type _tKeyControl;
    private System.Reflection.PropertyInfo _currentInput;
    private System.Reflection.PropertyInfo _currentInputItem;
    private System.Reflection.PropertyInfo _currentInputWasPressed;

    private enum CameraMode
    {
        None = 0,
        PlayerPov = 1
    }

    private class PlayerAvatar
    {
        public Transform Root;
        public Transform Head;
        public string DisplayName;
        public int InstanceId;

        public bool IsValid => Root != null;
    }

    public void Init(ManualLogSource logger)
    {
        _logger = logger;
    }
    
    private void Update()
    {
        var activeScene = SceneManager.GetActiveScene();

        // TODO: Temp fix: Detect scene changes without using SceneManager.activeSceneChanged (IL2CPP issues)
        if (activeScene.IsValid())
        {
            var sceneName = activeScene.name;
            if (_lastSceneName != sceneName)
            {
                if (_customCameraActive) DisableCustomCamera(true);

                _lastSceneName = sceneName;
                _ball = null;
                _lastMainCamera = null;
                _matchBuilder = null;
                _players.Clear();
                _currentPlayer = null;
                _currentPlayerIndex = -1;
                _cameraMode = CameraMode.None;
            }
        }

        // Only operate in the MatchPlayback scene.
        // TODO: Test removing this as the scene is destroyed the camera will be too (remove DontDestroyOnLoad)
        if (!activeScene.IsValid() || activeScene.name != GameScene)
        {
            if (_customCameraActive)
                DisableCustomCamera(true);
            return;
        }

        if (NewInputWasPressedThisFrame(normalCameraKey))
        {
            if (_customCameraActive) DisableCustomCamera(false);
        }

        if (NewInputWasPressedThisFrame(ballCarrierPovKey))
        {
            ActivatePlayerPov(autoFollowBallCarrier: true);
        }

        if (NewInputWasPressedThisFrame(previousPlayerKey))
        {
            CyclePlayer(-1);
        }

        if (NewInputWasPressedThisFrame(nextPlayerKey))
        {
            CyclePlayer(1);
        }

        // While active, keep aiming at the ball; if anything disappears, auto-deactivate
        if (_customCameraActive)
        {
            switch (_cameraMode)
            {
                case CameraMode.PlayerPov:
                    UpdatePlayerPovCamera();
                    break;
            }
        }
    }
    

    // TODO: Ugly reflection, ideally i would use Unity's InputSystem API directly', not sure why that's not working
    private bool TryInitInputSystem()
    {
        if (_tKeyboard != null && _tKeyEnum != null && _tKeyControl != null && _currentInput != null && _currentInputItem != null && _currentInputWasPressed != null)
            return true;

        try
        {
            _tKeyboard = System.Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
            _tKeyEnum = System.Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem");
            _tKeyControl = System.Type.GetType("UnityEngine.InputSystem.Controls.KeyControl, Unity.InputSystem");
            if (_tKeyboard == null || _tKeyEnum == null || _tKeyControl == null)
                return false;

            _currentInput = _tKeyboard.GetProperty("current", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            _currentInputItem = _tKeyboard.GetProperty("Item", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            _currentInputWasPressed = _tKeyControl.GetProperty("wasPressedThisFrame", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            return _currentInput != null && _currentInputItem != null && _currentInputWasPressed != null;
        }
        catch
        {
            return false;
        }
    }

    private bool NewInputWasPressedThisFrame(KeyCode keyCode)
    {
        if (TryGetInputSystemKeyDown(keyCode, out var pressed))
            return pressed;

        if (_legacyInputUnavailable)
            return false;

        try
        {
            return UnityEngine.Input.GetKeyDown(keyCode);
        }
        catch (Exception ex)
        {
            _legacyInputUnavailable = true;
            ManagerCameraMod.Log?.LogWarning($"ManagerCameraBootstrap: Legacy Input.GetKeyDown unavailable ({ex.Message}); relying on new Input System only.");
            return false;
        }
    }

    private bool TryGetInputSystemKeyDown(KeyCode keyCode, out bool pressed)
    {
        pressed = false;

        if (!TryInitInputSystem()) return false;

        var keyboard = _currentInput.GetValue(null, null);
        if (keyboard == null) return false;

        // Map KeyCode to InputSystem.Key
        object keyEnumValue = null;
        try
        {
            keyEnumValue = System.Enum.Parse(_tKeyEnum, keyCode.ToString(), ignoreCase: true);
        }
        catch
        {
            return false;
        }

        // Access indexer: Keyboard[key]
        var keyControl = _currentInputItem.GetValue(keyboard, new object[] { keyEnumValue });
        if (keyControl == null) return false;

        var value = _currentInputWasPressed.GetValue(keyControl, null);
        pressed = value is bool b && b;
        return true;
    }

    private void ActivatePlayerPov(bool autoFollowBallCarrier)
    {
        if (!EnableCustomCamera())
            return;

        var stateChanged = _cameraMode != CameraMode.PlayerPov || !_customCameraActive || _autoFollowBallCarrier != autoFollowBallCarrier;

        _cameraMode = CameraMode.PlayerPov;
        _autoFollowBallCarrier = autoFollowBallCarrier;

        if (autoFollowBallCarrier)
        {
            _currentPlayer = null;
            _currentPlayerIndex = -1;
        }

        EnsurePlayerList(forceRefresh: autoFollowBallCarrier);

        if (!_autoFollowBallCarrier && _players.Count > 0)
        {
            if (_currentPlayerIndex < 0 || _currentPlayerIndex >= _players.Count)
                _currentPlayerIndex = 0;

            _currentPlayer = _players[_currentPlayerIndex];
        }

        if (stateChanged)
        {
            var modeLabel = autoFollowBallCarrier ? "ball-carrier auto follow" : "manual selection";
            ManagerCameraMod.Log?.LogInfo($"ManagerCameraBootstrap: Player POV camera active ({modeLabel}).");
        }
    }

    private void CyclePlayer(int direction)
    {
        if (direction == 0)
            return;

        EnsurePlayerList(forceRefresh: _players.Count == 0);

        if (_players.Count == 0)
        {
            ManagerCameraMod.Log?.LogWarning("ManagerCameraBootstrap: No players found to cycle POV camera.");
            return;
        }

        if (!_customCameraActive || _cameraMode != CameraMode.PlayerPov || _autoFollowBallCarrier)
        {
            ActivatePlayerPov(autoFollowBallCarrier: false);
        }

        if (_players.Count == 0)
            return;

        if (_currentPlayerIndex < 0 || _currentPlayerIndex >= _players.Count)
        {
            _currentPlayerIndex = direction > 0 ? 0 : _players.Count - 1;
        }
        else
        {
            _currentPlayerIndex = (_currentPlayerIndex + direction) % _players.Count;
            if (_currentPlayerIndex < 0)
                _currentPlayerIndex += _players.Count;
        }

        _currentPlayer = _players[_currentPlayerIndex];
        _autoFollowBallCarrier = false;
        ManagerCameraMod.Log?.LogInfo($"ManagerCameraBootstrap: POV switched to {_currentPlayer.DisplayName}.");
    }

    private void UpdatePlayerPovCamera()
    {
        if (!_customCameraActive || _customCamera == null)
            return;

        EnsureBallReference();
        EnsurePlayerList();

        if (_autoFollowBallCarrier)
        {
            var carrier = FindClosestPlayerToBall();
            if (carrier != null)
            {
                SetCurrentPlayer(carrier);
            }
        }

        if (_currentPlayer == null || !_currentPlayer.IsValid)
        {
            if (!_loggedMissingPlayerOnce)
            {
                ManagerCameraMod.Log?.LogWarning("ManagerCameraBootstrap: POV target missing; awaiting next valid player.");
                _loggedMissingPlayerOnce = true;
            }

            if (_ball != null)
            {
                var fallbackPosition = _ball.position + Vector3.up * 1.6f;
                _customCamera.transform.position = fallbackPosition;
                _customCamera.transform.LookAt(_ball);
            }
            return;
        }

        _loggedMissingPlayerOnce = false;

        // Anchor the POV at the player's head and look towards the active ball.
        var headPosition = GetPlayerHeadPosition(_currentPlayer);
        var forward = _currentPlayer.Root != null ? _currentPlayer.Root.forward : Vector3.forward;
        var cameraPosition = headPosition - forward * 0.08f + Vector3.up * 0.02f;

        _customCamera.transform.position = cameraPosition;

        Vector3 lookTarget;
        if (_ball != null)
        {
            lookTarget = _ball.position;
        }
        else
        {
            lookTarget = headPosition + forward * 5f;
        }

        var lookDirection = lookTarget - headPosition;
        if (lookDirection.sqrMagnitude < 0.0001f)
        {
            lookDirection = forward;
        }

        _customCamera.transform.rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
    }

    private void EnsureBallReference()
    {
        if (_ball != null)
        {
            var ballObject = _ball.gameObject;
            if (ballObject == null || !ballObject.activeInHierarchy)
            {
                _ball = null;
            }
        }

        if (_ball == null)
        {
            var ballGo = GameObject.Find(BallPath);
            _ball = ballGo != null ? ballGo.transform : null;
        }
    }

    private void EnsurePlayerList(bool forceRefresh = false)
    {
        var now = Time.time;
        if (forceRefresh || _players.Count == 0 || now >= _nextPlayerScanTime)
        {
            _nextPlayerScanTime = now + 1.5f;
            RefreshPlayerList();
        }
        else
        {
            _players.RemoveAll(p => !p.IsValid);
        }
    }

    private void RefreshPlayerList()
    {
        try
        {
            if (_matchBuilder == null)
            {
                var builderGo = GameObject.Find(MatchBuilderRoot);
                _matchBuilder = builderGo != null ? builderGo.transform : null;
            }

            if (_matchBuilder == null)
                return;

            var candidates = new List<PlayerAvatar>();
            var seen = new HashSet<int>();

            foreach (var transform in EnumerateHierarchy(_matchBuilder, includeInactive: true))
            {
                if (transform == _matchBuilder)
                    continue;

                var animator = SafeGetComponent<Animator>(transform);
                if (animator == null)
                    continue;

                var root = animator.transform;
                if (!root.gameObject.activeInHierarchy)
                    continue;

                if (!IsLikelyPlayer(root))
                    continue;

                var instanceId = root.gameObject.GetInstanceID();
                if (!seen.Add(instanceId))
                    continue;

        var head = TryGetHead(animator);
        if (head == null)
            head = FindHeadTransform(root);

                var displayName = CleanPlayerName(root.gameObject.name);
                candidates.Add(new PlayerAvatar
                {
                    Root = root,
                    Head = head,
                    DisplayName = displayName,
                    InstanceId = instanceId
                });
            }

            if (candidates.Count > 0)
            {
                candidates.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
                _players.Clear();
                _players.AddRange(candidates);

                if (_currentPlayer != null)
                {
                    var index = _players.FindIndex(p => p.InstanceId == _currentPlayer.InstanceId);
                    if (index >= 0)
                    {
                        _currentPlayerIndex = index;
                        _currentPlayer = _players[index];
                    }
                    else if (_autoFollowBallCarrier)
                    {
                        _currentPlayer = null;
                        _currentPlayerIndex = -1;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ManagerCameraMod.Log?.LogError($"ManagerCameraBootstrap: RefreshPlayerList failed: {ex}");
        }
    }

    private PlayerAvatar FindClosestPlayerToBall()
    {
        if (_ball == null || _players.Count == 0)
            return null;

        var ballPosition = _ball.position;
        PlayerAvatar closest = null;
        var bestDistance = float.MaxValue;

        foreach (var player in _players)
        {
            if (!player.IsValid)
                continue;

            var head = GetPlayerHeadPosition(player);
            var distance = (head - ballPosition).sqrMagnitude;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                closest = player;
            }
        }

        return closest;
    }

    private void SetCurrentPlayer(PlayerAvatar candidate)
    {
        if (candidate == null || !candidate.IsValid)
            return;

        var index = _players.FindIndex(p => p.InstanceId == candidate.InstanceId);
        if (index >= 0)
        {
            _currentPlayerIndex = index;
            _currentPlayer = _players[index];
        }
        else
        {
            _currentPlayer = candidate;
            _currentPlayerIndex = -1;
        }
    }

    private Vector3 GetPlayerHeadPosition(PlayerAvatar player)
    {
        if (player == null)
            return Vector3.zero;

        if (player.Head != null)
            return player.Head.position;

        if (player.Root != null)
            return player.Root.position + Vector3.up * 1.7f;

        return Vector3.zero;
    }

    private Transform FindHeadTransform(Transform root)
    {
        if (root == null)
            return null;

        foreach (var child in EnumerateHierarchy(root, includeInactive: true))
        {
            if (child == root)
                continue;

            if (string.IsNullOrEmpty(child.name))
                continue;

            if (child.name.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0)
                return child;
        }

        return null;
    }

    private bool IsLikelyPlayer(Transform candidate)
    {
        if (candidate == null)
            return false;

        if (MatchesPlayerKeyword(candidate.name))
            return true;

        var parent = candidate.parent;
        var depth = 0;
        while (parent != null && depth < 3)
        {
            if (MatchesPlayerKeyword(parent.name))
                return true;
            parent = parent.parent;
            depth++;
        }

        return false;
    }

    private bool MatchesPlayerKeyword(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        var lower = value.ToLowerInvariant();
        return lower.Contains("player") ||
               lower.Contains("goalkeeper") ||
               lower.Contains("goalie") ||
               lower.Contains("forward") ||
               lower.Contains("striker") ||
               lower.Contains("winger") ||
               lower.Contains("midfield") ||
               lower.Contains("midfielder") ||
               lower.Contains("defender") ||
               lower.Contains("attacker");
    }

    private string CleanPlayerName(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "Unknown Player";

        var cleaned = raw.Replace("(Clone)", string.Empty);
        cleaned = cleaned.Replace("_", " ");
        return cleaned.Trim();
    }

    private Transform TryGetHead(Animator animator)
    {
        if (animator == null)
            return null;

        try
        {
            if (animator.isHuman)
            {
                var head = animator.GetBoneTransform(HumanBodyBones.Head);
                if (head != null)
                    return head;
            }
        }
        catch
        {
            // ignore IL2CPP issues
        }

        return null;
    }

    private IEnumerable<Transform> EnumerateHierarchy(Transform root, bool includeInactive)
    {
        if (root == null)
            yield break;

        var stack = new Stack<Transform>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            if (current != root)
            {
                if (includeInactive || current.gameObject.activeInHierarchy)
                    yield return current;
            }

            for (int i = current.childCount - 1; i >= 0; i--)
            {
                var child = current.GetChild(i);
                if (child != null)
                    stack.Push(child);
            }
        }
    }

    private T SafeGetComponent<T>(Transform transform) where T : Component
    {
        if (transform == null)
            return null;

        try
        {
            return transform.GetComponent<T>();
        }
        catch
        {
            return null;
        }
    }

    private void OnGUI()
    {
        if (!_customCameraActive || _cameraMode != CameraMode.PlayerPov)
            return;

        if (_currentPlayer == null || string.IsNullOrEmpty(_currentPlayer.DisplayName))
            return;

        if (_hudStyle == null)
        {
            _hudStyle = new GUIStyle();

            if (GUI.skin != null && GUI.skin.label != null)
            {
                _hudStyle.font = GUI.skin.label.font;
                _hudStyle.richText = GUI.skin.label.richText;
            }

            _hudStyle.fontSize = 24;
            _hudStyle.alignment = TextAnchor.UpperCenter;
            _hudStyle.fontStyle = FontStyle.Bold;
            _hudStyle.normal.textColor = Color.white;
        }

        var width = 320f;
        var rect = new Rect((Screen.width - width) * 0.5f, 20f, width, 28f);
        GUI.Label(rect, _currentPlayer.DisplayName, _hudStyle);
    }
    
    private bool EnableCustomCamera()
    {
        try
        {
            _lastMainCamera = Camera.main;
            EnsureBallReference();

            if (_customCamera == null)
            {
                var go = new GameObject("ManagerCam_Custom");
                _customCamera = go.AddComponent<Camera>();
                DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.tag = "MainCamera";
            }

            if (copySettingsFromMain && _lastMainCamera != null)
            {
                try
                {
                    _customCamera.fieldOfView = _lastMainCamera.fieldOfView;
                    _customCamera.nearClipPlane = _lastMainCamera.nearClipPlane;
                    _customCamera.farClipPlane = _lastMainCamera.farClipPlane;
                    _customCamera.allowHDR = _lastMainCamera.allowHDR;
                    _customCamera.allowMSAA = _lastMainCamera.allowMSAA;
                    _customCamera.clearFlags = _lastMainCamera.clearFlags;
                    _customCamera.backgroundColor = _lastMainCamera.backgroundColor;
                    _customCamera.cullingMask = _lastMainCamera.cullingMask;
                    _customCamera.depth = _lastMainCamera.depth + 1f;
                }
                catch
                {
                    /* ignore per-field copy issues on IL2CPP */
                }
            }

            _customCamera.transform.SetParent(null, worldPositionStays: true);

            if (_lastMainCamera != null)
            {
                _originalMainEnabled = _lastMainCamera.enabled;
                _lastMainCamera.enabled = false;
            }

            _customCamera.gameObject.SetActive(true);
            _customCamera.enabled = true;
            _customCameraActive = true;
            return true;
        }
        catch (Exception ex)
        {
            ManagerCameraMod.Log?.LogError($"ManagerCameraBootstrap: EnableCustomCamera failed: {ex}");
            _customCameraActive = false;
            return false;
        }
    }

    private void DisableCustomCamera(bool dueToSceneChange)
    {
        try
        {
            if (_customCamera != null)
            {
                _customCamera.enabled = false;
                _customCamera.gameObject.SetActive(false);
                _customCamera.transform.SetParent(null, worldPositionStays: true);
            }

            if (_lastMainCamera != null)
            {
                try
                {
                    _lastMainCamera.enabled = _originalMainEnabled;
                }
                catch
                {
                }
            }

            _customCameraActive = false;
            _cameraMode = CameraMode.None;
            _autoFollowBallCarrier = false;
            _currentPlayer = null;
            _currentPlayerIndex = -1;

            if (dueToSceneChange)
                ManagerCameraMod.Log?.LogInfo("ManagerCameraBootstrap: Scene change detected - custom camera deactivated.");
            else
                ManagerCameraMod.Log?.LogInfo("ManagerCameraBootstrap: Custom camera deactivated; restored game camera.");
        }
        catch (Exception ex)
        {
            ManagerCameraMod.Log?.LogError($"ManagerCameraBootstrap: DisableCustomCamera failed: {ex}");
        }
        finally
        {
            _ball = null;
            _lastMainCamera = null;
            _matchBuilder = null;
        }
    }
}
