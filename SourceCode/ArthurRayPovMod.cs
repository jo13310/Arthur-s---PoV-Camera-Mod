using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
using UnityEngine.SceneManagement;
using Il2CppInterop.Runtime.Injection;
using System;
using System.Collections.Generic;
using Object = UnityEngine.Object;

namespace ArthurRayPovMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class ArthurRayPovMod : BasePlugin
{
    internal new static ManualLogSource Log;

    public override void Load()
    {
        // Plugin startup logic
        Log = base.Log;
        Log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
        Log.LogInfo("Credits: GerKo & Brululul");

        // IL2CPP: register our managed class before using AddComponent<T>()
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<ArthurRayPovBootstrap>();
        }
        catch (System.Exception)
        {
            return;
        }

        // Bootstrap a persistent script that will attach the main camera
        try
        {
            var go = new GameObject("ArthurRayPovBootstrap");
            Object.DontDestroyOnLoad(go);
            var component = go.AddComponent<ArthurRayPovBootstrap>();
            component.Init(Log);
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Error creating ArthurRayPovBootstrap GameObject/component: {ex}");
        }
    }
}

public class ArthurRayPovBootstrap : MonoBehaviour
{
    private const string MatchBuilderRoot = "MatchPlaybackController/MatchComponents/Match3DBuilder";
    private static readonly string[] BallObjectPaths =
    {
        "MatchPlaybackController/MatchComponents/Match3DBuilder/BallPrefab(Clone)",
        "MatchPlaybackController/MatchComponents/Match3DBuilder/BallPrefab"
    };

    private static readonly string[] BallObjectNames =
    {
        "BallPrefab(Clone)",
        "BallPrefab"
    };
    private const string GameScene = "MatchPlayback";
    private const float PlayerPovForwardOffset = 4.6f;
    private const float PlayerPovVerticalOffset = 0.9f;
    private const float PlayerPovLookHeightOffset = 1.8f;
    private const float ManagerPovForwardOffset = -0.12f;
    private const float ManagerPovVerticalOffset = 0.05f;
    private const float ManagerSidelineOffset = 18f;
    private const float ManagerSidelineBackOffset = 4f;
    private const float ManagerLookHeightOffset = 0.15f;
    private const float ManagerSidelineHeight = 2.2f;
    private const float ManagerBallFollowSmoothTime = 0.35f;
    
    public KeyCode normalCameraKey = KeyCode.F1;
    public KeyCode ballCarrierPovKey = KeyCode.F2;
    public KeyCode previousPlayerKey = KeyCode.F3;
    public KeyCode nextPlayerKey = KeyCode.F4;
    public KeyCode managerPovKey = KeyCode.F5;
    public bool copySettingsFromMain = true;

    private ManualLogSource _logger;

    private Transform _ball;
    private Camera _lastMainCamera;
    private Camera _customCamera;
    private bool _customCameraActive;
    private bool _originalMainEnabled = true;
    private string _originalMainTag = "MainCamera";
    private string _lastSceneName = string.Empty;
    private Transform _matchBuilder;
    private CameraMode _cameraMode = CameraMode.None;
    private bool _autoFollowBallCarrier;
    private readonly List<Avatar> _players = new();
    private readonly List<Avatar> _managers = new();
    private Avatar _currentPlayer;
    private Avatar _currentManager;
    private int _currentPlayerIndex = -1;
    private int _currentManagerIndex = -1;
    private float _nextPlayerScanTime;
    private float _nextManagerScanTime;
    private GUIStyle _hudStyle;
    private bool _loggedMissingPlayerOnce;
    private bool _loggedMissingManagerOnce;
    private bool _legacyInputUnavailable;
    private bool _lastMatch3DActive;
    private bool _hasSeenBall;
    private bool _hasSmoothedBall;
    private Vector3 _smoothedBallPosition;
    private float _ballLastActiveTime;
    private const float BallStateGraceSeconds = 0.5f;
    private bool _restoreOnNext3D;
    private CustomCameraState _pendingRestoreState;

    // Auto-trigger event system fields
    public KeyCode toggleAutoTriggerKey = KeyCode.F6;
    private bool _autoTriggerEnabled = true;
    private int _lastProcessedEventFrame = -1;
    private object _matchEventList;
    private object _matchObject;
    private System.Type _tMatchEventList;
    private System.Type _tMatchEventData;
    private System.Type _tMatchEventType;
    private System.Type _tMatch;
    private System.Reflection.PropertyInfo _matchEventDataProperty;
    private System.Reflection.PropertyInfo _matchFrameProperty;
    private System.Reflection.PropertyInfo _matchEventTypeProperty;
    private bool _wasInReplay;
    private float _replayDetectionCooldown;

    // Auto-POV state management
    private bool _autoModeCameraActive;
    private float _originalMatchSpeed = 1.0f;
    private int _eventStartTimeSlice;
    private int _currentSetPieceType; // SetPieceType as int
    private GUIStyle _recStyle;
    private Vector3 _cameraVelocity;
    private float _eventCompletionTimeout;
    private const float EventTimeoutSeconds = 5f;

    // Reflection for MatchPreferences speed control
    private object _matchPreferencesObject;
    private System.Reflection.MethodInfo _getMatchSpeedMethod;
    private System.Reflection.MethodInfo _setMatchSpeedMethod;

    // Reflection for StopTimeInfo access
    private System.Reflection.FieldInfo _stopTimeInfosField;
    private System.Type _tStopTimeInfo;
    private System.Reflection.FieldInfo _stopTimeSliceField;
    private System.Reflection.FieldInfo _setPieceTypeField;

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
        PlayerPov = 1,
        ManagerPov = 2
    }

    private struct CustomCameraState
    {
        public CameraMode Mode;
        public bool AutoFollowBallCarrier;
        public int PlayerInstanceId;
        public int ManagerInstanceId;
        public bool HasState;
    }

    private class Avatar
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
                if (_customCameraActive)
                {
                    ClearPendingRestore();
                    DisableCustomCamera(true);
                }

                _lastSceneName = sceneName;
                _ball = null;
                _lastMainCamera = null;
                _matchBuilder = null;
                _players.Clear();
                _currentPlayer = null;
                _currentPlayerIndex = -1;
                _cameraMode = CameraMode.None;
                _lastMatch3DActive = false;
                _hasSeenBall = false;
                _hasSmoothedBall = false;
                _smoothedBallPosition = Vector3.zero;
                _ballLastActiveTime = 0f;
            }
        }

        // Only operate in the MatchPlayback scene.
        // TODO: Test removing this as the scene is destroyed the camera will be too (remove DontDestroyOnLoad)
        if (!activeScene.IsValid() || activeScene.name != GameScene)
        {
            if (_customCameraActive)
            {
                ClearPendingRestore();
                DisableCustomCamera(true);
            }
            _lastMatch3DActive = false;
            _hasSeenBall = false;
            _hasSmoothedBall = false;
            _smoothedBallPosition = Vector3.zero;
            _ballLastActiveTime = 0f;
            return;
        }

        var match3DActive = IsMatch3DViewActive();
        if (!match3DActive && _lastMatch3DActive && _customCameraActive)
        {
            RememberCurrentCustomState();
            _restoreOnNext3D = _pendingRestoreState.HasState;
            if (_restoreOnNext3D)
                ArthurRayPovMod.Log?.LogInfo("ArthurRayPovBootstrap: Match UI switched to 2D; saving POV for auto-restore.");
            else
                ArthurRayPovMod.Log?.LogInfo("ArthurRayPovBootstrap: Match UI switched to 2D; returning to default camera.");

            DisableCustomCamera(false);
        }
        else if (match3DActive && !_lastMatch3DActive)
        {
            if (_restoreOnNext3D && _pendingRestoreState.HasState)
            {
                RestorePendingCustomState();
            }
            else
            {
                _restoreOnNext3D = false;
                _pendingRestoreState = default;
                ArthurRayPovMod.Log?.LogInfo("ArthurRayPovBootstrap: Match UI switched back to 3D; staying on default camera.");
            }
        }
        _lastMatch3DActive = match3DActive;

        if (NewInputWasPressedThisFrame(normalCameraKey))
        {
            ClearPendingRestore();
            if (_customCameraActive) DisableCustomCamera(false);
        }

        if (NewInputWasPressedThisFrame(ballCarrierPovKey))
        {
            ActivatePlayerPov(autoFollowBallCarrier: true);
        }

        if (NewInputWasPressedThisFrame(managerPovKey))
        {
            ActivateManagerPov();
        }

        if (NewInputWasPressedThisFrame(previousPlayerKey))
        {
            if (_cameraMode == CameraMode.ManagerPov)
                CycleManager(-1);
            else
                CyclePlayer(-1);
        }

        if (NewInputWasPressedThisFrame(nextPlayerKey))
        {
            if (_cameraMode == CameraMode.ManagerPov)
                CycleManager(1);
            else
                CyclePlayer(1);
        }

        // Toggle auto-trigger functionality
        if (NewInputWasPressedThisFrame(toggleAutoTriggerKey))
        {
            _autoTriggerEnabled = !_autoTriggerEnabled;
            ArthurRayPovMod.Log?.LogInfo($"ArthurRayPovBootstrap: Auto-trigger for match events {(_autoTriggerEnabled ? "enabled" : "disabled")}");
        }

        // Check for match events and auto-trigger POV if enabled
        if (_autoTriggerEnabled && match3DActive)
        {
            CheckMatchEventsAndAutoTrigger();
            CheckReplayStateAndAutoTrigger();
        }

        // Auto-return to normal camera when event is complete
        if (_autoModeCameraActive && IsEventComplete())
        {
            ArthurRayPovMod.Log?.LogInfo("ArthurRayPovBootstrap: Event complete, returning to normal camera");

            // Restore match speed
            RestoreMatchSpeed();

            // Deactivate custom camera
            DisableCustomCamera(false);

            _autoModeCameraActive = false;
        }

        // While active, keep aiming at the ball; if anything disappears, auto-deactivate
        if (_customCameraActive)
        {
            switch (_cameraMode)
            {
                case CameraMode.PlayerPov:
                    UpdatePlayerPovCamera();
                    break;
                case CameraMode.ManagerPov:
                    UpdateManagerPovCamera();
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
            ArthurRayPovMod.Log?.LogWarning($"ArthurRayPovBootstrap: Legacy Input.GetKeyDown unavailable ({ex.Message}); relying on new Input System only.");
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
        _currentManager = null;
        _currentManagerIndex = -1;

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
            ArthurRayPovMod.Log?.LogInfo($"ArthurRayPovBootstrap: Player POV camera active ({modeLabel}).");
        }
    }

    private void ActivateManagerPov()
    {
        if (!EnableCustomCamera())
            return;

        var stateChanged = _cameraMode != CameraMode.ManagerPov || !_customCameraActive;

        _cameraMode = CameraMode.ManagerPov;
        _autoFollowBallCarrier = false;
        _currentPlayer = null;
        _currentPlayerIndex = -1;

        EnsureManagerList(forceRefresh: true);

        if (_managers.Count == 0)
        {
            ArthurRayPovMod.Log?.LogWarning("ArthurRayPovBootstrap: No managers found for POV camera.");
            _currentManager = null;
            _currentManagerIndex = -1;
            return;
        }

        if (_currentManagerIndex < 0 || _currentManagerIndex >= _managers.Count)
            _currentManagerIndex = 0;

        _currentManager = _managers[_currentManagerIndex];
        _loggedMissingManagerOnce = false;

        if (stateChanged && _currentManager != null)
        {
            var label = string.IsNullOrEmpty(_currentManager.DisplayName) ? "Manager" : _currentManager.DisplayName;
            ArthurRayPovMod.Log?.LogInfo($"ArthurRayPovBootstrap: Manager POV camera active ({label}).");
        }
    }

    private void CycleManager(int direction)
    {
        if (direction == 0)
            return;

        EnsureManagerList(forceRefresh: _managers.Count == 0);

        if (_managers.Count == 0)
        {
            ArthurRayPovMod.Log?.LogWarning("ArthurRayPovBootstrap: No managers found to cycle POV camera.");
            return;
        }

        if (!_customCameraActive || _cameraMode != CameraMode.ManagerPov)
        {
            ActivateManagerPov();
        }

        if (_managers.Count == 0 || _currentManager == null)
            return;

        if (_currentManagerIndex < 0 || _currentManagerIndex >= _managers.Count)
        {
            _currentManagerIndex = direction > 0 ? 0 : _managers.Count - 1;
        }
        else
        {
            _currentManagerIndex = (_currentManagerIndex + direction) % _managers.Count;
            if (_currentManagerIndex < 0)
                _currentManagerIndex += _managers.Count;
        }

        _currentManager = _managers[_currentManagerIndex];
        _loggedMissingManagerOnce = false;

        var label = string.IsNullOrEmpty(_currentManager.DisplayName) ? "Manager" : _currentManager.DisplayName;
        ArthurRayPovMod.Log?.LogInfo($"ArthurRayPovBootstrap: Manager POV camera active ({label}).");
    }

    private void CyclePlayer(int direction)
    {
        if (direction == 0)
            return;

        EnsurePlayerList(forceRefresh: _players.Count == 0);

        if (_players.Count == 0)
        {
            ArthurRayPovMod.Log?.LogWarning("ArthurRayPovBootstrap: No players found to cycle POV camera.");
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
        ArthurRayPovMod.Log?.LogInfo($"ArthurRayPovBootstrap: POV switched to {_currentPlayer.DisplayName}.");
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
                ArthurRayPovMod.Log?.LogWarning("ArthurRayPovBootstrap: POV target missing; awaiting next valid player.");
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

        var headPosition = GetAvatarHeadPosition(_currentPlayer);
        var forward = _currentPlayer.Root != null ? _currentPlayer.Root.forward : Vector3.forward;
        forward.y = 0f;

        if (forward.sqrMagnitude < 0.0001f)
            forward = Vector3.forward;
        else
            forward.Normalize();

        var cameraPosition = headPosition - forward * PlayerPovForwardOffset + Vector3.up * PlayerPovVerticalOffset;

        _customCamera.transform.position = cameraPosition;

        Vector3 lookTarget;
        if (_ball != null)
        {
            lookTarget = _ball.position + Vector3.up * PlayerPovLookHeightOffset;
        }
        else
        {
            lookTarget = headPosition + forward * 7.5f + Vector3.up * (PlayerPovLookHeightOffset + 0.6f);
        }

        var lookDirection = lookTarget - headPosition;
        if (lookDirection.sqrMagnitude < 0.0001f)
        {
            lookDirection = forward + Vector3.up * PlayerPovLookHeightOffset;
        }

        _customCamera.transform.rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
    }

    private void UpdateManagerPovCamera()
    {
        if (!_customCameraActive || _customCamera == null)
            return;

        EnsureBallReference();
        EnsureManagerList();

        var fallbackFocus = _customCamera.transform.position + _customCamera.transform.forward * 5f;
        var smoothedBallGround = GetStableBallGroundPosition(fallbackFocus);

        var hasAnchor = false;

        if (_managers.Count > 0)
        {
            if (_currentManager == null || !_currentManager.IsValid || _currentManagerIndex < 0 || _currentManagerIndex >= _managers.Count)
            {
                _currentManager = _managers[0];
                _currentManagerIndex = 0;
            }

            if (_currentManager != null && _currentManager.IsValid)
            {
                var headPosition = GetAvatarHeadPosition(_currentManager);
                if (headPosition != Vector3.zero)
                {
                    _loggedMissingManagerOnce = false;

                    var forward = _currentManager.Root != null ? _currentManager.Root.forward : Vector3.forward;
                    forward.y = 0f;
                    if (forward.sqrMagnitude < 0.0001f)
                        forward = Vector3.forward;
                    else
                        forward.Normalize();

                    var cameraPosition = headPosition - forward * ManagerPovForwardOffset + Vector3.up * ManagerPovVerticalOffset;
                    _customCamera.transform.position = cameraPosition;

                    Vector3 lookTarget;
                    if (_ball != null || _hasSmoothedBall)
                        lookTarget = smoothedBallGround + Vector3.up * ManagerLookHeightOffset;
                    else
                        lookTarget = headPosition + forward * 5f;

                    var lookDirection = lookTarget - headPosition;
                    if (lookDirection.sqrMagnitude < 0.0001f)
                    {
                        lookDirection = forward;
                    }

                    _customCamera.transform.rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
                    hasAnchor = true;
                }
                else if (!_loggedMissingManagerOnce)
                {
                    ArthurRayPovMod.Log?.LogWarning("ArthurRayPovBootstrap: Manager POV target missing head transform.");
                    _loggedMissingManagerOnce = true;
                }
            }
        }
        else
        {
            _currentManager = null;
            _currentManagerIndex = -1;
        }

        if (hasAnchor)
            return;

        _loggedMissingManagerOnce = false;

        var ballPosition = smoothedBallGround;

        Vector3 fieldForward = Vector3.forward;
        Vector3 fieldRight = Vector3.right;
        if (_matchBuilder != null)
        {
            fieldForward = _matchBuilder.forward;
            fieldRight = _matchBuilder.right;
        }

        fieldForward.y = 0f;
        fieldRight.y = 0f;

        if (fieldForward.sqrMagnitude < 0.0001f)
            fieldForward = Vector3.forward;
        else
            fieldForward.Normalize();

        if (fieldRight.sqrMagnitude < 0.0001f)
            fieldRight = Vector3.right;
        else
            fieldRight.Normalize();

        const float sidelineSign = -1f;
        var lateralOffset = fieldRight * ManagerSidelineOffset * sidelineSign;
        var backwardOffset = fieldForward * ManagerSidelineBackOffset;
        var cameraPos = ballPosition + lateralOffset - backwardOffset + Vector3.up * ManagerSidelineHeight;

        _customCamera.transform.position = cameraPos;

        var lookTargetFallback = ballPosition + Vector3.up * ManagerLookHeightOffset;
        var lookDir = lookTargetFallback - cameraPos;
        if (lookDir.sqrMagnitude < 0.0001f)
            lookDir = fieldForward;

        _customCamera.transform.rotation = Quaternion.LookRotation(lookDir.normalized, Vector3.up);
    }

    private bool IsMatch3DViewActive()
    {
        EnsureBallReference();
        var now = Time.unscaledTime;
        var hadBallReference = _ball != null;

        if (_ball != null)
        {
            var ballObject = _ball.gameObject;
            if (ballObject == null)
            {
                _ball = null;
            }
            else
            {
                var isActive = ballObject.activeSelf && ballObject.activeInHierarchy;
                if (isActive)
                {
                    _ballLastActiveTime = now;
                    return true;
                }

                if (_hasSeenBall)
                {
                    if (now - _ballLastActiveTime <= BallStateGraceSeconds)
                        return _lastMatch3DActive;
                    return false;
                }
            }
        }

        if (_ball == null && hadBallReference)
        {
            if (_hasSeenBall)
            {
                if (now - _ballLastActiveTime <= BallStateGraceSeconds)
                    return _lastMatch3DActive;
                return false;
            }
        }

        if (_hasSeenBall)
            return false;

        if (_matchBuilder == null)
        {
            var builderGo = GameObject.Find(MatchBuilderRoot);
            _matchBuilder = builderGo != null ? builderGo.transform : null;
        }

        if (_matchBuilder == null)
            return false;

        var builderObject = _matchBuilder.gameObject;
        if (builderObject == null)
        {
            _matchBuilder = null;
            return false;
        }

        return builderObject.activeInHierarchy;
    }

    private void RememberCurrentCustomState()
    {
        if (!_customCameraActive || _cameraMode == CameraMode.None)
        {
            _pendingRestoreState = default;
            return;
        }

        _pendingRestoreState = new CustomCameraState
        {
            HasState = true,
            Mode = _cameraMode,
            AutoFollowBallCarrier = _autoFollowBallCarrier,
            PlayerInstanceId = _currentPlayer?.InstanceId ?? -1,
            ManagerInstanceId = _currentManager?.InstanceId ?? -1
        };
    }

    private void RestorePendingCustomState()
    {
        var state = _pendingRestoreState;
        _pendingRestoreState = default;
        _restoreOnNext3D = false;

        if (!state.HasState)
            return;

        try
        {
            switch (state.Mode)
            {
                case CameraMode.PlayerPov:
                    if (!state.AutoFollowBallCarrier && state.PlayerInstanceId > 0)
                        TryRestorePlayerSelection(state.PlayerInstanceId, forceRefresh: true);

                    ActivatePlayerPov(state.AutoFollowBallCarrier);

                    if (!state.AutoFollowBallCarrier && state.PlayerInstanceId > 0)
                        TryRestorePlayerSelection(state.PlayerInstanceId, forceRefresh: false);
                    break;

                case CameraMode.ManagerPov:
                    if (state.ManagerInstanceId > 0)
                        TryRestoreManagerSelection(state.ManagerInstanceId, forceRefresh: true);

                    ActivateManagerPov();

                    if (state.ManagerInstanceId > 0)
                        TryRestoreManagerSelection(state.ManagerInstanceId, forceRefresh: false);
                    break;

                default:
                    break;
            }

            var restored = _customCameraActive && _cameraMode == state.Mode && state.Mode != CameraMode.None;
            if (restored)
                ArthurRayPovMod.Log?.LogInfo($"ArthurRayPovBootstrap: Restored {state.Mode} POV after 3D view returned.");
            else
                ArthurRayPovMod.Log?.LogInfo("ArthurRayPovBootstrap: 3D view returned; keeping default camera.");
        }
        catch (Exception ex)
        {
            ArthurRayPovMod.Log?.LogWarning($"ArthurRayPovBootstrap: Failed to restore POV after 3D return: {ex}");
        }
    }

    private void TryRestorePlayerSelection(int instanceId, bool forceRefresh)
    {
        if (instanceId <= 0)
            return;

        EnsurePlayerList(forceRefresh);

        if (_players.Count == 0)
            return;

        var index = _players.FindIndex(p => p.InstanceId == instanceId);
        if (index < 0)
            return;

        _currentPlayerIndex = index;
        _currentPlayer = _players[index];
        _autoFollowBallCarrier = false;
    }

    private void TryRestoreManagerSelection(int instanceId, bool forceRefresh)
    {
        if (instanceId <= 0)
            return;

        EnsureManagerList(forceRefresh);

        if (_managers.Count == 0)
            return;

        var index = _managers.FindIndex(m => m.InstanceId == instanceId);
        if (index < 0)
            return;

        _currentManagerIndex = index;
        _currentManager = _managers[index];
        _loggedMissingManagerOnce = false;
    }

    private Vector3 GetStableBallGroundPosition(Vector3 fallback)
    {
        var groundY = _matchBuilder != null ? _matchBuilder.position.y : 0f;
        var fallbackGround = new Vector3(fallback.x, groundY, fallback.z);
        var deltaTime = Time.unscaledDeltaTime;
        var lerpFactor = deltaTime <= 0f ? 1f : 1f - Mathf.Exp(-deltaTime / ManagerBallFollowSmoothTime);

        if (_ball != null)
        {
            var target = _ball.position;
            target.y = groundY;

            if (!_hasSmoothedBall)
            {
                _smoothedBallPosition = target;
                _hasSmoothedBall = true;
                return _smoothedBallPosition;
            }

            _smoothedBallPosition = Vector3.Lerp(_smoothedBallPosition, target, lerpFactor);
            return _smoothedBallPosition;
        }

        if (!_hasSmoothedBall)
        {
            _smoothedBallPosition = fallbackGround;
            _hasSmoothedBall = true;
            return _smoothedBallPosition;
        }

        _smoothedBallPosition = Vector3.Lerp(_smoothedBallPosition, fallbackGround, lerpFactor);
        return _smoothedBallPosition;
    }

    private void ClearPendingRestore()
    {
        _restoreOnNext3D = false;
        _pendingRestoreState = default;
    }

    private void EnsureBallReference()
    {
        if (_ball != null)
        {
            var ballObject = _ball.gameObject;
            if (ballObject == null)
            {
                _ball = null;
            }
            else if (!_hasSeenBall)
            {
                _hasSeenBall = true;
                if (ballObject.activeSelf && ballObject.activeInHierarchy)
                    _ballLastActiveTime = Time.unscaledTime;
            }
        }

        if (_ball == null)
        {
            GameObject ballGo = null;
            foreach (var path in BallObjectPaths)
            {
                ballGo = GameObject.Find(path);
                if (ballGo != null)
                    break;
            }

            if (ballGo == null)
                ballGo = FindBallInMatchBuilder();

            _ball = ballGo != null ? ballGo.transform : null;
            if (_ball != null)
            {
                _hasSeenBall = true;
                _ballLastActiveTime = Time.unscaledTime;
            }
        }
    }

    private GameObject FindBallInMatchBuilder()
    {
        if (_matchBuilder == null)
        {
            var builderGo = GameObject.Find(MatchBuilderRoot);
            _matchBuilder = builderGo != null ? builderGo.transform : null;
        }

        if (_matchBuilder == null)
            return null;

        foreach (var transform in EnumerateHierarchy(_matchBuilder, includeInactive: true))
        {
            if (transform == null)
                continue;

            var name = transform.name;
            if (string.IsNullOrEmpty(name))
                continue;

            foreach (var candidate in BallObjectNames)
            {
                if (string.Equals(name, candidate, StringComparison.Ordinal))
                    return transform.gameObject;
            }

            if (name.StartsWith("BallPrefab", StringComparison.Ordinal))
                return transform.gameObject;
        }

        return null;
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

    private void EnsureManagerList(bool forceRefresh = false)
    {
        var now = Time.time;
        if (forceRefresh || _managers.Count == 0 || now >= _nextManagerScanTime)
        {
            _nextManagerScanTime = now + 2f;
            RefreshManagerList();
        }
        else
        {
            _managers.RemoveAll(m => !m.IsValid);
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

            var candidates = new List<Avatar>();
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

                var displayName = CleanDisplayName(root.gameObject.name);
                candidates.Add(new Avatar
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
            ArthurRayPovMod.Log?.LogError($"ArthurRayPovBootstrap: RefreshPlayerList failed: {ex}");
        }
    }

    private void RefreshManagerList()
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

            var candidates = new List<Avatar>();
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

                if (!IsLikelyManager(root))
                    continue;

                var instanceId = root.gameObject.GetInstanceID();
                if (!seen.Add(instanceId))
                    continue;
                var head = TryGetHead(animator);
                if (head == null)
                    head = FindHeadTransform(root);

                var displayName = CleanDisplayName(root.gameObject.name);
                candidates.Add(new Avatar
                {
                    Root = root,
                    Head = head,
                    DisplayName = displayName,
                    InstanceId = instanceId
                });
            }

            _managers.Clear();

            if (candidates.Count > 0)
            {
                candidates.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
                _managers.AddRange(candidates);

                if (_currentManager != null)
                {
                    var index = _managers.FindIndex(m => m.InstanceId == _currentManager.InstanceId);
                    if (index >= 0)
                    {
                        _currentManagerIndex = index;
                        _currentManager = _managers[index];
                    }
                    else
                    {
                        _currentManagerIndex = 0;
                        _currentManager = _managers[0];
                    }
                }
                else if (_managers.Count > 0)
                {
                    _currentManagerIndex = 0;
                    _currentManager = _managers[0];
                }
            }
            else
            {
                _currentManager = null;
                _currentManagerIndex = -1;
            }
        }
        catch (Exception ex)
        {
            ArthurRayPovMod.Log?.LogError($"ArthurRayPovBootstrap: RefreshManagerList failed: {ex}");
        }
    }

    private Avatar FindClosestPlayerToBall()
    {
        if (_ball == null || _players.Count == 0)
            return null;

        var ballPosition = _ball.position;
        Avatar closest = null;
        var bestDistance = float.MaxValue;

        foreach (var player in _players)
        {
            if (!player.IsValid)
                continue;

            var head = GetAvatarHeadPosition(player);
            var distance = (head - ballPosition).sqrMagnitude;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                closest = player;
            }
        }

        return closest;
    }

    private void SetCurrentPlayer(Avatar candidate)
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

    private Vector3 GetAvatarHeadPosition(Avatar player)
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

    private bool IsLikelyManager(Transform candidate)
    {
        if (candidate == null)
            return false;

        if (MatchesManagerKeyword(candidate.name))
            return true;

        var parent = candidate.parent;
        var depth = 0;
        while (parent != null && depth < 4)
        {
            if (MatchesManagerKeyword(parent.name))
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

    private bool MatchesManagerKeyword(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        var lower = value.ToLowerInvariant();
        return lower.Contains("manager") ||
               lower.Contains("coach") ||
               lower.Contains("staff") ||
               lower.Contains("gaffer") ||
               lower.Contains("assistant") ||
               lower.Contains("technical") ||
               lower.Contains("touchline") ||
               lower.Contains("sideline");
    }

    private string CleanDisplayName(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "Unknown Person";

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
        // Draw REC indicator when auto-trigger is enabled and in match 3D view
        if (_autoTriggerEnabled && IsMatch3DViewActive())
        {
            if (_recStyle == null)
            {
                _recStyle = new GUIStyle();
                if (GUI.skin != null && GUI.skin.label != null)
                {
                    _recStyle.font = GUI.skin.label.font;
                    _recStyle.richText = GUI.skin.label.richText;
                }
                _recStyle.fontSize = 18;
                _recStyle.alignment = TextAnchor.UpperRight;
                _recStyle.fontStyle = FontStyle.Bold;
                _recStyle.normal.textColor = Color.red;
            }

            float recWidth = 70f;
            float recX = Screen.width * 0.75f;
            Rect recRect = new Rect(recX, 10f, recWidth, 25f);
            GUI.Label(recRect, "● REC", _recStyle);
        }

        if (!_customCameraActive)
            return;

        string label = null;
        if (_cameraMode == CameraMode.PlayerPov)
        {
            if (_currentPlayer != null && !string.IsNullOrEmpty(_currentPlayer.DisplayName))
            {
                label = _currentPlayer.DisplayName;
            }
            else
            {
                EnsureBallReference();
                EnsurePlayerList();
                var carrier = FindClosestPlayerToBall();
                if (carrier != null && !string.IsNullOrEmpty(carrier.DisplayName))
                    label = carrier.DisplayName;
            }
        }
        else if (_cameraMode == CameraMode.ManagerPov)
        {
            EnsureBallReference();
            EnsurePlayerList();
            var focus = FindClosestPlayerToBall();
            if (focus != null && !string.IsNullOrEmpty(focus.DisplayName))
                label = focus.DisplayName;
        }

        if (!string.IsNullOrEmpty(label))
        {
            var canonical = label.Trim();
            var lower = canonical.ToLowerInvariant();
            var squashed = lower.Replace(" ", string.Empty);
            if (lower.Contains("player 3d") ||
                lower.Contains("player-3d") ||
                lower.Contains("player_3d") ||
                squashed.Contains("player3d"))
            {
                return;
            }
        }

        if (string.IsNullOrEmpty(label))
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
            _hudStyle.alignment = TextAnchor.UpperRight;
            _hudStyle.fontStyle = FontStyle.Bold;
            _hudStyle.normal.textColor = Color.white;
        }

        var width = 280f;
        var rectX = Mathf.Max(Screen.width * 0.75f - width, 0f);
        var rect = new Rect(rectX, 20f, width, 28f);
        GUI.Label(rect, label, _hudStyle);
    }
    
    private void CheckMatchEventsAndAutoTrigger()
    {
        try
        {
            // Try to get the MatchEventList component
            if (_matchEventList == null)
            {
                if (!TryInitializeEventReflection())
                    return;

                if (_matchBuilder == null)
                {
                    var builderGo = GameObject.Find(MatchBuilderRoot);
                    _matchBuilder = builderGo != null ? builderGo.transform : null;
                }

                if (_matchBuilder == null)
                    return;

                // Try to find MatchEventList in the scene
                var matchEventListGo = GameObject.Find("MatchPlaybackController/MatchComponents");
                if (matchEventListGo != null)
                {
                    var components = matchEventListGo.GetComponents<Component>();
                    foreach (var component in components)
                    {
                        if (component != null && component.GetType().Name == "MatchEventList")
                        {
                            _matchEventList = component;
                            break;
                        }
                    }
                }

                if (_matchEventList == null)
                    return;
            }

            // Get the list of match events
            var eventDataList = _matchEventDataProperty.GetValue(_matchEventList, null);
            if (eventDataList == null)
                return;

            var listType = eventDataList.GetType();
            var countProperty = listType.GetProperty("Count");
            if (countProperty == null)
                return;

            int count = (int)countProperty.GetValue(eventDataList, null);
            if (count == 0)
                return;

            // Check for new events since last frame
            var getItemMethod = listType.GetMethod("get_Item");
            if (getItemMethod == null)
                return;

            // Iterate through events starting from the last processed
            for (int i = count - 1; i >= 0; i--)
            {
                var eventData = getItemMethod.Invoke(eventDataList, new object[] { i });
                if (eventData == null)
                    continue;

                var matchFrame = (int)_matchFrameProperty.GetValue(eventData, null);

                // Skip if we've already processed this frame
                if (matchFrame <= _lastProcessedEventFrame)
                    break;

                var eventType = (int)_matchEventTypeProperty.GetValue(eventData, null);

                // Check if this is an auto-trigger event
                // FreeKick=2, CornerKick=3, PenaltyKick=4, Goal=0
                if (eventType == 2 || eventType == 3 || eventType == 4 || eventType == 0)
                {
                    _lastProcessedEventFrame = matchFrame;

                    string eventName = eventType switch
                    {
                        0 => "Goal",
                        2 => "Free Kick",
                        3 => "Corner Kick",
                        4 => "Penalty Kick",
                        _ => "Event"
                    };

                    ArthurRayPovMod.Log?.LogInfo($"ArthurRayPovBootstrap: Auto-triggering POV for {eventName} at frame {matchFrame}");

                    // Save match speed and slow down
                    SaveAndSlowMatchSpeed();

                    // Store event timing for completion detection
                    _eventStartTimeSlice = matchFrame;
                    _eventCompletionTimeout = Time.time;
                    _currentSetPieceType = eventType; // Store as int for now
                    _autoModeCameraActive = true;

                    // For goals, try to find the goal scorer specifically
                    if (eventType == 0)
                    {
                        TrySelectGoalScorer();
                    }
                    else
                    {
                        // For other events, follow ball carrier
                        ActivatePlayerPov(autoFollowBallCarrier: true);
                    }
                    break;
                }

                _lastProcessedEventFrame = matchFrame;
            }
        }
        catch (Exception ex)
        {
            ArthurRayPovMod.Log?.LogError($"ArthurRayPovBootstrap: CheckMatchEventsAndAutoTrigger failed: {ex}");
        }
    }

    private bool TryInitializeEventReflection()
    {
        if (_tMatchEventList != null && _tMatchEventData != null && _tMatchEventType != null)
            return true;

        try
        {
            // Try to load FM.Match assembly types via reflection
            var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                if (assembly.GetName().Name.Contains("FM.Match") || assembly.FullName.Contains("FM.Match"))
                {
                    _tMatchEventList = assembly.GetType("FM.Match.MatchEventList");
                    _tMatchEventData = assembly.GetType("FM.Match.MatchEventData");
                    _tMatchEventType = assembly.GetType("FM.Match.MatchEventType");
                    _tMatch = assembly.GetType("FM.Match.Match");

                    if (_tMatchEventList != null && _tMatchEventData != null && _tMatchEventType != null)
                        break;
                }
            }

            // Fallback: try to get types by full name
            if (_tMatchEventList == null)
                _tMatchEventList = System.Type.GetType("FM.Match.MatchEventList, FM.Match");
            if (_tMatchEventData == null)
                _tMatchEventData = System.Type.GetType("FM.Match.MatchEventData, FM.Match");
            if (_tMatchEventType == null)
                _tMatchEventType = System.Type.GetType("FM.Match.MatchEventType, FM.Match");
            if (_tMatch == null)
                _tMatch = System.Type.GetType("FM.Match.Match, FM.Match");

            if (_tMatchEventList == null || _tMatchEventData == null || _tMatchEventType == null)
                return false;

            _matchEventDataProperty = _tMatchEventList.GetProperty("MatchEventData");
            _matchFrameProperty = _tMatchEventData.GetProperty("MatchFrame");
            _matchEventTypeProperty = _tMatchEventData.GetProperty("MatchEventType");

            return _matchEventDataProperty != null && _matchFrameProperty != null && _matchEventTypeProperty != null;
        }
        catch (Exception ex)
        {
            ArthurRayPovMod.Log?.LogWarning($"ArthurRayPovBootstrap: Failed to initialize event reflection: {ex.Message}");
            return false;
        }
    }

    private bool IsUserTeamEvent(object eventData)
    {
        try
        {
            if (_matchObject == null || eventData == null)
                return false;

            // Get UserTeamId from Match object (0 = Home, 1 = Away)
            var userTeamIdProperty = _matchObject.GetType().GetProperty("UserTeamId");
            if (userTeamIdProperty == null)
                return false;

            sbyte userTeamId = (sbyte)userTeamIdProperty.GetValue(_matchObject, null);

            // Get player lists from event data
            var homePlayersField = eventData.GetType().GetField("m_homePlayersInvolved",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var awayPlayersField = eventData.GetType().GetField("m_awayPlayersInvolved",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (homePlayersField == null || awayPlayersField == null)
                return false;

            var homePlayers = homePlayersField.GetValue(eventData);
            var awayPlayers = awayPlayersField.GetValue(eventData);

            // Check count via reflection
            var homeCountProperty = homePlayers.GetType().GetProperty("Count");
            var awayCountProperty = awayPlayers.GetType().GetProperty("Count");

            int homeCount = (int)homeCountProperty.GetValue(homePlayers, null);
            int awayCount = (int)awayCountProperty.GetValue(awayPlayers, null);

            // If user is home team and event has home players, it's user's team event
            if (userTeamId == 0 && homeCount > 0)
                return true;

            // If user is away team and event has away players, it's user's team event
            if (userTeamId == 1 && awayCount > 0)
                return true;

            return false;
        }
        catch (Exception ex)
        {
            ArthurRayPovMod.Log?.LogError($"ArthurRayPovBootstrap: IsUserTeamEvent failed: {ex.Message}");
            return false;
        }
    }

    private bool TryInitializeMatchPreferences()
    {
        try
        {
            if (_matchPreferencesObject != null && _getMatchSpeedMethod != null && _setMatchSpeedMethod != null)
                return true;

            if (_matchObject == null)
                return false;

            // Get MatchPreferences from Match object
            var matchPrefsField = _matchObject.GetType().GetField("m_matchPreferences",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (matchPrefsField == null)
                return false;

            _matchPreferencesObject = matchPrefsField.GetValue(_matchObject);
            if (_matchPreferencesObject == null)
                return false;

            var prefsType = _matchPreferencesObject.GetType();
            _getMatchSpeedMethod = prefsType.GetMethod("get_MatchSpeedDuringHighlights");
            _setMatchSpeedMethod = prefsType.GetMethod("set_MatchSpeedDuringHighlights");

            return _getMatchSpeedMethod != null && _setMatchSpeedMethod != null;
        }
        catch (Exception ex)
        {
            ArthurRayPovMod.Log?.LogWarning($"ArthurRayPovBootstrap: Failed to initialize MatchPreferences: {ex.Message}");
            return false;
        }
    }

    private void SaveAndSlowMatchSpeed()
    {
        try
        {
            if (!TryInitializeMatchPreferences())
                return;

            // Save current speed
            _originalMatchSpeed = (float)_getMatchSpeedMethod.Invoke(_matchPreferencesObject, null);

            // Set to 0.8
            _setMatchSpeedMethod.Invoke(_matchPreferencesObject, new object[] { 0.8f });

            ArthurRayPovMod.Log?.LogInfo($"ArthurRayPovBootstrap: Match speed slowed to 0.8 (was {_originalMatchSpeed})");
        }
        catch (Exception ex)
        {
            ArthurRayPovMod.Log?.LogError($"ArthurRayPovBootstrap: Failed to slow match speed: {ex.Message}");
        }
    }

    private void RestoreMatchSpeed()
    {
        try
        {
            if (!TryInitializeMatchPreferences())
                return;

            // Restore original speed
            _setMatchSpeedMethod.Invoke(_matchPreferencesObject, new object[] { _originalMatchSpeed });

            ArthurRayPovMod.Log?.LogInfo($"ArthurRayPovBootstrap: Match speed restored to {_originalMatchSpeed}");
        }
        catch (Exception ex)
        {
            ArthurRayPovMod.Log?.LogError($"ArthurRayPovBootstrap: Failed to restore match speed: {ex.Message}");
        }
    }

    private bool TryInitializeStopTimeInfoReflection()
    {
        try
        {
            if (_stopTimeInfosField != null && _tStopTimeInfo != null)
                return true;

            if (_matchObject == null)
                return false;

            // Get m_stopTimeInfos field from Match
            _stopTimeInfosField = _matchObject.GetType().GetField("m_stopTimeInfos",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (_stopTimeInfosField == null)
                return false;

            // Get StopTimeInfo type
            var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                if (assembly.FullName.Contains("SI.Match") || assembly.GetName().Name.Contains("SI.Match"))
                {
                    _tStopTimeInfo = assembly.GetType("SI.Match.StopTimeInfo");
                    if (_tStopTimeInfo != null)
                        break;
                }
            }

            if (_tStopTimeInfo == null)
                _tStopTimeInfo = System.Type.GetType("SI.Match.StopTimeInfo, SI.Match");

            if (_tStopTimeInfo == null)
                return false;

            _stopTimeSliceField = _tStopTimeInfo.GetField("StopTimeSlice");
            _setPieceTypeField = _tStopTimeInfo.GetField("SetPieceType");

            return _stopTimeSliceField != null && _setPieceTypeField != null;
        }
        catch (Exception ex)
        {
            ArthurRayPovMod.Log?.LogWarning($"ArthurRayPovBootstrap: Failed to initialize StopTimeInfo reflection: {ex.Message}");
            return false;
        }
    }

    private bool IsEventComplete()
    {
        try
        {
            if (!_autoModeCameraActive)
                return false;

            // Timeout check (5 seconds)
            if (Time.time - _eventCompletionTimeout > EventTimeoutSeconds)
            {
                ArthurRayPovMod.Log?.LogInfo("ArthurRayPovBootstrap: Event timed out after 5 seconds");
                return true;
            }

            if (_matchObject == null)
                return false;

            // Get current time slice
            var currentTimeProperty = _matchObject.GetType().GetProperty("CurrentTimeSlice");
            if (currentTimeProperty == null)
                return false;

            int currentTime = (int)currentTimeProperty.GetValue(_matchObject, null);

            // Check if enough time has passed (2 seconds = ~120 time slices at 60fps)
            if (currentTime - _eventStartTimeSlice > 120)
            {
                ArthurRayPovMod.Log?.LogInfo($"ArthurRayPovBootstrap: Event completed after {currentTime - _eventStartTimeSlice} time slices");
                return true;
            }

            // Try to check StopTimeInfo for set piece completion
            if (!TryInitializeStopTimeInfoReflection())
                return false;

            var stopInfosList = _stopTimeInfosField.GetValue(_matchObject);
            if (stopInfosList == null)
                return false;

            var listType = stopInfosList.GetType();
            var countProperty = listType.GetProperty("Count");
            if (countProperty == null)
                return false;

            int count = (int)countProperty.GetValue(stopInfosList, null);
            if (count == 0)
                return false;

            var getItemMethod = listType.GetMethod("get_Item");
            if (getItemMethod == null)
                return false;

            // Check if a new set piece has started (indicates previous is complete)
            for (int i = count - 1; i >= 0; i--)
            {
                var stopInfo = getItemMethod.Invoke(stopInfosList, new object[] { i });
                if (stopInfo == null)
                    continue;

                int stopTimeSlice = (int)_stopTimeSliceField.GetValue(stopInfo);
                byte setPieceType = (byte)_setPieceTypeField.GetValue(stopInfo);

                // If we found a newer stop time with different set piece type, previous is complete
                if (stopTimeSlice > _eventStartTimeSlice && setPieceType != _currentSetPieceType)
                {
                    ArthurRayPovMod.Log?.LogInfo($"ArthurRayPovBootstrap: New set piece detected (type {setPieceType}), previous event complete");
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            ArthurRayPovMod.Log?.LogError($"ArthurRayPovBootstrap: IsEventComplete failed: {ex.Message}");
            // On error, assume event is complete to avoid getting stuck
            return true;
        }
    }

    private void TrySelectGoalScorer()
    {
        try
        {
            // Try to find the goal scorer from MatchObjects
            if (_matchObject == null)
            {
                // Fallback to ball carrier if Match object not found
                ActivatePlayerPov(autoFollowBallCarrier: true);
                return;
            }

            var matchObjectsProperty = _matchObject.GetType().GetProperty("MatchObjects");
            if (matchObjectsProperty == null)
            {
                ActivatePlayerPov(autoFollowBallCarrier: true);
                return;
            }

            var matchObjects = matchObjectsProperty.GetValue(_matchObject, null);
            if (matchObjects == null)
            {
                ActivatePlayerPov(autoFollowBallCarrier: true);
                return;
            }

            var goalScorerProperty = matchObjects.GetType().GetProperty("GoalScorer");
            if (goalScorerProperty == null)
            {
                ActivatePlayerPov(autoFollowBallCarrier: true);
                return;
            }

            var goalScorer = goalScorerProperty.GetValue(matchObjects, null);
            if (goalScorer == null)
            {
                ArthurRayPovMod.Log?.LogInfo("ArthurRayPovBootstrap: No goal scorer found, falling back to ball carrier");
                ActivatePlayerPov(autoFollowBallCarrier: true);
                return;
            }

            // Try to get the entity ID or instance ID of the goal scorer
            var entityIdProperty = goalScorer.GetType().GetProperty("EntityID");
            if (entityIdProperty == null)
                entityIdProperty = goalScorer.GetType().GetProperty("EntityId");

            if (entityIdProperty != null)
            {
                var entityId = (int)entityIdProperty.GetValue(goalScorer, null);
                ArthurRayPovMod.Log?.LogInfo($"ArthurRayPovBootstrap: Found goal scorer with EntityID {entityId}");

                // Activate player POV and try to find the scorer in our player list
                ActivatePlayerPov(autoFollowBallCarrier: false);

                // Try to find the scorer by matching entity ID with player GameObject name
                EnsurePlayerList(forceRefresh: true);

                foreach (var player in _players)
                {
                    if (player.IsValid && player.DisplayName.Contains(entityId.ToString()))
                    {
                        SetCurrentPlayer(player);
                        ArthurRayPovMod.Log?.LogInfo($"ArthurRayPovBootstrap: Selected goal scorer: {player.DisplayName}");
                        return;
                    }
                }

                // If we can't find by ID, just follow ball carrier (scorer likely has the ball anyway)
                ArthurRayPovMod.Log?.LogInfo("ArthurRayPovBootstrap: Could not match scorer to player, following ball carrier");
                ActivatePlayerPov(autoFollowBallCarrier: true);
            }
            else
            {
                // No entity ID, just follow ball carrier
                ActivatePlayerPov(autoFollowBallCarrier: true);
            }
        }
        catch (Exception ex)
        {
            ArthurRayPovMod.Log?.LogError($"ArthurRayPovBootstrap: TrySelectGoalScorer failed: {ex.Message}");
            // Fallback to ball carrier
            ActivatePlayerPov(autoFollowBallCarrier: true);
        }
    }

    private void CheckReplayStateAndAutoTrigger()
    {
        try
        {
            // Cooldown to prevent rapid re-triggering
            if (_replayDetectionCooldown > 0f)
            {
                _replayDetectionCooldown -= Time.unscaledDeltaTime;
                return;
            }

            // Try to get Match object if not already cached
            if (_matchObject == null && _tMatch != null)
            {
                var matchPlaybackController = GameObject.Find("MatchPlaybackController");
                if (matchPlaybackController != null)
                {
                    var matchProperty = matchPlaybackController.GetType().GetProperty("Match");
                    if (matchProperty != null)
                    {
                        _matchObject = matchProperty.GetValue(matchPlaybackController.GetComponent<Component>(), null);
                    }
                }

                if (_matchObject == null)
                    return;
            }

            if (_matchObject == null)
                return;

            // Check for replay state by looking at playback speed/time manipulation
            // Replays typically change the playback state
            var playbackInterfaceProp = _matchObject.GetType().GetProperty("PlaybackInterface");
            if (playbackInterfaceProp != null)
            {
                var playbackInterface = playbackInterfaceProp.GetValue(_matchObject, null);
                if (playbackInterface != null)
                {
                    // Check if currently in replay mode
                    var isInReplayMethod = playbackInterface.GetType().GetMethod("IsInReplay");
                    if (isInReplayMethod != null)
                    {
                        bool isInReplay = (bool)isInReplayMethod.Invoke(playbackInterface, null);

                        // Trigger POV when replay starts (transition from false to true)
                        if (isInReplay && !_wasInReplay)
                        {
                            ArthurRayPovMod.Log?.LogInfo("ArthurRayPovBootstrap: Replay started - auto-triggering POV");
                            ActivatePlayerPov(autoFollowBallCarrier: true);
                            _replayDetectionCooldown = 2f; // 2 second cooldown
                        }

                        _wasInReplay = isInReplay;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Silent failure - replay detection is optional
            if (ex.Message.Contains("IsInReplay"))
            {
                // Method doesn't exist, disable replay detection
                _replayDetectionCooldown = 999999f;
            }
        }
    }

    private bool EnableCustomCamera()
    {
        string previousCustomTag = null;
        try
        {
            if (_customCamera != null)
            {
                previousCustomTag = _customCamera.tag;
                if (_customCamera.tag == "MainCamera")
                {
                    _customCamera.tag = "Untagged";
                }
            }

            // NOTE: IL2CPP runtime used by FM26 strips many reflection helpers
            // (FindObjectsOfType<T>, Camera.allCameras, Scene.GetRootGameObjects, etc.).
            // Always rely on the simple Camera.main/tag lookup to avoid MissingMethodException.
            var mainCameraCandidate = Camera.main;
            if (mainCameraCandidate == _customCamera)
            {
                mainCameraCandidate = null;
            }

            if (mainCameraCandidate == null)
            {
                var mainTagged = GameObject.FindWithTag("MainCamera");
                if (mainTagged != null)
                {
                    var cam = SafeGetComponent<Camera>(mainTagged.transform);
                    if (cam != null && cam != _customCamera)
                        mainCameraCandidate = cam;
                }
            }

            if (mainCameraCandidate == null && _lastMainCamera != null && _lastMainCamera != _customCamera)
            {
                mainCameraCandidate = _lastMainCamera;
            }

            _lastMainCamera = mainCameraCandidate;
            EnsureBallReference();

            if (_customCamera == null)
            {
                var go = new GameObject("ArthurRayCam_Custom");
                _customCamera = go.AddComponent<Camera>();
                DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.tag = "MainCamera";
            }
            else
            {
                _customCamera.tag = "MainCamera";
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
                _originalMainTag = _lastMainCamera.tag;

                try
                {
                    _lastMainCamera.tag = "MainCamera";
                    _lastMainCamera.enabled = false;
                }
                catch
                {
                }
            }

            _customCamera.gameObject.SetActive(true);
            _customCamera.enabled = true;
            _customCameraActive = true;
            return true;
        }
        catch (Exception ex)
        {
            if (_customCamera != null && previousCustomTag != null)
            {
                _customCamera.tag = previousCustomTag;
            }
            ArthurRayPovMod.Log?.LogError($"ArthurRayPovBootstrap: EnableCustomCamera failed: {ex}");
            _customCameraActive = false;
            return false;
        }
    }

    private void DisableCustomCamera(bool dueToSceneChange)
    {
        try
        {
            if (dueToSceneChange)
                ClearPendingRestore();

            if (_customCamera != null)
            {
                _customCamera.enabled = false;
                _customCamera.gameObject.SetActive(false);
                _customCamera.transform.SetParent(null, worldPositionStays: true);
                _customCamera.tag = "Untagged";
            }

            var cameraToRestore = _lastMainCamera;
            var restoredEnabled = _originalMainEnabled;
            var restoredTag = string.IsNullOrEmpty(_originalMainTag) ? "MainCamera" : _originalMainTag;

            if (cameraToRestore == null)
            {
                cameraToRestore = Camera.main;
                if (cameraToRestore == _customCamera)
                    cameraToRestore = null;

                if (cameraToRestore == null)
                {
                    var mainTagged = GameObject.FindWithTag("MainCamera");
                    if (mainTagged != null)
                    {
                        var candidate = SafeGetComponent<Camera>(mainTagged.transform);
                        if (candidate != null && candidate != _customCamera)
                            cameraToRestore = candidate;
                    }
                }

                if (cameraToRestore != null)
                {
                    restoredEnabled = true;
                    restoredTag = "MainCamera";
                }
            }

            if (cameraToRestore != null)
            {
                try
                {
                    cameraToRestore.gameObject.SetActive(true);
                    cameraToRestore.tag = restoredTag;
                    cameraToRestore.enabled = true;
                }
                catch
                {
                }
            }
            else
            {
                ArthurRayPovMod.Log?.LogWarning("ArthurRayPovBootstrap: Unable to locate a main camera to restore after disabling custom camera.");
            }

            _customCameraActive = false;
            _cameraMode = CameraMode.None;
            _autoFollowBallCarrier = false;
            _currentPlayer = null;
            _currentPlayerIndex = -1;
            _currentManager = null;
            _currentManagerIndex = -1;
            _loggedMissingManagerOnce = false;

            if (dueToSceneChange)
                ArthurRayPovMod.Log?.LogInfo("ArthurRayPovBootstrap: Scene change detected - custom camera deactivated.");
            else
                ArthurRayPovMod.Log?.LogInfo("ArthurRayPovBootstrap: Custom camera deactivated; restored game camera.");
        }
        catch (Exception ex)
        {
            ArthurRayPovMod.Log?.LogError($"ArthurRayPovBootstrap: DisableCustomCamera failed: {ex}");
        }
        finally
        {
            _ball = null;
            _lastMainCamera = null;
            _matchBuilder = null;
            _originalMainTag = "MainCamera";
            _players.Clear();
            _managers.Clear();
            _hasSmoothedBall = false;
            _smoothedBallPosition = Vector3.zero;

            // Reset event tracking state
            _matchEventList = null;
            _matchObject = null;
            _lastProcessedEventFrame = -1;
            _wasInReplay = false;
            _replayDetectionCooldown = 0f;
        }
    }
}
